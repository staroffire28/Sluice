using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Janitor de snapshots Synapse Link en el container origen de ADLS: purga carpetas CSV
// ya ingestadas y más antiguas que el umbral configurado, conservando un piso de las
// últimas K procesadas y nunca tocando snapshots pendientes o en export.
public sealed class SnapshotRetentionService
{
    // Piso de seguridad: conservar siempre las últimas K snapshots procesadas aunque
    // excedan la antigüedad, para validación manual y margen ante retrasos de ingest.
    public const int KeepLastProcessedSnapshots = 14;

    public const string DispositionDisabled = "retention-disabled";
    public const string DispositionProtected = "retention-protected";
    public const string DispositionRetained = "retention-retained";
    public const string DispositionEligible = "retention-eligible";
    public const string DispositionNotProcessed = "retention-not-processed";

    private readonly IPipelineService _pipelines;
    private readonly IJobService _jobs;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly IActivityLog _activity;
    private readonly ILogger<SnapshotRetentionService> _logger;

    public SnapshotRetentionService(
        IPipelineService pipelines,
        IJobService jobs,
        IAdlsClientFactory adlsFactory,
        IActivityLog activity,
        ILogger<SnapshotRetentionService> logger)
    {
        _pipelines = pipelines;
        _jobs = jobs;
        _adlsFactory = adlsFactory;
        _activity = activity;
        _logger = logger;
    }

    // Lógica pura: clasifica cada snapshot y decide elegibles para purga. protectFrom: el primer
    // snapshot que aún tiene que releer una tabla reanudada (SharedBackfillFrom); desde él, protegidos.
    public static SnapshotRetentionAnalysis Analyze(
        DateTimeOffset now,
        AppSettings settings,
        IReadOnlyList<string> allSnapshots,
        string? lastProcessed,
        SnapshotSelector.SelectionResult selection,
        string? protectFrom = null)
    {
        var dispositions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in allSnapshots.Where(ExcludedEntities.IsSnapshotFolder))
        {
            dispositions[name] = DispositionNotProcessed;
        }

        if (settings.SourceSnapshotRetentionDays <= 0)
        {
            foreach (var key in dispositions.Keys.ToList())
            {
                dispositions[key] = DispositionDisabled;
            }

            return new SnapshotRetentionAnalysis([], dispositions);
        }

        if (selection.Blocked || string.IsNullOrWhiteSpace(lastProcessed))
        {
            foreach (var key in dispositions.Keys.ToList())
            {
                dispositions[key] = DispositionDisabled;
            }

            return new SnapshotRetentionAnalysis([], dispositions);
        }

        var cutoff = now - TimeSpan.FromDays(settings.SourceSnapshotRetentionDays);
        var protectedNames = new HashSet<string>(selection.ToProcess, StringComparer.Ordinal);
        foreach (var name in selection.Unclosed)
        {
            protectedNames.Add(name);
        }

        foreach (var name in protectedNames)
        {
            if (dispositions.ContainsKey(name))
            {
                dispositions[name] = DispositionProtected;
            }
        }

        var processed = allSnapshots
            .Where(ExcludedEntities.IsSnapshotFolder)
            .Where(s => string.CompareOrdinal(s, lastProcessed) <= 0)
            .Where(s => !protectedNames.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var floor = new HashSet<string>(
            processed.Count <= KeepLastProcessedSnapshots
                ? processed
                : processed[^KeepLastProcessedSnapshots..],
            StringComparer.Ordinal);

        var eligible = new List<string>();
        foreach (var snapshot in processed)
        {
            if (floor.Contains(snapshot))
            {
                dispositions[snapshot] = DispositionRetained;
                continue;
            }

            // Pendiente de releer por una tabla reanudada: cuenta en el piso, pero no se purga.
            if (protectFrom is not null && string.CompareOrdinal(snapshot, protectFrom) >= 0)
            {
                dispositions[snapshot] = DispositionProtected;
                continue;
            }

            // Sin fecha legible no se puede saber su antigüedad: se conserva.
            if (!SnapshotFolderTime.TryParse(snapshot, out var timestamp))
            {
                dispositions[snapshot] = DispositionRetained;
                continue;
            }

            if (timestamp >= cutoff)
            {
                dispositions[snapshot] = DispositionRetained;
                continue;
            }

            dispositions[snapshot] = DispositionEligible;
            eligible.Add(snapshot);
        }

        return new SnapshotRetentionAnalysis(eligible, dispositions);
    }

