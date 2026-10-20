using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Adls;

// Cadena de búsqueda del {entity}-model.json, común a AdlsClient y LocalFolderClient: el snapshot, el
// sellado, cualquier otro snapshot y la raíz. El ingest la recorre por cada entidad y snapshot, y
// listar las carpetas de snapshots es la llamada cara, así que solo se lista si los dos primeros fallan.
internal static class CdmSchemaLocator
{
    private const string CdmFolder = "Microsoft.Athena.TrickleFeedService";

    // read: el contenido del fichero, o null si no existe. listSnapshots: las carpetas de snapshots.
    public static async Task<CdmEntitySchema> FindAsync(
        string entity,
        string snapshot,
        string? settledSnapshot,
        Func<string, Task<string?>> read,
        Func<Task<IReadOnlyList<string>>> listSnapshots)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<CdmEntitySchema?> TryAsync(string candidate)
        {
            if (!tried.Add(candidate))
            {
                return null;
            }

            var json = await read(candidate);
            return json is null ? null : CdmSchemaParser.Parse(entity, json);
        }

        var schema = await TryAsync($"{snapshot}/{CdmFolder}/{entity}-model.json");
        if (schema is null && !string.IsNullOrWhiteSpace(settledSnapshot))
        {
            schema = await TryAsync($"{settledSnapshot}/{CdmFolder}/{entity}-model.json");
        }

        if (schema is not null)
        {
            return schema;
        }

        foreach (var folder in await listSnapshots())
        {
            schema = await TryAsync($"{folder}/{CdmFolder}/{entity}-model.json");
            if (schema is not null)
            {
                return schema;
            }
        }

        return await TryAsync($"{CdmFolder}/{entity}-model.json")
            ?? throw new InvalidOperationException($"CDM schema not found for entity '{entity}'.");
    }
}
