using System.Text.Json;
using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Workers;

public interface IReconcileExecutor
{
    Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default);
}

public sealed class ReconcileExecutor : IReconcileExecutor
{
    private readonly IJobService _jobs;
    private readonly IPipelineService _pipelines;
    private readonly IReconcileService _reconcile;
    private readonly IngestManifestStore _manifestStore;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly ISqlConnectionFactory _sqlFactory;
    private readonly ISqlMergeExecutor _mergeExecutor;
    private readonly AlertService _alerts;
    private readonly JobProgressStore? _progressStore;
    private readonly ColumnSelectionStore? _columns;

    public ReconcileExecutor(
        IJobService jobs,
        IPipelineService pipelines,
        IReconcileService reconcile,
        IngestManifestStore manifestStore,
        IAdlsClientFactory adlsFactory,
        ISqlConnectionFactory sqlFactory,
        ISqlMergeExecutor mergeExecutor,
        AlertService alerts,
        JobProgressStore? progress = null,
        ColumnSelectionStore? columns = null)
    {
        _columns = columns;
        _jobs = jobs;
        _pipelines = pipelines;
        _reconcile = reconcile;
        _manifestStore = manifestStore;
        _adlsFactory = adlsFactory;
        _sqlFactory = sqlFactory;
        _mergeExecutor = mergeExecutor;
        _alerts = alerts;
        _progressStore = progress;
    }

    public async Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(task.JobId)
            ?? throw new InvalidOperationException($"Job '{task.JobId}' not found.");

