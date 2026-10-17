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

// Un punto de restauración: el lago tal como quedó al procesar un snapshot. LeftAt y
// ReplacedByJobId: cuándo y con qué job se dejó atrás (su primera versión archivada).
public sealed record RestorePoint(string Snapshot, DateTimeOffset LeftAt, string? ReplacedByJobId);

// Qué pasa con cada tabla al volver a un punto: Restore recupera la versión que tenía entonces y
// Archive son las que aún no existían (su consolidado pasa a versión).
public sealed record RestorePlan(IReadOnlyList<(string Entity, ParquetVersion Version)> Restore, IReadOnlyList<string> Archive);

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

    private static string EntityOf(string pipelineId, string parquetPath) => parquetPath[Prefix(pipelineId).Length..].Split('/')[0];

    // Versiones de una tabla, la más reciente primero.
    public static async Task<IReadOnlyList<ParquetVersion>> ListAsync(
        IParquetStore store, string pipelineId, string entity, CancellationToken cancellationToken = default) =>
        (await ReadAsync(store, await store.ListParquetAsync(EntityPrefix(pipelineId, entity), cancellationToken), cancellationToken))
            .Select(v => v.Version)
            .OrderByDescending(v => v.Id, StringComparer.Ordinal)
            .ToList();

    // Lee los sidecars en paralelo: con cientos de tablas son miles de lecturas.
    private static async Task<IReadOnlyList<(string Path, ParquetVersion Version)>> ReadAsync(
        IParquetStore store, IReadOnlyList<ParquetArtifact> artifacts, CancellationToken cancellationToken)
    {
        var versions = new (string Path, ParquetVersion Version)?[artifacts.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, artifacts.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var path = artifacts[i].Path;
                var json = await store.ReadTextAsync(SidecarPath(path), ct);
                if (json is not null && JsonSerializer.Deserialize<ParquetVersion>(json, JsonDefaults.Options) is { } version)
                {
                    versions[i] = (path, version with { Id = IdOf(path) });
                }
            });

        return versions.Where(v => v is not null).Select(v => v!.Value).ToList();
    }

    // Tablas del pipeline con alguna versión, aunque ya no tengan consolidado: la página Parquet
    // las lista igualmente para poder restaurar cuando la ingesta se para porque falta el consolidado.
    public static async Task<IReadOnlyList<string>> ListEntitiesAsync(
        IParquetStore store, string pipelineId, CancellationToken cancellationToken = default) =>
        (await store.ListParquetAsync(Prefix(pipelineId), cancellationToken))
            .Select(a => EntityOf(pipelineId, a.Path))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static async Task<bool> HasVersionsAsync(
        IParquetStore store, string pipelineId, string entity, CancellationToken cancellationToken = default) =>
        (await store.ListParquetAsync(EntityPrefix(pipelineId, entity), cancellationToken)).Count > 0;

    // Las versiones de todas las tablas del pipeline, por tabla.
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>>> ListAllAsync(
        IParquetStore store, string pipelineId, CancellationToken cancellationToken = default) =>
        await ListAllAsync(store, pipelineId, await store.ListParquetAsync(Prefix(pipelineId), cancellationToken), cancellationToken);

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>>> ListAllAsync(
        IParquetStore store, string pipelineId, IReadOnlyList<ParquetArtifact> artifacts, CancellationToken cancellationToken) =>
        (await ReadAsync(store, artifacts, cancellationToken))
            .GroupBy(v => EntityOf(pipelineId, v.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ParquetVersion>)g.Select(v => v.Version).OrderByDescending(v => v.Id, StringComparer.Ordinal).ToList(),
                StringComparer.OrdinalIgnoreCase);

    // La versión de una tabla que era la vigente al terminar de procesar un snapshot: la primera que
    // se archivó después. Se archiva con el LastProcessedSnapshot del momento, así que es la de menor
    // PipelineSnapshot >= snapshot y, entre las que lo comparten (un job que falló a medias y se
    // reintentó, un Force), la más antigua: las siguientes ya traen filas posteriores o rehechas.
    // null: la tabla no ha cambiado desde entonces, o aún no existía.
    public static ParquetVersion? VersionAt(IEnumerable<ParquetVersion> versions, string snapshot) =>
        versions
            .Where(v => v.PipelineSnapshot is not null && string.CompareOrdinal(v.PipelineSnapshot, snapshot) >= 0)
            .OrderBy(v => v.PipelineSnapshot, StringComparer.Ordinal)
            .ThenBy(v => v.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    // Un punto por cada snapshot con el que se archivó alguna versión, el más reciente primero.
    public static IReadOnlyList<RestorePoint> RestorePoints(IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>> versions) =>
        versions.Values
            .SelectMany(v => v)
            .Where(v => v.PipelineSnapshot is not null)
            .GroupBy(v => v.PipelineSnapshot!, StringComparer.Ordinal)
            .Select(g => g.MinBy(v => v.Id, StringComparer.Ordinal)!)
            .Select(v => new RestorePoint(v.PipelineSnapshot!, v.ArchivedAt, v.ReplacedByJobId))
            .OrderByDescending(p => p.Snapshot, StringComparer.Ordinal)
            .ToList();

    // Volver al punto de un snapshot. Una tabla sin versión de entonces válida (no tiene, o ya trae
    // datos posteriores) cuyo estado actual tiene datos posteriores aún no existía: se archiva. Las
    // demás ya están en ese punto (no han cambiado desde entonces, o una restauración anterior ya las
    // devolvió o archivó) y se quedan como están. Los nombres de snapshot
    // (yyyy-MM-ddTHH.mm.ssZ) ordenan cronológicamente.
    public static RestorePlan Plan(
        IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>> versions,
        IReadOnlyDictionary<string, TableState> tables,
        string snapshot)
    {
        static bool After(string? value, string snapshot) => value is not null && string.CompareOrdinal(value, snapshot) > 0;

        var restore = new List<(string, ParquetVersion)>();
        var archive = new List<string>();
        foreach (var entity in versions.Keys.Union(tables.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var version = versions.TryGetValue(entity, out var list) ? VersionAt(list, snapshot) : null;
            if (version is not null && !After(version.Table?.LastSnapshot, snapshot))
            {
                restore.Add((entity, version));
            }
            else if (After(tables.GetValueOrDefault(entity)?.LastSnapshot, snapshot))
            {
                archive.Add(entity);
            }
        }

        return new RestorePlan(restore, archive);
    }

    // Tablas que restaurar un punto recarga en SQL: las que vuelven atrás y las que un restore
    // anterior ya movió sin llegar a recargarlas (PipelineState.PendingReload).
    public static IReadOnlyList<string> ToReload(RestorePlan plan, IEnumerable<string> pendingReload) =>
        plan.Restore.Select(r => r.Entity).Union(pendingReload, StringComparer.OrdinalIgnoreCase)
            .Except(plan.Archive, StringComparer.OrdinalIgnoreCase).ToList();

    // Los puntos que la poda conserva enteros, los únicos que se ofrecen: los de snapshots de los
    // últimos keepDays días y siempre el más reciente. Cuenta la fecha del snapshot y no cuándo se
    // archivaron sus versiones, que cambia al podar: así un punto que se deja caer no vuelve a la
    // lista con versiones de menos. Con keepDays <= 0 (versiones desactivadas), ninguno.
    public static IReadOnlyList<RestorePoint> KeptPoints(
        IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>> versions, int keepDays, DateTimeOffset now)
    {
        if (keepDays <= 0)
        {
            return [];
        }

        var cutoff = now.AddDays(-keepDays);
        return RestorePoints(versions)
            .Where((point, index) => index == 0 || (SnapshotFolderTime.TryParse(point.Snapshot, out var at) && at >= cutoff))
            .ToList();
    }

    // Poda por puntos de restauración: se conservan las versiones que necesitan los puntos
    // conservados (KeptPoints); del resto solo se borran las de más de keepDays días. Con
    // keepDays <= 0 (versiones desactivadas) se borran todas. El id es la fecha de archivado: si
    // ninguna versión es anterior al corte no hay nada que podar y no se leen los sidecars.
    public static async Task<int> PruneAsync(
        IParquetStore store, string pipelineId, int keepDays, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (keepDays <= 0)
        {
            return await DeleteAllAsync(store, pipelineId, cancellationToken);
        }

        var cutoff = now.AddDays(-keepDays);
        var artifacts = await store.ListParquetAsync(Prefix(pipelineId), cancellationToken);
        if (artifacts.All(a => DateTimeOffset.TryParseExact(IdOf(a.Path), IdFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at >= cutoff))
        {
            return 0;
        }

        var versions = await ListAllAsync(store, pipelineId, artifacts, cancellationToken);
        var kept = KeptPoints(versions, keepDays, now);
        var expired = versions
            .SelectMany(e => e.Value
                .Where(v => v.ArchivedAt < cutoff && !kept.Any(p => VersionAt(e.Value, p.Snapshot) == v))
                .Select(v => ParquetPath(pipelineId, e.Key, v.Id)))
            .ToList();

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
