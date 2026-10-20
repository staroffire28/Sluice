using System.Text.Json;

namespace Sluice.Portal.Services.Adls;

// Completitud de un snapshot de Synapse Link según el model.json (CDM) de su raíz. Synapse declara
// ahí todas las tablas del perfil, pero solo las que cambiaron en el intervalo llevan "partitions",
// y cada partition apunta (URL completa) a un CSV de la carpeta. El snapshot está completo si cada
// partition declarada existe y no está vacía; las tablas sin partitions no cuentan. Visto en un
// volcado real (2026-10): ningún CSV de datos pesa 0 bytes y los .csv.lck siguen tras cerrar la
// carpeta, así que no indican escritura en curso. Comparte el criterio AdlsClient y LocalFolderClient.
public static class SnapshotManifest
{
    public const string ModelFile = "model.json";

    // modelJson: contenido del model.json de la raíz, o null si no existe. hasEntityFolders: la carpeta
    // tiene carpetas de tabla (sin contar las de sistema). sizeOf: tamaño del fichero en la ruta
    // relativa a la carpeta del snapshot ("tabla/1.csv"), o null si no existe.
    public static SnapshotIntegrity Evaluate(
        string snapshot, string? modelJson, bool hasEntityFolders, Func<string, long?> sizeOf)
    {
        // Sin model.json y sin tablas: la carpeta vacía que Synapse crea a veces al reintentar. Con
        // tablas pero sin model.json, la exportación no terminó (o no es una carpeta de Synapse Link).
        if (modelJson is null)
        {
            return hasEntityFolders
                ? Incomplete(null, $"Snapshot '{snapshot}' has table folders but no {ModelFile} (export not finished).")
                : Complete;
        }

        IReadOnlyList<(string Entity, string Location)> partitions;
        try
        {
            partitions = ReadPartitions(modelJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Incomplete(null, $"Snapshot '{snapshot}' has an unreadable {ModelFile} ({ex.Message}).");
        }

        foreach (var (entity, location) in partitions)
        {
            if (RelativePath(snapshot, location) is not { } path)
            {
                return Incomplete(entity, $"Snapshot '{snapshot}': {ModelFile} declares a {entity} partition outside the snapshot folder ({location}).");
            }

            switch (sizeOf(path))
            {
                case null:
                    return Incomplete(entity, $"Snapshot '{snapshot}': {ModelFile} declares {path}, but the file does not exist (export not finished).");
                case 0:
                    return Incomplete(entity, $"Snapshot '{snapshot}': {ModelFile} declares {path}, but the file is empty (export not finished).");
            }
        }

        return Complete;
    }

    private static readonly SnapshotIntegrity Complete = new(true, null, null);

    private static SnapshotIntegrity Incomplete(string? entity, string reason) => new(false, entity, reason);

    // (tabla, location) de cada partition declarada. Lanza JsonException, o InvalidOperationException
    // si un elemento no tiene el tipo esperado, cuando el fichero no es un model.json.
    private static List<(string Entity, string Location)> ReadPartitions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("no 'entities' array");
        }

        var partitions = new List<(string, string)>();
        foreach (var entity in entities.EnumerateArray())
        {
            if (!entity.TryGetProperty("partitions", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var name = entity.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            foreach (var partition in list.EnumerateArray())
            {
                var location = partition.TryGetProperty("location", out var l) ? l.GetString() : null;
                if (string.IsNullOrWhiteSpace(location))
                {
                    throw new JsonException($"partition of '{name}' without location");
                }

                partitions.Add((name, location));
            }
        }

        return partitions;
    }

    // "https://cuenta.dfs.core.windows.net/container/{snapshot}/tabla/1.csv" → "tabla/1.csv". Solo URL
    // absolutas, que es lo que escribe Synapse Link. Null si no lo es o no cae dentro de la carpeta del
    // snapshot.
    internal static string? RelativePath(string snapshot, string location)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        var at = segments.FindIndex(s => string.Equals(s, snapshot, StringComparison.Ordinal));
        var relative = at >= 0 ? segments.Skip(at + 1).ToList() : null;
        return relative is { Count: > 0 } && !relative.Any(s => s == ".." || s.Contains('\\'))
            ? string.Join('/', relative)
            : null;
    }
}
