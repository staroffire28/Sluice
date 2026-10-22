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

    // Una versión con su schema, de su sidecar; null si ya no existe.
    public static async Task<ParquetVersion?> GetAsync(
        IParquetStore store, string pipelineId, string entity, string id, CancellationToken cancellationToken = default)
    {
        var path = ParquetPath(pipelineId, entity, id);
        if (!await store.ParquetExistsAsync(path, cancellationToken))
        {
            return null;
        }

        var json = await store.ReadTextAsync(SidecarPath(path), cancellationToken);
        return json is not null && JsonSerializer.Deserialize<ParquetVersion>(json, JsonDefaults.Options) is { } version
            ? version with { Id = id }
            : null;
    }

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

    // Índice de los sidecars del pipeline sin el schema, en _versions/{pipeline}.index.json (fuera de
    // {pipeline}/ para no listarse como versión). Leer todos los sidecars costaba miles de lecturas en
    // cada ingesta, cada una con su schema. Es una caché derivada: manda el listado de .parquet, las
    // versiones que no están en el índice se leen de su sidecar y las que ya no se listan se
    // descartan, así que un índice que falta, está corrupto o se quedó atrás (otro proceso archivó,
    // restauró o podó) se repara en la siguiente lectura. Un sidecar no cambia una vez escrito, así
    // que una entrada del índice nunca queda desfasada.
    private static string IndexPath(string pipelineId) => $"{Root}/{pipelineId}.index.json";

    private static async Task<IReadOnlyList<(string Path, ParquetVersion Version)>> ReadIndexedAsync(
        IParquetStore store, string pipelineId, IReadOnlyList<ParquetArtifact> artifacts, CancellationToken cancellationToken)
    {
        var index = await ReadIndexAsync(store, pipelineId, cancellationToken);
        var listed = artifacts.Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        var cached = index.Where(e => listed.Contains(e.Key)).Select(e => (Path: e.Key, Version: e.Value with { Id = IdOf(e.Key) })).ToList();
        var read = (await ReadAsync(store, artifacts.Where(a => !index.ContainsKey(a.Path)).ToList(), cancellationToken))
            .Select(v => (v.Path, Version: v.Version with { SchemaJson = null }))
            .ToList();
        var versions = cached.Concat(read).ToList();

        if (read.Count > 0 || cached.Count != index.Count)
        {
            var updated = versions.ToDictionary(v => v.Path, v => v.Version, StringComparer.Ordinal);
            await store.UploadTextAsync(IndexPath(pipelineId), JsonSerializer.Serialize(updated, JsonDefaults.Options), cancellationToken);
        }

        return versions;
    }

    private static async Task<Dictionary<string, ParquetVersion>> ReadIndexAsync(
        IParquetStore store, string pipelineId, CancellationToken cancellationToken)
    {
        var json = await store.ReadTextAsync(IndexPath(pipelineId), cancellationToken);
        if (json is null)
        {
            return new(StringComparer.Ordinal);
        }

        try
        {
            var index = JsonSerializer.Deserialize<Dictionary<string, ParquetVersion?>>(json, JsonDefaults.Options) ?? [];
            return index.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Corrupto: se reconstruye desde los sidecars.
            return new(StringComparer.Ordinal);
        }
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

    // Sin el schema (SchemaJson null): quien restaura una versión la lee entera con GetAsync.
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>>> ListAllAsync(
        IParquetStore store, string pipelineId, IReadOnlyList<ParquetArtifact> artifacts, CancellationToken cancellationToken) =>
        (await ReadIndexedAsync(store, pipelineId, artifacts, cancellationToken))
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

    // Estado de una tabla tras volver a una versión: el de la versión o, si no lo trae, ninguno.
    public static void SetTable(Dictionary<string, TableState> tables, string entity, TableState? table)
    {
        if (table is not null)
        {
            tables[entity] = table;
        }
        else
        {
            tables.Remove(entity);
        }
    }

    // Tablas que restaurar un punto recarga en SQL: las que vuelven atrás y las que un restore
    // anterior ya movió sin llegar a recargarlas (PipelineState.PendingReload).
    public static IReadOnlyList<string> ToReload(RestorePlan plan, IEnumerable<string> pendingReload) =>
        plan.Restore.Select(r => r.Entity).Union(pendingReload, StringComparer.OrdinalIgnoreCase)
            .Except(plan.Archive, StringComparer.OrdinalIgnoreCase).ToList();

    // Los puntos que la poda conserva enteros, los únicos que se ofrecen, escalonados: siempre el
    // más reciente, todos los de las últimas 24 h y, más atrás y dentro de los últimos keepDays
    // días, el último de cada día UTC (los snapshots y los ids ya van en UTC y la poda corre en el
    // worker, sin zona del usuario). El último del día se busca entre todos los puntos de ese día:
    // uno que no lo es cae en cuanto pasan las 24 h. Cuenta la fecha del snapshot y no cuándo
    // se archivaron sus versiones, que cambia al podar: así un punto que se deja caer no vuelve a la
    // lista con versiones de menos. Por lo mismo, los snapshots cuyas versiones retiró un restore
    // (PipelineState.RetiredSnapshots) siguen contando como puntos aunque no se ofrezcan: si no, el
    // punto anterior de su día, ya podado, pasaría a ser el último. Con keepDays <= 0 (versiones
    // desactivadas), ninguno.
    public static IReadOnlyList<RestorePoint> KeptPoints(
        IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>> versions, int keepDays, DateTimeOffset now,
        IEnumerable<string>? retired = null)
    {
        if (keepDays <= 0)
        {
            return [];
        }

        var cutoff = now.AddDays(-keepDays);
        var recent = now.AddDays(-1);
        var points = RestorePoints(versions);
        var snapshots = points.Select(p => p.Snapshot).Union(retired ?? [], StringComparer.Ordinal)
            .OrderByDescending(s => s, StringComparer.Ordinal)
            .ToList();
        var times = snapshots.Select(s => SnapshotFolderTime.TryParse(s, out var at) ? at : (DateTimeOffset?)null).ToList();

        // Del más reciente al más antiguo: un punto es el último de su día si el anterior de la
        // lista (el siguiente en el tiempo) es de otro día.
        var kept = snapshots
            .Where((snapshot, index) => index == 0
                || times[index] >= recent
                || (times[index] >= cutoff && times[index - 1]?.UtcDateTime.Date != times[index]!.Value.UtcDateTime.Date))
            .ToHashSet(StringComparer.Ordinal);
        return points.Where(p => kept.Contains(p.Snapshot)).ToList();
    }

    // Los snapshots que retira un restore: los de las versiones que vuelven a ser el consolidado,
    // no solo el del punto (una tabla que no cambió justo después usa la versión de un punto
    // posterior). Se olvidan los anteriores a todas las versiones: ya no hay puntos que puedan
    // dejar de ser el último de su día y los nuevos siempre son posteriores.
    public static List<string> Retire(
        IEnumerable<string> retired, IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>> versions, RestorePlan plan)
    {
        var oldest = versions.Values.SelectMany(v => v).Select(v => v.PipelineSnapshot).OfType<string>().Min(StringComparer.Ordinal);
        return retired
            .Union(plan.Restore.Select(r => r.Version.PipelineSnapshot).OfType<string>(), StringComparer.Ordinal)
            .Where(s => oldest is not null && string.CompareOrdinal(s, oldest) >= 0)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // Poda por puntos de restauración: se conservan las versiones que necesitan los puntos
    // conservados (KeptPoints) y nada más, también dentro de la ventana (las de los puntos que no
    // son el último de su día, la copia repetida de un reintento o un Force). Con keepDays <= 0
    // (versiones desactivadas) se borran todas. No hay atajo por la fecha de archivado (el id): qué
    // sobra depende del snapshot de cada versión, no de su edad. Sin nada que podar cuesta el
    // listado y el índice; solo se leen los sidecars de las versiones que aún no están en él.
    // retired: PipelineState.RetiredSnapshots, como en KeptPoints.
    public static async Task<int> PruneAsync(
        IParquetStore store, string pipelineId, int keepDays, DateTimeOffset now, IEnumerable<string>? retired = null,
        CancellationToken cancellationToken = default)
    {
        if (keepDays <= 0)
        {
            return await DeleteAllAsync(store, pipelineId, cancellationToken);
        }

        var artifacts = await store.ListParquetAsync(Prefix(pipelineId), cancellationToken);
        if (artifacts.Count == 0)
        {
            return 0;
        }

        var versions = await ListAllAsync(store, pipelineId, artifacts, cancellationToken);
        var kept = KeptPoints(versions, keepDays, now, retired);
        var expired = versions
            .SelectMany(e =>
            {
                var needed = NeededBy(e.Value, kept);
                return e.Value
                    .Where(v => !needed.Contains(v.Id))
                    .Select(v => ParquetPath(pipelineId, e.Key, v.Id));
            })
            .ToList();

        foreach (var path in expired)
        {
            await DeleteAsync(store, path, cancellationToken);
        }

        return expired.Count;
    }

    // Ids de las versiones de una tabla que usan los puntos conservados: VersionAt de cada punto, en
    // una sola pasada (con miles de versiones, repetir VersionAt por punto y versión no acababa).
    internal static HashSet<string> NeededBy(IEnumerable<ParquetVersion> versions, IEnumerable<RestorePoint> points)
    {
        var ordered = versions
            .Where(v => v.PipelineSnapshot is not null)
            .OrderBy(v => v.PipelineSnapshot, StringComparer.Ordinal)
            .ThenBy(v => v.Id, StringComparer.Ordinal)
            .ToList();
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var snapshot in points.Select(p => p.Snapshot).Order(StringComparer.Ordinal))
        {
            while (i < ordered.Count && string.CompareOrdinal(ordered[i].PipelineSnapshot, snapshot) < 0)
            {
                i++;
            }

            if (i == ordered.Count)
            {
                break;
            }

            needed.Add(ordered[i].Id);
        }

        return needed;
    }

    // Reset del pipeline: también se van sus versiones y su índice.
    public static async Task<int> DeleteAllAsync(IParquetStore store, string pipelineId, CancellationToken cancellationToken = default)
    {
        var artifacts = await store.ListParquetAsync(Prefix(pipelineId), cancellationToken);
        foreach (var artifact in artifacts)
        {
            await DeleteAsync(store, artifact.Path, cancellationToken);
        }

        // Si nunca se archivó nada no hay _versions/ y LocalParquetStore no borra en una carpeta que no existe.
        if (await store.ParquetExistsAsync(IndexPath(pipelineId), cancellationToken))
        {
            await store.DeleteParquetAsync(IndexPath(pipelineId), cancellationToken);
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
