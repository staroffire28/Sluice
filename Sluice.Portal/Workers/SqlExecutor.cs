using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Workers;

public interface ISqlExecutor
{
    Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default);
}

public sealed class SqlExecutor : ISqlExecutor
{
    private readonly IJobService _jobs;
    private readonly IPipelineService _pipelines;
    private readonly IngestManifestStore _manifestStore;
    private readonly ISqlConnectionFactory _sqlFactory;
    private readonly ISqlMergeExecutor _mergeExecutor;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly AlertService _alerts;
    private readonly AppOptions _options;
    private readonly ILogger<SqlExecutor> _logger;
    private readonly JobProgressStore? _progressStore;
    private readonly PipelineStateStore? _stateStore;
    private readonly ColumnSelectionStore? _columns;

    public SqlExecutor(
        IJobService jobs,
        IPipelineService pipelines,
        IngestManifestStore manifestStore,
        ISqlConnectionFactory sqlFactory,
        ISqlMergeExecutor mergeExecutor,
        IAdlsClientFactory adlsFactory,
        AlertService alerts,
        IOptions<AppOptions> options,
        ILogger<SqlExecutor> logger,
        JobProgressStore? progress = null,
        PipelineStateStore? stateStore = null,
        ColumnSelectionStore? columns = null)
    {
        _columns = columns;
        _jobs = jobs;
        _pipelines = pipelines;
        _manifestStore = manifestStore;
        _sqlFactory = sqlFactory;
        _mergeExecutor = mergeExecutor;
        _adlsFactory = adlsFactory;
        _alerts = alerts;
        _options = options.Value;
        _logger = logger;
        _progressStore = progress;
        _stateStore = stateStore;
    }

    // Umbral del aviso de posible bloqueo: las sentencias no tienen límite de tiempo (ver
    // SqlConnectionFactory.ForPipeline), así que si la etapa pasa este tiempo sin avanzar se avisa.
    internal TimeSpan StallAlertAfter { get; init; } = TimeSpan.FromHours(1);

    public async Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(task.JobId)
            ?? throw new InvalidOperationException($"Job '{task.JobId}' not found.");