    public static SnapshotRetentionPlan Plan(
        DateTimeOffset now,
        AppSettings settings,
        IReadOnlyList<string> allSnapshots,
        string? lastProcessed,
        SnapshotSelector.SelectionResult selection) =>
        new() { SnapshotFolders = Analyze(now, settings, allSnapshots, lastProcessed, selection).EligibleForPurge };

    public async Task<SnapshotRetentionSweepResult> SweepAsync(
        DateTimeOffset now,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.SourceSnapshotRetentionDays <= 0)
        {
            return new SnapshotRetentionSweepResult();
        }

        var runningPipelineIds = _jobs.ListRunning()
            .Select(j => j.PipelineId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var totalDeleted = 0;
        var totalWouldDelete = 0;
        var totalSkipped = 0;
        var totalSkippedCert = 0;

        foreach (var pipeline in _pipelines.List().Where(p => p.Enabled))
        {
            var sourcePipelineIds = _pipelines.List()
                .Where(p => p.SourceId == pipeline.SourceId)
                .Select(p => p.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (runningPipelineIds.Overlaps(sourcePipelineIds))
            {
                totalSkipped++;
                continue;
            }

            try
            {
                var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
                var parquetStore = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
                var allSnapshots = await adls.ListSnapshotFoldersAsync(cancellationToken);
                var currentFolder = await adls.GetSettledSnapshotAsync(cancellationToken);
                var floor = SharedLastProcessed(_pipelines, pipeline.SourceId);
                var selection = SnapshotSelector.Select(
                    allSnapshots,
                    floor,
                    currentFolder);

                var analysis = Analyze(now, settings, allSnapshots, floor, selection, SharedBackfillFrom(_pipelines, pipeline.SourceId));
                if (analysis.EligibleForPurge.Count == 0)
                {
                    continue;
                }

                // Las carpetas son de la fuente: cada pipeline que la lee (activo o no) debe tener su
                // parquet consolidado antes de purgar.
                if (settings.SourceSnapshotRetentionRequireParquet)
                {
                    var certified = true;
                    foreach (var sourcePipelineId in sourcePipelineIds)
                    {
                        var cert = await CertifyParquetAsync(
                            sourcePipelineId,
                            parquetStore,
                            _pipelines.GetState(sourcePipelineId),
                            cancellationToken);
                        if (!cert.IsComplete)
                        {
                            totalSkippedCert++;
                            _activity.LogSystem(
                                "snapshot-retention.skip",
                                $"Pipeline {sourcePipelineId}: purga omitida — faltan parquet consolidados: " +
                                $"{string.Join(", ", cert.MissingEntities)}.",
                                sourcePipelineId, null, "warning",
                                "Activity.SnapshotRetention.Skip", sourcePipelineId, string.Join(", ", cert.MissingEntities));
                            certified = false;
                            break;
                        }
                    }

                    if (!certified)
                    {
                        continue;
                    }
                }

                var deleted = 0;
                foreach (var folder in analysis.EligibleForPurge)
                {
                    if (_jobs.ListRunning().Any(j => sourcePipelineIds.Contains(j.PipelineId)))
                    {
                        _logger.LogInformation(
                            "Retención snapshots: purga interrumpida en {PipelineId} — job en ejecución.",
                            pipeline.Id);
                        break;
                    }

                    var floorNow = SharedLastProcessed(_pipelines, pipeline.SourceId);
                    var selectionNow = SnapshotSelector.Select(
                        allSnapshots,
                        floorNow,
                        currentFolder);
                    if (!Analyze(now, settings, allSnapshots, floorNow, selectionNow, SharedBackfillFrom(_pipelines, pipeline.SourceId))
                        .EligibleForPurge.Contains(folder, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    if (settings.SourceSnapshotRetentionDryRun)
                    {
                        totalWouldDelete++;
                        continue;
                    }

                    try
                    {
                        await adls.DeleteSnapshotFolderAsync(folder, cancellationToken);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Retención snapshots: no se pudo borrar {Folder} en pipeline {PipelineId}",
                            folder,
                            pipeline.Id);
                        _activity.LogSystem(
                            "snapshot-retention.error",
                            $"Pipeline {pipeline.Id}: no se pudo borrar snapshot {folder}: {ex.Message}",
                            pipeline.Id, null, "warning",
                            "Activity.SnapshotRetention.DeleteFailed", pipeline.Id, folder, ex.Message);
                    }
                }

                if (settings.SourceSnapshotRetentionDryRun && analysis.EligibleForPurge.Count > 0)
                {
                    _activity.LogSystem(
                        "snapshot-retention.dry-run",
                        $"Pipeline {pipeline.Id}: {analysis.EligibleForPurge.Count} snapshot(s) elegibles " +
                        $"(simulación, sin borrar).",
                        pipeline.Id, null, "info",
                        "Activity.SnapshotRetention.DryRun", pipeline.Id, analysis.EligibleForPurge.Count);
                }
                else if (deleted > 0)
                {
                    _activity.LogSystem(
                        "snapshot-retention.sweep",
                        $"Pipeline {pipeline.Id}: {deleted} snapshot(s) origen eliminados " +
                        $"(antigüedad {settings.SourceSnapshotRetentionDays}d, piso {KeepLastProcessedSnapshots}).",
                        pipeline.Id, null, "info",
                        "Activity.SnapshotRetention.Sweep", pipeline.Id, deleted, settings.SourceSnapshotRetentionDays, KeepLastProcessedSnapshots);
                    totalDeleted += deleted;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot retention sweep failed for pipeline {PipelineId}", pipeline.Id);
                _activity.LogSystem(
                    "snapshot-retention.error",
                    $"Barrido de snapshots falló en pipeline {pipeline.Id}: {ex.Message}",
                    pipeline.Id, null, "error",
                    "Activity.SnapshotRetention.PipelineFailed", pipeline.Id, ex.Message);
            }
        }

        if (settings.SourceSnapshotRetentionDryRun && totalWouldDelete > 0)
        {
            _activity.LogSystem(
                "snapshot-retention.dry-run",
                $"Simulación global: {totalWouldDelete} snapshot(s) elegibles para purga " +
                $"(antigüedad {settings.SourceSnapshotRetentionDays}d).",
                null, null, "info", "Activity.SnapshotRetention.DryRunTotal", totalWouldDelete, settings.SourceSnapshotRetentionDays);
        }
        else if (totalDeleted > 0)
        {
            _activity.LogSystem(
                "snapshot-retention.sweep",
                $"Retención snapshots origen: {totalDeleted} carpeta(s) eliminadas " +
                $"(antigüedad {settings.SourceSnapshotRetentionDays}d, piso {KeepLastProcessedSnapshots}).",
                null, null, "info",
                "Activity.SnapshotRetention.SweepTotal", totalDeleted, settings.SourceSnapshotRetentionDays, KeepLastProcessedSnapshots);
        }

        return new SnapshotRetentionSweepResult
        {
            Deleted = totalDeleted,
            WouldDelete = totalWouldDelete,
            PipelinesSkippedRunning = totalSkipped,
            PipelinesSkippedCertification = totalSkippedCert,
            DryRun = settings.SourceSnapshotRetentionDryRun,
        };
    }

    // Varios pipelines pueden leer la misma fuente: el suelo de la purga es el snapshot procesado
    // más antiguo entre todos ellos (activos o no). Si alguno aún no tiene estado, null: no se purga.
    public static string? SharedLastProcessed(IPipelineService pipelines, string sourceId)
    {
        string? floor = null;
        foreach (var pipeline in pipelines.List().Where(p => p.SourceId == sourceId))
        {
            var last = pipelines.GetState(pipeline.Id).LastProcessedSnapshot;
            if (string.IsNullOrWhiteSpace(last))
            {
                return null;
            }

            if (floor is null || string.CompareOrdinal(last, floor) < 0)
            {
                floor = last;
            }
        }

        return floor;
    }

    // Segundo suelo: una tabla reanudada relee desde el primer snapshot que se saltó en pausa
    // (PendingBackfill). El más antiguo de los pipelines que leen la fuente, o null si no hay ninguno.
    public static string? SharedBackfillFrom(IPipelineService pipelines, string sourceId)
    {
        string? from = null;
        foreach (var pipeline in pipelines.List().Where(p => p.SourceId == sourceId))
        {
            foreach (var snapshot in pipelines.GetState(pipeline.Id).PendingBackfill.Values)
            {
                if (from is null || string.CompareOrdinal(snapshot, from) < 0)
                {
                    from = snapshot;
                }
            }
        }

        return from;
    }

    public static async Task<ParquetCertificationResult> CertifyParquetAsync(
        string pipelineId,
        IParquetStore parquetStore,
        PipelineState state,
        CancellationToken cancellationToken = default)
    {
        var entities = state.Tables.Keys.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
        if (entities.Count == 0)
        {
            var listed = await parquetStore.ListParquetAsync($"{pipelineId}/", cancellationToken);
            entities = ConsolidatedEntities(listed);
        }

        var missing = new List<string>();
        foreach (var entity in entities)
        {
            var blobPath = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity);
            if (!await parquetStore.ParquetExistsAsync(blobPath, cancellationToken))
            {
                missing.Add(entity);
            }
        }

        return new ParquetCertificationResult(missing.Count == 0, missing);
    }

    // Para quien ya tiene el listado del pipeline (el Panel): diferencia de conjuntos contra ese
    // listado en vez de un Exists por tabla.
    public static ParquetCertificationResult CertifyParquet(
        string pipelineId,
        PipelineState state,
        IReadOnlyList<ParquetArtifact> listed)
    {
        var existing = listed.Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        var entities = state.Tables.Count == 0
            ? ConsolidatedEntities(listed)
            : state.Tables.Keys.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = entities
            .Where(e => !existing.Contains(IngestManifestStore.GetConsolidatedBlobPath(pipelineId, e)))
            .ToList();

        return new ParquetCertificationResult(missing.Count == 0, missing);
    }

    private static List<string> ConsolidatedEntities(IReadOnlyList<ParquetArtifact> listed) =>
        listed
            .Select(a => a.Path)
            .Where(p => p.Count(c => c == '/') == 1 && p.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFileNameWithoutExtension(p.Split('/').Last()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public sealed record SnapshotRetentionAnalysis(
    IReadOnlyList<string> EligibleForPurge,
    IReadOnlyDictionary<string, string> DispositionBySnapshot);

public sealed record SnapshotRetentionPlan
{
    public IReadOnlyList<string> SnapshotFolders { get; init; } = [];
}

public sealed record SnapshotRetentionSweepResult
{
    public int Deleted { get; init; }
    public int WouldDelete { get; init; }
    public int PipelinesSkippedRunning { get; init; }
    public int PipelinesSkippedCertification { get; init; }
    public bool DryRun { get; init; }
}

public sealed record ParquetCertificationResult(bool IsComplete, IReadOnlyList<string> MissingEntities);