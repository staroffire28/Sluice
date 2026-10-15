using System.Globalization;
using System.Text.Json;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

// Una versión anterior del parquet consolidado de una tabla, con lo necesario para restaurarla:
// su schema y el estado de la tabla y del pipeline cuando era la vigente.
public sealed record ParquetVersion
{
    public string Id { get; init; } = "";
    public DateTimeOffset ArchivedAt { get; init; }
    public string? ReplacedByJobId { get; init; }
    public TableState? Table { get; init; }

    // LastProcessedSnapshot del pipeline cuando esta versión era la vigente: al restaurarla se
    // rebobina hasta aquí para que las filas posteriores vuelvan en la siguiente ejecución.
    public string? PipelineSnapshot { get; init; }
    public string? SchemaJson { get; init; }
}

// Versiones del consolidado. Antes de sustituirlo, la copia vigente se mueve (rename, sin copiar
// datos) a _versions/{pipeline}/{entity}/{id}.parquet con un sidecar {id}.json. Fuera de
// {pipeline}/ a propósito: la conciliación, Force y la página Parquet tratan como tabla cualquier
// .parquet bajo ese prefijo. El id es la fecha UTC de archivado y ordena cronológicamente.
public static class ParquetVersions
{
    public const string Root = "_versions";
    private const string IdFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    public static string Prefix(string pipelineId) => $"{Root}/{pipelineId}/";

    public static string EntityPrefix(string pipelineId, string entity) => $"{Root}/{pipelineId}/{entity}/";

    public static string ParquetPath(string pipelineId, string entity, string id) => $"{EntityPrefix(pipelineId, entity)}{id}.parquet";

    private static string SidecarPath(string parquetPath) => parquetPath[..^".parquet".Length] + ".json";

    private static string IdOf(string parquetPath) => Path.GetFileNameWithoutExtension(parquetPath);

    private static DateTimeOffset? ArchivedAtOf(string id) =>
        DateTimeOffset.TryParseExact(id, IdFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    // Publica el consolidado nuevo sin dejar nunca la única copia a medias: se sube con otro
    // nombre, se comprueba que llegó entero, la copia vigente pasa a versión y el nuevo ocupa su
    // sitio con un rename. Siempre se archiva (la poda decide luego cuántas quedan): así un fallo
    // entre los dos renames deja la copia anterior a salvo y detectable (HasVersionsAsync).
    public static async Task PublishAsync(
        IParquetStore store,
        string pipelineId,
        string entity,
        string localParquet,
        long expectedRows,
        string schemaJson,
        ParquetVersion archiveAs,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var current = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity);
        var uploading = current + ".uploading";
        await using (var input = File.OpenRead(localParquet))
        {
            await store.UploadParquetAsync(uploading, input, cancellationToken);
        }

        var uploaded = await store.CountParquetRowsAsync(uploading, cancellationToken: cancellationToken);
        if (uploaded != expectedRows)
        {
            await store.DeleteParquetAsync(uploading, cancellationToken);
            throw new InvalidOperationException(
                $"{entity}: el parquet subido tiene {uploaded:N0} filas y se esperaban {expectedRows:N0}; no se publica y se conserva el anterior.");
        }

        if (await store.ParquetExistsAsync(current, cancellationToken))
        {
            await ArchiveCurrentAsync(store, pipelineId, entity, archiveAs, now, cancellationToken);
        }

        await store.MoveAsync(uploading, current, cancellationToken);
        await store.UploadTextAsync(IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity), schemaJson, cancellationToken);
    }

    // Mueve el consolidado vigente a una versión nueva. El sidecar va primero: una versión sin
    // sidecar no se lista (no se podría restaurar su schema ni su estado).
    public static async Task<string> ArchiveCurrentAsync(
        IParquetStore store,
        string pipelineId,
        string entity,
        ParquetVersion info,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var id = now.UtcDateTime.ToString(IdFormat, CultureInfo.InvariantCulture);
        var schemaJson = await store.ReadTextAsync(IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity), cancellationToken);
        var version = info with { Id = id, ArchivedAt = now, SchemaJson = schemaJson };
        var path = ParquetPath(pipelineId, entity, id);
        await store.UploadTextAsync(SidecarPath(path), JsonSerializer.Serialize(version, JsonDefaults.Options), cancellationToken);
        await store.MoveAsync(IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity), path, cancellationToken);
        return id;
    }

    // Versiones de una tabla, la más reciente primero.
    public static async Task<IReadOnlyList<ParquetVersion>> ListAsync(
        IParquetStore store, string pipelineId, string entity, CancellationToken cancellationToken = default)
    {
        var versions = new List<ParquetVersion>();
        foreach (var artifact in await store.ListParquetAsync(EntityPrefix(pipelineId, entity), cancellationToken))
        {
            var json = await store.ReadTextAsync(SidecarPath(artifact.Path), cancellationToken);
            if (json is not null && JsonSerializer.Deserialize<ParquetVersion>(json, JsonDefaults.Options) is { } version)
            {
                versions.Add(version with { Id = IdOf(artifact.Path) });
            }
        }

        return versions.OrderByDescending(v => v.Id, StringComparer.Ordinal).ToList();
    }

    public static async Task<bool> HasVersionsAsync(
        IParquetStore store, string pipelineId, string entity, CancellationToken cancellationToken = default) =>
        (await store.ListParquetAsync(EntityPrefix(pipelineId, entity), cancellationToken)).Count > 0;

    // Borra las versiones de más de keepDays días y conserva siempre la más reciente; con
    // keepDays <= 0 (versiones desactivadas) las borra todas.
    public static async Task<int> PruneAsync(
        IParquetStore store, string pipelineId, string entity, int keepDays, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var paths = (await store.ListParquetAsync(EntityPrefix(pipelineId, entity), cancellationToken))
            .Select(a => a.Path)
            .OrderByDescending(IdOf, StringComparer.Ordinal)
            .ToList();
        var cutoff = now.AddDays(-keepDays);
        var expired = keepDays <= 0
            ? paths
            : paths.Skip(1).Where(p => ArchivedAtOf(IdOf(p)) is { } at && at < cutoff).ToList();

        foreach (var path in expired)
        {
            await DeleteAsync(store, path, cancellationToken);
        }

        return expired.Count;
    }

    // Reset del pipeline: también se van sus versiones.
    public static async Task<int> DeleteAllAsync(IParquetStore store, string pipelineId, CancellationToken cancellationToken = default)
    {
        var artifacts = await store.ListParquetAsync(Prefix(pipelineId), cancellationToken);
        foreach (var artifact in artifacts)
        {
            await DeleteAsync(store, artifact.Path, cancellationToken);
        }

        return artifacts.Count;
    }

    // Saca una versión de la lista para que vuelva a ser el consolidado vigente.
    public static async Task TakeAsync(
        IParquetStore store, string pipelineId, string entity, string id, CancellationToken cancellationToken = default)
    {
        var path = ParquetPath(pipelineId, entity, id);
        await store.MoveAsync(path, IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity), cancellationToken);
        await store.DeleteParquetAsync(SidecarPath(path), cancellationToken);
    }

    private static async Task DeleteAsync(IParquetStore store, string parquetPath, CancellationToken cancellationToken)
    {
        await store.DeleteParquetAsync(parquetPath, cancellationToken);
        await store.DeleteParquetAsync(SidecarPath(parquetPath), cancellationToken);
    }
}