        var pipeline = _pipelines.Get(task.PipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{task.PipelineId}' not found.");

        // `job` queda obsoleto tras este Update: la salida temprana lo reutiliza, así que repite
        // el inicio junto al fin para no perderlo.
        var stageStart = DateTimeOffset.UtcNow;
        _jobs.AppendEvent(task.JobId, EventLevel.Info, JobEventMarkers.SqlStarted, null, "Event.Sql.Started");
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["sql"] = "running" },
            StageTimings = job.StageTimings.Set("sql", started: stageStart),
        });
        var progress = _progressStore?.Begin(task.PipelineId, task.JobId, "sql", cancellationToken) ?? StageProgress.None;
        progress.StartStep(JobStepCatalog.Prepare);

        var manifest = _manifestStore.Load(task.PipelineId, task.JobId);
        if (manifest is null || manifest.Entities.Count == 0)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Info, JobEventMarkers.SqlNoManifest, null, "Event.Sql.NoManifest");
            _jobs.Update(job.Id, job with
            {
                StageStatus = new Dictionary<string, string>(job.StageStatus) { ["sql"] = "done" },
                StageTimings = job.StageTimings.Set("sql", started: stageStart, finished: DateTimeOffset.UtcNow),
            });
            progress.CompleteStep(JobStepCatalog.Prepare);
            progress.SkipRemaining();
            progress.Finish("done");
            return;
        }

        await using var connection = await _sqlFactory.OpenAsync(pipeline.DestinationId, cancellationToken);
        var schema = _sqlFactory.GetSchema(pipeline.DestinationId);
        var sqlVersion = _sqlFactory.GetSqlVersion(pipeline.DestinationId);
        var parquetStore = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);

        // Traducción de enums (opt-in): se lee el optionset del snapshot más reciente UNA vez por
        // job y se usa para añadir las columnas [<col>_$label] a cada tabla. Si falla o no existe,
        // se continúa sin traducir (no se aborta el MERGE por un metadata ausente).
        var optionsetMetadata = await LoadOptionsetMetadataAsync(
            pipeline.SourceId, pipeline.Id, manifest, task.JobId, cancellationToken);
        progress.CompleteStep(JobStepCatalog.Prepare);

        var entityResults = new List<JobEntityResult>();
        var totalMerged = 0;
        var entitySqlRows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Re-sync por tabla en modo Reload: tras el upsert se borran las filas huérfanas (Id que
        // ya no está en el parquet) para garantizar SQL == parquet en ambas direcciones. NO se
        // vacía la tabla antes: el upsert la mantiene poblada y el borrado va en un solo DELETE,
        // así no hay ventana en la que la tabla quede vacía si el worker cae. En Remerge o en
        // cualquier otro job no se borran huérfanas (solo upsert).
        var reload = job.Type is JobType.Force or JobType.ReloadFromParquet
            || (task.Payload.TryGetValue("resyncMode", out var modeRaw)
                && string.Equals(modeRaw?.ToString(), nameof(ResyncMode.Reload), StringComparison.OrdinalIgnoreCase));

        // Un Incremental mergea solo el delta de su corrida. Si el último job terminado no completó
        // SQL, sus filas pueden no estar en la tabla: antes un "Ejecutar" lo reparaba remergeando
        // el consolidado entero, así que en ese caso se sigue haciendo.
        var useDelta = job.Type == JobType.Incremental && !reload && PreviousSqlCompleted(task);

        var failedEntities = 0;
        progress.StartStep(JobStepCatalog.Merge, total: manifest.Entities.Count);
        using var stallWatch = new SqlStallWatch(StallAlertAfter, (step, elapsed) => WarnPossibleBlock(pipeline, task.JobId, step, elapsed), _logger);
        foreach (var entry in manifest.Entities)
        {
            stallWatch.Touch($"{entry.Entity} · ensureTable");
            progress.Report(JobStepCatalog.Merge, entityResults.Count, item: entry.Entity, phase: "ensureTable", failed: failedEntities, rows: totalMerged, force: true);
            try
            {
                // Schema y parquet son durables en ADLS: se leen desde ahí, no del disco local.
                var schemaJson = string.IsNullOrWhiteSpace(entry.SchemaPath)
                    ? null
                    : await parquetStore.ReadTextAsync(entry.SchemaPath, cancellationToken);

                if (schemaJson is null || !await parquetStore.ParquetExistsAsync(entry.ParquetPath, cancellationToken))
                {
                    entityResults.Add(new JobEntityResult
                    {
                        Entity = entry.Entity,
                        CsvRows = entry.CsvRows,
                        Status = "skipped",
                        Message = "Missing parquet or schema in ADLS.",
                    });
                    continue;
                }

                var cdmSchema = JsonSerializer.Deserialize<CdmEntitySchema>(schemaJson, JsonDefaults.Options)
                    ?? throw new InvalidOperationException($"Invalid schema for {entry.Entity}.");

                // Columnas excluidas en el catálogo: el parquet las trae, pero no se cargan en SQL.
                var excludedColumns = _columns?.GetExcluded(task.PipelineId, entry.Entity) ?? new HashSet<string>();
                cdmSchema = ColumnSelection.Apply(cdmSchema, excludedColumns);

                if (optionsetMetadata is not null)
                {
                    cdmSchema = EnumLabelEnricher.Apply(cdmSchema, optionsetMetadata);
                }

                await _mergeExecutor.EnsureTableAsync(connection, schema, entry.Entity, cdmSchema, sqlVersion, cancellationToken);
                if (excludedColumns.Count > 0)
                {
                    // No se borran de SQL (romperían informes que las usen): quedan con su último valor.
                    var existing = await _mergeExecutor.GetColumnsAsync(connection, schema, entry.Entity, cancellationToken);
                    var stale = excludedColumns.Where(existing.Contains).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                    if (stale.Count > 0)
                    {
                        _jobs.AppendEvent(task.JobId, EventLevel.Info,
                            $"{entry.Entity}: {stale.Count} columna(s) excluida(s) siguen en la tabla SQL y ya no se actualizan ({string.Join(", ", stale)}): conservan su último valor y quedan vacías en las filas nuevas.",
                            null, "Event.Sql.ExcludedColumnsRemain", entry.Entity, stale.Count, string.Join(", ", stale));
                    }
                }
                if (!await _mergeExecutor.EnsureUniqueIdIndexAsync(connection, schema, entry.Entity, cancellationToken))
                {
                    _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                        $"{entry.Entity}: la tabla destino tiene Ids duplicados; no se crea el índice único sobre [Id] (el MERGE sigue sin él).",
                        null, "Event.Sql.DuplicateIds", entry.Entity);
                }

                // Origen y modo: el delta (solo lo nuevo, sin reescribir lo igual) si la tabla ya
                // tiene datos; si está vacía, el consolidado con inserción directa (el delta no la
                // dejaría completa); en cualquier otro caso, el consolidado con MERGE incondicional.
                var targetRows = await _mergeExecutor.CountRowsAsync(connection, schema, entry.Entity, sqlVersion, cancellationToken);
                var deltaPath = _manifestStore.GetDeltaPath(task.PipelineId, task.JobId, entry.Entity);
                var fromDelta = useDelta && entry.HasDelta && targetRows > 0 && File.Exists(deltaPath);
                var mode = fromDelta ? MergeMode.UpsertNewer
                    : targetRows == 0 ? MergeMode.InsertIntoEmpty
                    : MergeMode.Upsert;

                // MERGE en streaming: se lee por lotes (un row group a la vez) y se mergea por
                // lote, sin cargar la tabla entera en memoria. En modo Reload las claves de cada
                // lote (solo los Id, no las filas) se vuelcan a SQL tras su MERGE para borrar
                // huérfanas al final; tampoco se juntan en memoria.
                stallWatch.Touch($"{entry.Entity} · merge");
                progress.Report(JobStepCatalog.Merge, entityResults.Count, item: entry.Entity, phase: "merge", failed: failedEntities);
                var mergeBatch = SqlMergeExecutor.BatchSize;
                var merged = 0;
                await using var reloadKeys = reload
                    ? _mergeExecutor.CreateSourceKeySink(connection, schema, entry.Entity, cdmSchema, sqlVersion)
                    : null;
                // DeleteMode.Delete: las filas de borrado no se cargan; sus Id se borran al final.
                var deleteKeys = pipeline.DeleteMode == DeleteMode.Delete ? new List<object?>() : null;
                await using (var parquetStream = fromDelta
                    ? File.OpenRead(deltaPath)
                    : await parquetStore.OpenParquetReadAsync(entry.ParquetPath, cancellationToken))
                {
                    var batch = new List<Dictionary<string, object?>>(mergeBatch);
                    await foreach (var row in ParquetMaterializer.ReadStreamingAsync(parquetStream, cdmSchema, cancellationToken))
                    {
                        if (deleteKeys is not null && DeleteRows.IsDeleteRow(row))
                        {
                            if (row.TryGetValue("Id", out var deletedId))
                            {
                                deleteKeys.Add(deletedId);
                            }

                            continue;
                        }

                        batch.Add(row);
                        if (batch.Count >= mergeBatch)
                        {
                            merged += await _mergeExecutor.MergeEntityAsync(connection, schema, entry.Entity, cdmSchema, batch, sqlVersion, mode, cancellationToken);
                            await AddKeysAsync(reloadKeys, batch, cancellationToken);
                            batch = new List<Dictionary<string, object?>>(mergeBatch);
                            stallWatch.Touch($"{entry.Entity} · merge");
                            // Filas en vivo durante una tabla grande (con throttle: misma entidad y fase).
                            progress.Report(JobStepCatalog.Merge, entityResults.Count, item: entry.Entity, phase: "merge", failed: failedEntities, rows: totalMerged + merged);
                        }
                    }

                    if (batch.Count > 0)
                    {
                        merged += await _mergeExecutor.MergeEntityAsync(connection, schema, entry.Entity, cdmSchema, batch, sqlVersion, mode, cancellationToken);
                        await AddKeysAsync(reloadKeys, batch, cancellationToken);
                    }
                }

                if (reloadKeys is { Count: 0 })
                {
                    _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                        $"{entry.Entity}: el parquet no trae claves; no se eliminan huérfanas para no vaciar la tabla.",
                        null, "Event.Sql.NoReloadKeys", entry.Entity);
                }
                else if (reloadKeys is not null)
                {
                    stallWatch.Touch($"{entry.Entity} · orphans");
                    progress.Report(JobStepCatalog.Merge, entityResults.Count, item: entry.Entity, phase: "orphans", failed: failedEntities);
                    var removed = await reloadKeys.DeleteOrphansAsync(cancellationToken);
                    if (removed > 0)
                    {
                        _jobs.AppendEvent(task.JobId, EventLevel.Info, $"{entry.Entity}: {removed} fila(s) huérfana(s) eliminada(s) (recarga).",
                            null, "Event.Sql.OrphansDeleted", entry.Entity, removed);
                    }
                }

                // Una tabla vacía no tiene nada que borrar (InsertIntoEmpty ya las saltó).
                if (deleteKeys is { Count: > 0 } && mode != MergeMode.InsertIntoEmpty)
                {
                    stallWatch.Touch($"{entry.Entity} · deletes");
                    progress.Report(JobStepCatalog.Merge, entityResults.Count, item: entry.Entity, phase: "deletes", failed: failedEntities);
                    var deleted = await _mergeExecutor.DeleteKeysAsync(connection, schema, entry.Entity, cdmSchema, deleteKeys, sqlVersion, cancellationToken);
                    if (deleted > 0)
                    {
                        _jobs.AppendEvent(task.JobId, EventLevel.Info, $"{entry.Entity}: {deleted} fila(s) borrada(s) en F&O eliminada(s) del destino.",
                            null, "Event.Sql.SourceDeletesApplied", entry.Entity, deleted);
                    }
                }

                stallWatch.Touch($"{entry.Entity} · count");
                progress.Report(JobStepCatalog.Merge, entityResults.Count, item: entry.Entity, phase: "count", failed: failedEntities);
                var sqlRows = await _mergeExecutor.CountRowsAsync(connection, schema, entry.Entity, sqlVersion, cancellationToken);
                totalMerged += merged;
                entitySqlRows[entry.Entity] = sqlRows;

                entityResults.Add(new JobEntityResult
                {
                    Entity = entry.Entity,
                    CsvRows = entry.CsvRows,
                    SqlRows = (int)Math.Min(sqlRows, int.MaxValue),
                    Status = "ok",
                    Watermark = entry.Watermark,
                    Message = $"Merged {merged} row(s).",
                });

                _jobs.AppendEvent(task.JobId, EventLevel.Info, $"{entry.Entity}: merged {merged}{(fromDelta ? " (delta)" : "")}, table count {sqlRows}.",
                    null, fromDelta ? "Event.Sql.EntityMergedDelta" : "Event.Sql.EntityMerged", entry.Entity, merged, sqlRows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Una cancelación del worker (SIGTERM/redeploy) no es un fallo de la entidad: se
                // propaga para que la tarea se reintente en vez de marcar el job sano como Error.
                // Una entidad que falla (container parquet sin configurar, schema corrupto,
                // error SQL) NO debe tumbar toda la etapa ni terminar en silencio: se marca
                // 'error', se registra, y el resto de entidades continúa.
                failedEntities++;
                var code = KnownErrors.CodeFrom(ex);
                entityResults.Add(new JobEntityResult
                {
                    Entity = entry.Entity,
                    CsvRows = entry.CsvRows,
                    Status = "error",
                    Message = $"{ex.GetType().Name}: {ex.Message}",
                    Code = code,
                });
                // Stack completo al log del container para poder diagnosticar el origen real
                // (p. ej. un "Access to the path is denied" transitorio de Windows).
                _logger.LogError(ex, "SQL stage failed for entity {Entity}", entry.Entity);
                _jobs.AppendEvent(task.JobId, EventLevel.Error, $"{entry.Entity}: SQL stage failed — {ex.GetType().Name}: {ex.Message}", code,
                    "Event.Sql.EntityFailed", entry.Entity, ex.GetType().Name, ex.Message);
            }
        }

        progress.Report(JobStepCatalog.Merge, entityResults.Count, failed: failedEntities, rows: totalMerged);
        progress.CompleteStep(JobStepCatalog.Merge);

        // Un Force/ReloadFromParquet (o el re-sync en modo Reload) no pasa por el ingest, que es
        // quien fija el conteo de Tablas: tras la recarga se toma el conteo real de SQL.
        if (reload && _stateStore is not null && entitySqlRows.Count > 0)
        {
            var state = _stateStore.GetOrCreate(task.PipelineId);
            var tables = new Dictionary<string, TableState>(state.Tables, StringComparer.OrdinalIgnoreCase);
            foreach (var (entity, rows) in entitySqlRows)
            {
                if (tables.TryGetValue(entity, out var table))
                {
                    tables[entity] = table with { RowCount = rows };
                }
            }

            // Las tablas recargadas ya no esperan la recarga de un punto de restauración.
            _stateStore.Save(state with
            {
                Tables = tables,
                PendingReload = state.PendingReload.Where(e => !entitySqlRows.ContainsKey(e)).ToList(),
            });
        }

        // El delta solo sirve dentro de este job: se borra al terminar la etapa (vaya bien o mal;
        // un reintento recarga desde el consolidado). Una cancelación sale antes por excepción
        // y lo conserva para la réplica que reclame la tarea.
        TryDeleteDirectory(_manifestStore.GetDeltaDirectory(task.PipelineId, task.JobId));

        var hasEntityError = entityResults.Any(e =>
            string.Equals(e.Status, "error", StringComparison.OrdinalIgnoreCase));
        var hasEntityOk = entityResults.Any(e =>
            string.Equals(e.Status, "ok", StringComparison.OrdinalIgnoreCase));
        var sqlStageStatus = hasEntityError ? "error" : "done";
        JobStatus? jobStatusAfterSql = hasEntityError
            ? hasEntityOk ? JobStatus.Partial : JobStatus.Error
            : null;

        job = _jobs.Get(task.JobId)!;
        var updated = job with
        {
            Entities = JobEntityResultMerger.Merge(job.Entities, entityResults),
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["sql"] = sqlStageStatus },
            StageTimings = job.StageTimings.Set("sql", started: stageStart, finished: DateTimeOffset.UtcNow),
            Summary = new Dictionary<string, object?>(job.Summary)
            {
                ["sqlMergedRows"] = totalMerged,
            },
        };
        if (jobStatusAfterSql is not null)
        {
            updated = updated with { Status = jobStatusAfterSql.Value };
            if (jobStatusAfterSql == JobStatus.Error)
            {
                // Todas las entidades fallaron: no hay reconcile; cerrar el job aquí.
                updated = updated with
                {
                    CurrentStage = null,
                    FinishedAt = DateTimeOffset.UtcNow,
                };
            }
        }

        progress.Finish(sqlStageStatus, failedStep: JobStepCatalog.Merge);
        _jobs.Update(job.Id, updated);

        if (jobStatusAfterSql == JobStatus.Error)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Error,
                $"SQL MERGE completed with entity errors ({totalMerged} row(s) merged) — job marked Error.",
                null, "Event.Sql.CompletedWithErrors", totalMerged);
        }
        else if (jobStatusAfterSql == JobStatus.Partial)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                $"SQL MERGE completed with partial entity failures ({totalMerged} row(s) merged).",
                null, "Event.Sql.CompletedPartial", totalMerged);
        }
        else
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Info, $"SQL MERGE completed ({totalMerged} row(s)).", null, "Event.Sql.Completed", totalMerged);
        }

        if (jobStatusAfterSql is JobStatus.Partial or JobStatus.Error)
        {
            await SendEntityFailureAlertAsync(
                pipeline, job, task.JobId, entityResults, totalMerged, jobStatusAfterSql.Value, cancellationToken);
        }
    }

    // Claves (Id) de un lote ya mergeado, para el borrado de huérfanas del Reload.
    private static Task AddKeysAsync(ISourceKeySink? keys, List<Dictionary<string, object?>> batch, CancellationToken cancellationToken) =>
        keys is null
            ? Task.CompletedTask
            : keys.AddAsync(batch.Where(r => r.ContainsKey("Id")).Select(r => r["Id"]).ToList(), cancellationToken);

    // ¿Completó SQL el último job terminado del pipeline que carga todas sus tablas? Se ignoran los
    // que no tienen etapa SQL (Reconcile, Diff) y los ResyncTable: cargan una sola tabla, así que
    // terminar bien no repara lo que un Incremental anterior dejó sin cargar en las demás. Sin
    // jobs previos no hay nada que reparar. Se recorre el índice del más reciente al más antiguo y
    // solo se lee el job.json de los candidatos hasta dar con uno que pasó por SQL (casi siempre el
    // primero): antes se leían hasta 500 job.json enteros en cada etapa SQL.
    private bool PreviousSqlCompleted(PipelineTask task)
    {
        var previous = _jobs.ListIndex(task.PipelineId)
            .Where(e => e.Id != task.JobId && e.FinishedAt is not null
                && e.Type is JobType.Incremental or JobType.Force or JobType.ReloadFromParquet)
            .Select(e => _jobs.Get(e.Id))
            .FirstOrDefault(j => j is not null && j.FinishedAt is not null
                && j.StageStatus.GetValueOrDefault("sql") is not ("skipped" or null));
        if (previous is null || previous.StageStatus["sql"] == "done")
        {
            return true;
        }

        _jobs.AppendEvent(task.JobId, EventLevel.Info,
            $"El último job ({previous.Id}) no completó la etapa SQL: este Incremental mergea el consolidado completo para reponer lo que faltara.",
            null, "Event.Sql.FullMergeAfterIncomplete", previous.Id);
        return false;
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException ex)
        {
            // Si queda, lo limpia la retención con la carpeta del job.
            _logger.LogWarning(ex, "No se pudo borrar el delta {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "No se pudo borrar el delta {Path}", path);
        }
    }

    // Lo llama SqlStallWatch desde su temporizador: la sentencia sigue en curso (no se corta), solo
    // se deja constancia en la línea de tiempo del job y se manda la alerta.
    private void WarnPossibleBlock(Pipeline pipeline, string jobId, string step, TimeSpan elapsed)
    {
        var message = $"{step}: lleva {elapsed:h\\:mm\\:ss} sin avanzar en SQL (posible bloqueo). La sentencia sigue en curso; " +
            "revisa los bloqueos en el destino (sys.dm_exec_requests, columna blocking_session_id).";
        _jobs.AppendEvent(jobId, EventLevel.Warning, message, null, "Event.Sql.PossibleBlock", step, elapsed.ToString(@"h\:mm\:ss"));
        _ = SendStallAlertAsync($"Pipeline {pipeline.Name} (job {jobId}): {message}");
    }

    private async Task SendStallAlertAsync(string message)
    {
        try
        {
            await _alerts.SendAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo enviar la alerta de posible bloqueo en SQL.");
        }
    }

    private async Task SendEntityFailureAlertAsync(
        Pipeline pipeline,
        Job job,
        string jobId,
        IReadOnlyList<JobEntityResult> entityResults,
        int totalMerged,
        JobStatus jobStatus,
        CancellationToken cancellationToken)
    {
        var failed = entityResults
            .Where(e => string.Equals(e.Status, "error", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (failed.Count == 0)
        {
            return;
        }

        var detail = string.Join("; ", failed.Select(e => $"{e.Entity}: {e.Message}"));
        var summary = jobStatus == JobStatus.Error
            ? $"SQL MERGE: ninguna entidad se cargó ({failed.Count}/{entityResults.Count} con error, {totalMerged} fila(s) mergeadas)."
            : $"SQL MERGE: fallos parciales ({failed.Count}/{entityResults.Count} entidades, {totalMerged} fila(s) mergeadas).";

        await _alerts.SendPipelineFailureAsync(new PipelineFailureAlert
        {
            PipelineId = pipeline.Id,
            PipelineName = pipeline.Name,
            JobId = jobId,
            JobType = job.Type.ToString(),
            Stage = "sql",
            Message = $"{summary} {detail}",
            // Fallos parciales: el job queda en Partial, es un aviso y no un fallo.
            Status = jobStatus == JobStatus.Partial ? PipelineFailureAlert.Warning : PipelineFailureAlert.Error,
        }, cancellationToken);
    }

    // Carga (opt-in) el optionset del snapshot más reciente del manifest para la traducción de
    // enums. Devuelve null cuando el flag está apagado, no hay snapshot, el archivo no existe, o
    // la lectura falla: en todos esos casos el MERGE sigue sin columnas _$label.
    private async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>>?>
        LoadOptionsetMetadataAsync(
            string sourceId,
            string pipelineId,
            IngestManifest manifest,
            string jobId,
            CancellationToken cancellationToken)
    {
        if (!_options.TranslateEnums)
        {
            return null;
        }

        var pipelineState = _pipelines.GetState(pipelineId);
        string? settledSnapshot = null;
        try
        {
            var adlsProbe = await _adlsFactory.CreateAsync(sourceId, cancellationToken);
            settledSnapshot = await adlsProbe.GetSettledSnapshotAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No se pudo leer settled snapshot para resolver optionset en pipeline {PipelineId}", pipelineId);
        }

        var snapshot = OptionsetSnapshotResolver.Resolve(
            manifest, pipelineState.LastProcessedSnapshot, settledSnapshot);

        if (string.IsNullOrEmpty(snapshot))
        {
            _jobs.AppendEvent(jobId, EventLevel.Warning,
                "Traducción de enums activa pero no se pudo resolver snapshot " +
                "(manifest vacío, sin last-processed ni settled). Se continúa sin columnas _$label.",
                null, "Event.Enums.NoSnapshot");
            return null;
        }

        try
        {
            var adls = await _adlsFactory.CreateAsync(sourceId, cancellationToken);
            var csv = await adls.ReadGlobalOptionsetMetadataCsvAsync(snapshot, cancellationToken);
            if (string.IsNullOrWhiteSpace(csv))
            {
                _jobs.AppendEvent(jobId, EventLevel.Info,
                    $"Traducción de enums activa pero no se halló GlobalOptionsetMetadata.csv en el snapshot {snapshot}.",
                    null, "Event.Enums.NoMetadataFile", snapshot);
                return null;
            }

            var metadata = OptionsetMetadataReader.Parse(csv);
            if (metadata.Count == 0)
            {
                // El archivo tenía contenido pero no produjo ningún optionset: probable cambio de
                // orden de columnas, truncamiento o idioma != 1033. No es fatal, pero sin esto el
                // job reportaría "0 entidades" como Info y la pérdida de etiquetas pasaría inadvertida.
                _jobs.AppendEvent(jobId, EventLevel.Warning,
                    $"Traducción de enums: GlobalOptionsetMetadata.csv del snapshot {snapshot} no produjo optionsets " +
                    "(¿formato cambiado, truncado o idioma != 1033?). Se continúa sin columnas _$label.",
                    null, "Event.Enums.EmptyMetadata", snapshot);
                return null;
            }

            _jobs.AppendEvent(jobId, EventLevel.Info,
                $"Traducción de enums: {metadata.Count} entidad(es) con optionsets cargadas del snapshot {snapshot}.",
                null, "Event.Enums.Loaded", metadata.Count, snapshot);
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo cargar el optionset metadata para traducción de enums.");
            _jobs.AppendEvent(jobId, EventLevel.Warning,
                $"No se pudo cargar la traducción de enums: {ex.Message}. Se continúa sin columnas _$label.",
                null, "Event.Enums.LoadFailed", ex.Message);
            return null;
        }
    }
}