        var pipeline = _pipelines.Get(task.PipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{task.PipelineId}' not found.");

        // Con DeleteMode.Delete las filas de borrado no llegan a SQL: tampoco cuentan en el parquet.
        var excludeDeleteRows = pipeline.DeleteMode == DeleteMode.Delete;

        var stageStart = DateTimeOffset.UtcNow;
        _jobs.AppendEvent(task.JobId, EventLevel.Info, JobEventMarkers.ReconcileStarted, null, "Event.Reconcile.Started");
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["reconcile"] = "running" },
            StageTimings = job.StageTimings.Set("reconcile", started: stageStart),
        });
        var progress = _progressStore?.Begin(task.PipelineId, task.JobId, "reconcile", cancellationToken) ?? StageProgress.None;
        progress.StartStep(JobStepCatalog.Compare);

        var manifest = _manifestStore.Load(task.PipelineId, task.JobId);
        // Re-sync: una o varias tablas separadas por comas (la corrección automática junta todas).
        var scopedEntities = (task.Payload.TryGetValue("entity", out var entRaw) ? entRaw?.ToString() : null)?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        await using var sql = await _sqlFactory.OpenAsync(pipeline.DestinationId, cancellationToken);
        var sqlSchema = _sqlFactory.GetSchema(pipeline.DestinationId);
        var sqlVersion = _sqlFactory.GetSqlVersion(pipeline.DestinationId);

        if (job.Type == JobType.CompareValues)
        {
            await CompareValuesAsync(task, pipeline.SourceId, pipeline.Name, sql, sqlSchema, scopedEntities, excludeDeleteRows, progress, stageStart, cancellationToken);
            return;
        }

        // Reconcile = validar la fuente de verdad durable (parquet consolidado, deduplicado)
        // contra SQL. Ambos conteos deben coincidir (SQL se carga desde el consolidado).
        var results = new List<ReconcileEntityResult>();

        // Una entidad que no se puede contar (sin parquet consolidado, o un fallo de SQL o del
        // almacén) no tumba la conciliación de las demás: queda fuera del reporte, con su aviso, y el
        // job en Partial. Antes un parquet ausente contaba 0 filas, y un fallo cortaba la etapa.
        var failed = new List<(string Entity, string? Error)>();
        async Task CompareCountsAsync(IParquetStore parquetStore, string entity, string blobPath)
        {
            try
            {
                if (!await parquetStore.ParquetExistsAsync(blobPath, cancellationToken))
                {
                    failed.Add((entity, null));
                    return;
                }

                var sourceRows = await parquetStore.CountParquetRowsAsync(blobPath, excludeDeleteRows, cancellationToken);
                var destRows = await _mergeExecutor.CountRowsAsync(sql, sqlSchema, entity, sqlVersion, cancellationToken);
                results.Add(new ReconcileEntityResult
                {
                    Entity = entity,
                    SourceRows = sourceRows,
                    DestinationRows = destRows,
                    DivergencePct = ReconcileService.ComputeDivergencePct(sourceRows, destRows),
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failed.Add((entity, ex.Message));
            }
        }

        if (scopedEntities.Length > 0)
        {
            // Re-sync por tabla: recontar el parquet consolidado de ESAS entidades directamente
            // (no se confía en el manifest sintético) y compararlo contra SQL.
            var parquetStore = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
            foreach (var scopedEntity in scopedEntities)
            {
                var blobPath = IngestManifestStore.GetConsolidatedBlobPath(task.PipelineId, scopedEntity);
                progress.Report(JobStepCatalog.Compare, results.Count, total: scopedEntities.Length, item: scopedEntity, force: true);
                await CompareCountsAsync(parquetStore, scopedEntity, blobPath);
            }
        }
        else if (manifest is not null && manifest.Entities.Count > 0)
        {
            // Camino normal: las entidades que tocó el ingest de este job. No confiamos en
            // FilteredRows del manifest — en una recarga desde parquet (Force/ReloadFromParquet,
            // ver OrchestrationService.EnqueueParquetReloadAsync) el manifest es sintético y ese
            // campo queda en 0, lo que producía ~100% de divergencia falsa. Recontamos el parquet
            // consolidado real, igual que las otras dos ramas de este método.
            var parquetStore = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
            foreach (var entry in manifest.Entities)
            {
                progress.Report(JobStepCatalog.Compare, results.Count, total: manifest.Entities.Count, item: entry.Entity);
                await CompareCountsAsync(parquetStore, entry.Entity, entry.ParquetPath);
            }
        }
        else if (job.Type is JobType.Reconcile or JobType.Diff)
        {
            // Reconcile/Diff explícitos (etapa única, sin ingest): contar TODOS los parquet
            // consolidados del pipeline (por metadatos, barato) y compararlos contra SQL.
            var parquetStore = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
            var artifacts = await parquetStore.ListParquetAsync($"{task.PipelineId}/", cancellationToken);
            foreach (var artifact in artifacts)
            {
                var fileName = artifact.Path.Split('/', '\\').Last();
                if (!fileName.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entity = fileName[..^".parquet".Length];
                progress.Report(JobStepCatalog.Compare, results.Count, total: artifacts.Count, item: entity);
                await CompareCountsAsync(parquetStore, entity, artifact.Path);
            }
        }
        // else: corrida incremental sin snapshots nuevos → nada que reconciliar (ver más abajo).

        foreach (var (entity, error) in failed)
        {
            if (error is null)
            {
                _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                    $"{entity}: falta el parquet consolidado; no se concilia.", null, "Event.Reconcile.MissingParquet", entity);
            }
            else
            {
                _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                    $"{entity}: no se pudo conciliar ({error}).", null, "Event.Reconcile.EntityFailed", entity, error);
            }
        }

        // Diff es informativo y no marca el job (ver FinishDiff).
        if (failed.Count > 0 && job.Type != JobType.Diff)
        {
            var current = _jobs.Get(task.JobId)!;
            _jobs.Update(current.Id, current with { Status = JobStatus.Partial });
        }

        // Cada rama reporta antes de procesar cada entidad: el contador final (todas las
        // procesadas) se fija aquí, una sola vez para las tres ramas.
        progress.Report(JobStepCatalog.Compare, results.Count);
        progress.CompleteStep(JobStepCatalog.Compare, results.Count == 0 ? "skipped" : "done");
        progress.StartStep(JobStepCatalog.Report);

        if (job.Type == JobType.Diff)
        {
            FinishDiff(task, results, progress, stageStart);
            return;
        }

        // Nada que comparar (Reconcile sin ningún parquet, o Incremental sin snapshots nuevos): un
        // ReconcileReport vacío pasaría a ser la "última conciliación" del pipeline y dejaría en
        // blanco Reconcile, Tablas y el banner de Pipelines. Se conserva la última real.
        if (results.Count == 0)
        {
            FinishWithNothingToCompare(task, job.Type, progress, stageStart, appendEvent: failed.Count == 0);
            return;
        }

        // Re-sync por tabla: el reporte arrastra las demás entidades del último reconcile para no
        // perder la foto completa del pipeline (ver ReconcileService.BuildScopedReport).
        var report = scopedEntities.Length > 0
            ? _reconcile.BuildScopedReport(task.PipelineId, task.JobId, scopedEntities, results)
            : _reconcile.BuildReport(task.PipelineId, task.JobId, results);

        // Tablas sobre el umbral y no aceptadas: las que alertan y las que la corrección automática
        // re-sincronizaría al cerrar el job (StageRouter). Un re-sync no se corrige a sí mismo.
        var actionable = report.IsPartial ? _reconcile.ActionableEntities(report) : [];
        string? pendingAlert = null;
        if (report.IsPartial)
        {
            var autoAttempt = job.Type == JobType.ResyncTable && AutoRemediation.IsAutomatic(task);
            pendingAlert = await SendOrDeferAlertAsync(new ReconcileAlert
            {
                EventType = ReconcileAlert.Divergence,
                PipelineId = task.PipelineId,
                PipelineName = pipeline.Name,
                JobId = task.JobId,
                Message = autoAttempt
                    ? $"Automatic correction did not resolve the divergence for pipeline {pipeline.Name} (job {task.JobId})."
                    : $"Reconcile divergence exceeded threshold for pipeline {pipeline.Name} (job {task.JobId}).",
                MaxDivergencePct = report.MaxDivergencePct,
                Entities = actionable,
                AutoFixEligible = AutoRemediation.IsEligible(job.Type) && actionable.Count > 0,
                AutoFixAttempted = autoAttempt,
            }, cancellationToken);
        }

        progress.CompleteStep(JobStepCatalog.Report);
        progress.Finish("done");

        job = _jobs.Get(task.JobId)!;
        _jobs.Update(job.Id, job with
        {
            Status = report.IsPartial ? JobStatus.Partial : job.Status,
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["reconcile"] = "done" },
            StageTimings = job.StageTimings.Set("reconcile", started: stageStart, finished: DateTimeOffset.UtcNow),
            Summary = new Dictionary<string, object?>(job.Summary)
            {
                ["maxDivergencePct"] = report.MaxDivergencePct,
                ["reconcilePartial"] = report.IsPartial,
                [AutoRemediation.SummaryKey] = string.Join(",", actionable),
                [AutoRemediation.PendingAlertKey] = pendingAlert,
            },
        });

        _jobs.AppendEvent(task.JobId, EventLevel.Info,
            $"Reconcile completed. Max divergence: {report.MaxDivergencePct:F2}%.",
            null, "Event.Reconcile.Completed", report.MaxDivergencePct);
    }

    // Un aviso que la corrección automática puede resolver no sale de aquí: va al Summary del job y lo
    // manda StageRouter al cerrarlo, después de intentar lanzarla, así dice si se lanzó o no. Devuelve
    // ese aviso en JSON, o null si ya se mandó.
    private async Task<string?> SendOrDeferAlertAsync(ReconcileAlert alert, CancellationToken cancellationToken)
    {
        if (alert.AutoFixEligible && !alert.AutoFixAttempted)
        {
            return JsonSerializer.Serialize(alert, JsonDefaults.Options);
        }

        await _alerts.SendReconcileAlertAsync(alert, cancellationToken);
        return null;
    }

    // Comparación de valores (bajo demanda): hash por fila del parquet consolidado frente a SQL, una
    // tabla cada vez. Tiene su propio reporte (reports/values) y no toca la conciliación de conteos.
    private async Task CompareValuesAsync(
        PipelineTask task,
        string sourceId,
        string pipelineName,
        SqlConnection sql,
        string sqlSchema,
        IReadOnlyList<string> scopedEntities,
        bool excludeDeleteRows,
        StageProgress progress,
        DateTimeOffset stageStart,
        CancellationToken cancellationToken)
    {
        var parquetStore = await _adlsFactory.CreateParquetStoreAsync(sourceId, cancellationToken);
        var entities = scopedEntities.Count > 0
            ? scopedEntities.ToList()
            : await ListConsolidatedEntitiesAsync(parquetStore, task.PipelineId, cancellationToken);

        var results = new List<ValueCompareEntityResult>();
        foreach (var entity in entities)
        {
            progress.Report(JobStepCatalog.Compare, results.Count, total: entities.Count, item: entity, force: true);
            results.Add(await CompareEntityValuesAsync(parquetStore, sql, sqlSchema, task.PipelineId, entity, excludeDeleteRows, cancellationToken));
        }

        progress.Report(JobStepCatalog.Compare, results.Count);
        progress.CompleteStep(JobStepCatalog.Compare, results.Count == 0 ? "skipped" : "done");
        progress.StartStep(JobStepCatalog.Report);

        var report = _reconcile.BuildValuesReport(task.PipelineId, task.JobId, results);
        var actionable = _reconcile.ActionableValueEntities(report);
        var failed = results.Where(r => r.Error is not null).ToList();
        string? pendingAlert = null;
        if (actionable.Count > 0)
        {
            pendingAlert = await SendOrDeferAlertAsync(new ReconcileAlert
            {
                EventType = ReconcileAlert.Values,
                PipelineId = task.PipelineId,
                PipelineName = pipelineName,
                JobId = task.JobId,
                Message = $"Value differences between Parquet and SQL in pipeline {pipelineName} (job {task.JobId}): {string.Join(", ", actionable)}.",
                Entities = actionable,
                AutoFixEligible = true,
            }, cancellationToken);
        }

        progress.CompleteStep(JobStepCatalog.Report);
        progress.Finish("done");

        var job = _jobs.Get(task.JobId)!;
        _jobs.Update(job.Id, job with
        {
            Status = actionable.Count > 0 || failed.Count > 0 ? JobStatus.Partial : job.Status,
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["reconcile"] = "done" },
            StageTimings = job.StageTimings.Set("reconcile", started: stageStart, finished: DateTimeOffset.UtcNow),
            // El detalle del job muestra la comparación por tabla en su cuadrícula de entidades.
            Entities = results.Select(ToJobEntity).ToList(),
            Summary = new Dictionary<string, object?>(job.Summary)
            {
                ["valueDifferences"] = results.Sum(r => r.Different + r.MissingInDestination + r.ExtraInDestination),
                ["valuesPartial"] = actionable.Count > 0,
                [AutoRemediation.SummaryKey] = string.Join(",", actionable),
                [AutoRemediation.PendingAlertKey] = pendingAlert,
            },
        });

        foreach (var error in failed)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Warning, $"{error.Entity}: no se pudo comparar ({error.Error}).", null, "Event.Compare.EntityFailed", error.Entity, error.Error);
        }

        if (results.Count == 0)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                "No hay ningún Parquet consolidado que comparar. Ejecuta el pipeline antes de comparar valores.",
                null, "Event.Compare.NoParquet");
        }
        else
        {
            var withDifferences = results.Count(r => r.HasDifferences);
            _jobs.AppendEvent(task.JobId, EventLevel.Info,
                $"Comparación de valores: {results.Count} tabla(s), {withDifferences} con diferencias.",
                null, "Event.Compare.Summary", results.Count, withDifferences);
        }
    }

    private async Task<ValueCompareEntityResult> CompareEntityValuesAsync(
        IParquetStore parquetStore, SqlConnection sql, string sqlSchema, string pipelineId, string entity, bool excludeDeleteRows, CancellationToken cancellationToken)
    {
        try
        {
            var parquetPath = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity);
            var schemaJson = await parquetStore.ReadTextAsync(IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity), cancellationToken);
            if (schemaJson is null || !await parquetStore.ParquetExistsAsync(parquetPath, cancellationToken))
            {
                return new ValueCompareEntityResult { Entity = entity, Error = "Falta el parquet o el schema consolidado en ADLS." };
            }

            var schema = JsonSerializer.Deserialize<CdmEntitySchema>(schemaJson, JsonDefaults.Options)
                ?? throw new InvalidOperationException($"Schema no válido para {entity}.");

            // Las columnas excluidas no se cargan en SQL: tampoco se comparan.
            schema = ColumnSelection.Apply(schema, _columns?.GetExcluded(pipelineId, entity) ?? new HashSet<string>());

            // Las columnas de etiqueta de enum ([<col>_$label]) se derivan al cargar: no están en el parquet.
            var columns = schema.Attributes.Where(a => a.EnumSourceColumn is null).ToList();
            await using var stream = await parquetStore.OpenParquetReadAsync(parquetPath, cancellationToken);
            var parquetRows = ParquetMaterializer.ReadStreamingAsync(stream, schema, cancellationToken);
            return await ValueComparer.CompareAsync(
                entity,
                columns,
                // Con DeleteMode.Delete las filas de borrado no están en SQL: no se comparan.
                excludeDeleteRows ? parquetRows.Where(row => !DeleteRows.IsDeleteRow(row)) : parquetRows,
                _mergeExecutor.ReadRowsAsync(sql, sqlSchema, entity, columns.Select(c => c.Name).ToList(), cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Con el token cancelado se propaga, aunque el driver de SQL la traduzca a SqlException.
            return new ValueCompareEntityResult { Entity = entity, Error = ex.Message };
        }
    }

    // Solo los consolidados ({pipeline}/{entidad}.parquet), no los parquet por job.
    private static async Task<List<string>> ListConsolidatedEntitiesAsync(IParquetStore parquetStore, string pipelineId, CancellationToken cancellationToken)
    {
        var artifacts = await parquetStore.ListParquetAsync($"{pipelineId}/", cancellationToken);
        return artifacts
            .Select(a => a.Path.Replace('\\', '/').TrimStart('/'))
            .Where(p => p.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path: p, Entity: Path.GetFileNameWithoutExtension(p)))
            .Where(x => x.Path.EndsWith(IngestManifestStore.GetConsolidatedBlobPath(pipelineId, x.Entity), StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Entity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JobEntityResult ToJobEntity(ValueCompareEntityResult r) => new()
    {
        Entity = r.Entity,
        CsvRows = (int)Math.Min(r.SourceRows, int.MaxValue),
        SqlRows = (int)Math.Min(r.DestinationRows, int.MaxValue),
        Status = r.Error is not null ? "error" : r.HasDifferences ? "partial" : "ok",
        Message = r.Error ?? $"{r.Different} distinta(s), {r.MissingInDestination} falta(n) en SQL, {r.ExtraInDestination} sobra(n) en SQL.",
    };

    private void FinishWithNothingToCompare(
        PipelineTask task, JobType type, StageProgress progress, DateTimeOffset stageStart, bool appendEvent)
    {
        progress.CompleteStep(JobStepCatalog.Report, "skipped");
        progress.Finish("done");

        var job = _jobs.Get(task.JobId)!;
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["reconcile"] = "done" },
            StageTimings = job.StageTimings.Set("reconcile", started: stageStart, finished: DateTimeOffset.UtcNow),
        });

        // Si todas las entidades fallaron, sus avisos por tabla ya lo explican: no se añade otro.
        if (!appendEvent)
        {
            return;
        }

        // En un Reconcile explícito es un aviso (no hay Parquet consolidado: falta ejecutar el
        // pipeline); en un Incremental sin datos nuevos es lo normal.
        if (type == JobType.Reconcile)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                "No hay ningún Parquet consolidado que conciliar: se conserva la última conciliación del pipeline. Ejecuta el pipeline antes de conciliar.",
                null, "Event.Reconcile.NoParquet");
        }
        else
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Info,
                "Nada nuevo que conciliar: se conserva la última conciliación del pipeline.",
                null, "Event.Reconcile.NothingNew");
        }
    }

    // Diff es informativo: reporta el delta de filas por entidad (origen - destino) en su propio
    // DiffReport. No toca el ReconcileReport (la "última conciliación" del pipeline), no marca el
    // job Partial y no alerta.
    private void FinishDiff(
        PipelineTask task, IReadOnlyList<ReconcileEntityResult> results, StageProgress progress, DateTimeOffset stageStart)
    {
        var deltas = results
            .Select(r => new DiffEntityResult
            {
                Entity = r.Entity,
                SourceRows = r.SourceRows,
                DestinationRows = r.DestinationRows,
                Delta = r.SourceRows - r.DestinationRows,
            })
            .ToList();
        _reconcile.BuildDiffReport(task.PipelineId, task.JobId, deltas);
        progress.CompleteStep(JobStepCatalog.Report);
        progress.Finish("done");

        var withDifferences = deltas.Count(d => d.Delta != 0);
        var totalDelta = deltas.Sum(d => Math.Abs(d.Delta));
        var job = _jobs.Get(task.JobId)!;
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["reconcile"] = "done" },
            StageTimings = job.StageTimings.Set("reconcile", started: stageStart, finished: DateTimeOffset.UtcNow),
            Summary = new Dictionary<string, object?>(job.Summary)
            {
                ["diffEntities"] = withDifferences,
                ["diffTotalDelta"] = totalDelta,
            },
        });

        _jobs.AppendEvent(task.JobId, EventLevel.Info,
            $"Diff completed: {withDifferences} entity(ies) with differences, {totalDelta} row(s) of total delta.",
            null, "Event.Diff.Completed", withDifferences, totalDelta);
    }
}