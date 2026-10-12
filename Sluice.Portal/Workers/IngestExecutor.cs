using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Workers;

public interface IIngestExecutor
{
    Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default);
}

public sealed class IngestExecutor : IIngestExecutor
{
    // Mismo tamaño de lote que ParquetConsolidator/SqlExecutor: cada lote se escribe como un row
    // group propio en el parquet transitorio, acotando el pico de memoria a un lote (no al CSV
    // fuente completo, sin importar cuántas filas/MB tenga).
    private const int StreamBatchSize = 5000;

    private readonly IJobService _jobs;
    private readonly IPipelineService _pipelines;
    private readonly PipelineStateStore _stateStore;
    private readonly TablePauseStore _pauseStore;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly IngestManifestStore _manifestStore;
    private readonly AppOptions _options;
    private readonly ILogger<IngestExecutor> _logger;
    private readonly JobProgressStore? _progressStore;

    public IngestExecutor(
        IJobService jobs,
        IPipelineService pipelines,
        PipelineStateStore stateStore,
        TablePauseStore pauseStore,
        IAdlsClientFactory adlsFactory,
        IngestManifestStore manifestStore,
        IOptions<AppOptions> options,
        ILogger<IngestExecutor> logger,
        JobProgressStore? progress = null)
    {
        _jobs = jobs;
        _pipelines = pipelines;
        _stateStore = stateStore;
        _pauseStore = pauseStore;
        _adlsFactory = adlsFactory;
        _manifestStore = manifestStore;
        _options = options.Value;
        _logger = logger;
        _progressStore = progress;
    }

    public async Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(task.JobId)
            ?? throw new InvalidOperationException($"Job '{task.JobId}' not found.");

        var pipeline = _pipelines.Get(task.PipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{task.PipelineId}' not found.");

        // `job` queda obsoleto tras este Update (no se relee): las salidas tempranas de abajo
        // guardan esa misma copia, así que repiten el inicio junto al fin para no perderlo.
        var stageStart = DateTimeOffset.UtcNow;
        _jobs.AppendEvent(task.JobId, EventLevel.Info, JobEventMarkers.IngestStarted);
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["ingest"] = "running" },
            StageTimings = job.StageTimings.Set("ingest", started: stageStart),
        });
        var progress = _progressStore?.Begin(task.PipelineId, task.JobId, "ingest", cancellationToken) ?? StageProgress.None;
        progress.StartStep(JobStepCatalog.Discover);

        // Un solo cliente ADLS: implementa IAdlsClient (lectura del origen) e IParquetStore
        // (escritura del parquet/schema durable). Evita doble construcción y doble descifrado
        // del client_secret por tarea de ingest.
        var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
        var parquetStore = adls as IParquetStore
            ?? throw new InvalidOperationException("El cliente ADLS no soporta almacenamiento de parquet.");
        var pipelineState = _stateStore.GetOrCreate(task.PipelineId);
        var settled = await adls.GetSettledSnapshotAsync(cancellationToken);
        var snapshots = await adls.ListSnapshotFoldersAsync(cancellationToken);
        var isForce = job.Type == JobType.Force;
        var selection = SnapshotSelector.Select(
            snapshots,
            isForce ? null : pipelineState.LastProcessedSnapshot,
            settled);

        if (selection.Blocked)
        {
            _jobs.Update(job.Id, job with
            {
                Status = JobStatus.Blocked,
                FinishedAt = DateTimeOffset.UtcNow,
                StageStatus = new Dictionary<string, string>(job.StageStatus) { ["ingest"] = "blocked" },
                StageTimings = job.StageTimings.Set("ingest", started: stageStart, finished: DateTimeOffset.UtcNow),
            });
            _jobs.AppendEvent(task.JobId, EventLevel.Warning, selection.BlockReason ?? "Blocked.");
            progress.CompleteStep(JobStepCatalog.Discover, "blocked");
            progress.Finish("blocked");
            return;
        }

        progress.CompleteStep(JobStepCatalog.Discover);

        if (selection.ToProcess.Count == 0)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Info, JobEventMarkers.NoNewSnapshots);
            _jobs.Update(job.Id, job with
            {
                StageStatus = new Dictionary<string, string>(job.StageStatus) { ["ingest"] = "done" },
                StageTimings = job.StageTimings.Set("ingest", started: stageStart, finished: DateTimeOffset.UtcNow),
                // Mismas claves que el cierre normal: la UI distingue "sin datos nuevos" por
                // snapshotsSelected = 0 sin depender del texto del evento.
                Summary = new Dictionary<string, object?>
                {
                    ["snapshotsProcessed"] = 0,
                    ["snapshotsSelected"] = 0,
                },
            });
            _manifestStore.Save(new IngestManifest
            {
                JobId = task.JobId,
                PipelineId = task.PipelineId,
                LastProcessedSnapshot = pipelineState.LastProcessedSnapshot,
            });
            progress.SkipRemaining();
            progress.Finish("done");
            return;
        }

        // Fase 1 — por snapshot (memoria acotada a un snapshot): cada snapshot/entidad se
        // transcodifica a un parquet transitorio LOCAL (un row group). No se acumula el
        // dataset en RAM; la deduplicación entre snapshots se hace luego en la consolidación.
        var entitySchemas = new Dictionary<string, CdmEntitySchema>(StringComparer.OrdinalIgnoreCase);
        var transientPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var transientIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entityCsvRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Acumula TODOS los fallos por entidad (uno por snapshot fallido), no solo el último —
        // así una entidad que falla en un snapshot y tiene éxito en otro no pierde el rastro del
        // fallo (ver partiallyFailedEntities más abajo).
        var entityFailures = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var totalCsvRows = 0;
        var snapshotsProcessed = 0;
        SnapshotIntegrity? incomplete = null;
        string? lastSnapshot = pipelineState.LastProcessedSnapshot;

        // Tablas con sincronización pausada por el usuario (default: ninguna). Se omiten en
        // ingest, así no llegan a la consolidación ni al MERGE de SQL; su estado/watermark
        // previo se conserva intacto (updatedTables parte de pipelineState.Tables más abajo).
        var pausedTables = _pauseStore.GetPaused(task.PipelineId);
        var skippedPaused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        progress.StartStep(JobStepCatalog.Snapshots, total: selection.ToProcess.Count);
        foreach (var snapshot in selection.ToProcess)
        {
            // Opción B: la carpeta ya está sellada por el changelog, pero validamos que el
            // volcado de Synapse esté completo. Si no lo está, paramos ANTES de marcarla como
            // procesada: el prefijo sano ya transcodificado sí se consolida y el watermark
            // queda en el último snapshot íntegro, así que el siguiente ciclo reintenta este.
            var integrity = await adls.ValidateSnapshotAsync(snapshot, cancellationToken);
            if (!integrity.IsComplete)
            {
                incomplete = integrity;
                _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                    $"Stopping at snapshot {snapshot}: {integrity.Reason} Will retry on the next run.");
                break;
            }

            _jobs.AppendEvent(task.JobId, EventLevel.Info, $"Processing snapshot {snapshot}.");
            progress.Report(JobStepCatalog.Snapshots, snapshotsProcessed, item: snapshot, failed: entityFailures.Count, rows: totalCsvRows, force: true);
            var entities = await adls.ListEntitiesAsync(snapshot, cancellationToken);

            foreach (var entity in entities)
            {
                if (pausedTables.Contains(entity))
                {
                    skippedPaused.Add(entity);
                    continue;
                }

                // Sin force: con cientos de entidades por snapshot basta un volcado por segundo.
                progress.Report(JobStepCatalog.Snapshots, snapshotsProcessed, item: $"{snapshot} · {entity}", failed: entityFailures.Count, rows: totalCsvRows);

                try
                {
                    var schema = await adls.GetEntitySchemaAsync(entity, snapshot, settled, cancellationToken);
                    entitySchemas[entity] = schema;

                    // Streaming de punta a punta: leer CSV línea a línea (AdlsClient), parsear
                    // línea a línea (CsvEntityReader) y escribir el parquet transitorio por
                    // lotes (ParquetMaterializer.WriteStreamingAsync, un row group por lote). El
                    // pico de memoria queda acotado a un lote de StreamBatchSize filas, sin
                    // importar el tamaño del CSV fuente (antes se materializaba todo el archivo
                    // en un string + una lista completa de filas, lo que causaba
                    // OutOfMemoryException en tablas con un único CSV de cientos de MB).
                    var lines = adls.StreamEntityCsvLinesAsync(snapshot, entity, cancellationToken);
                    var rows = CsvEntityReader.ReadRowsAsync(lines, schema, cancellationToken);

                    var index = transientIndex.GetValueOrDefault(entity);
                    var transientPath = _manifestStore.GetStagingTransientPath(task.JobId, entity, index);
                    Directory.CreateDirectory(Path.GetDirectoryName(transientPath)!);

                    var rowsWritten = 0;

                    async IAsyncEnumerable<IReadOnlyList<Dictionary<string, object?>>> BatchesAsync()
                    {
                        var batch = new List<Dictionary<string, object?>>(StreamBatchSize);
                        await foreach (var row in rows)
                        {
                            batch.Add(row);
                            rowsWritten++;
                            if (batch.Count >= StreamBatchSize)
                            {
                                yield return batch;
                                batch = new List<Dictionary<string, object?>>(StreamBatchSize);
                                // Filas leídas en vivo dentro de una entidad grande (un solo CSV
                                // puede tardar minutos); mismo elemento, así que con throttle.
                                progress.Report(JobStepCatalog.Snapshots, snapshotsProcessed, item: $"{snapshot} · {entity}",
                                    failed: entityFailures.Count, rows: totalCsvRows + rowsWritten);
                            }
                        }

                        if (batch.Count > 0)
                        {
                            yield return batch;
                        }
                    }

                    await using (var output = File.Create(transientPath))
                    {
                        await ParquetMaterializer.WriteStreamingAsync(output, schema, BatchesAsync(), cancellationToken);
                    }

                    if (rowsWritten == 0)
                    {
                        // Entidad sin filas en este snapshot (normal: tabla sin movimientos en la
                        // ventana) — no se registra como transitorio, mismo criterio que antes.
                        File.Delete(transientPath);
                        continue;
                    }

                    totalCsvRows += rowsWritten;
                    entityCsvRows[entity] = entityCsvRows.GetValueOrDefault(entity) + rowsWritten;
                    transientIndex[entity] = index + 1;

                    if (!transientPaths.TryGetValue(entity, out var list))
                    {
                        list = [];
                        transientPaths[entity] = list;
                    }

                    list.Add(transientPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // La cancelación del worker (SIGTERM/redeploy) no es un fallo de la entidad:
                    // se propaga para no dar el snapshot por procesado con entidades "fallidas".
                    // Una entidad con CSV corrupto, schema inválido o un fallo puntual de lectura
                    // NO debe abortar el resto de entidades/snapshots del job: se registra el
                    // fallo puntual y se continúa (mismo criterio de aislamiento por entidad que
                    // ya usa SqlExecutor).
                    if (!entityFailures.TryGetValue(entity, out var failuresForEntity))
                    {
                        failuresForEntity = [];
                        entityFailures[entity] = failuresForEntity;
                    }

                    failuresForEntity.Add($"snapshot {snapshot}: {ex.GetType().Name}: {ex.Message}");
                    _logger.LogError(ex, "Ingest stage failed for entity {Entity} in snapshot {Snapshot}", entity, snapshot);
                    _jobs.AppendEvent(task.JobId, EventLevel.Error,
                        $"{entity}: ingest failed for snapshot {snapshot} — {ex.GetType().Name}: {ex.Message}");
                }
            }

            lastSnapshot = snapshot;
            snapshotsProcessed++;
            progress.Report(JobStepCatalog.Snapshots, snapshotsProcessed, failed: entityFailures.Count, rows: totalCsvRows, force: true);
        }

        progress.CompleteStep(JobStepCatalog.Snapshots);

        if (skippedPaused.Count > 0)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Info,
                $"{skippedPaused.Count} tabla(s) omitida(s) por pausa: {string.Join(", ", skippedPaused.OrderBy(e => e))}.");
        }

        var manifestEntities = new List<IngestEntityManifest>();

        progress.StartStep(JobStepCatalog.Consolidate, total: transientPaths.Count);
        foreach (var (entity, transients) in transientPaths)
        {
            progress.Report(JobStepCatalog.Consolidate, manifestEntities.Count, item: entity, force: true);
            var schema = entitySchemas[entity];
            var consolidatedBlobPath = IngestManifestStore.GetConsolidatedBlobPath(task.PipelineId, entity);
            var schemaBlobPath = IngestManifestStore.GetConsolidatedSchemaBlobPath(task.PipelineId, entity);

            // Fuentes newest-first: transitorios (último snapshot primero); en incremental,
            // además el consolidado previo como base. En Force se reconstruye solo desde los
            // snapshots procesados (la selección abarca toda la historia).
            var sources = new List<Func<CancellationToken, Task<Stream>>>();
            for (var i = transients.Count - 1; i >= 0; i--)
            {
                var path = transients[i];
                sources.Add(_ => Task.FromResult<Stream>(File.OpenRead(path)));
            }

            var transientSources = sources.ToList();
            var hasPreviousConsolidated = !isForce && await parquetStore.ParquetExistsAsync(consolidatedBlobPath, cancellationToken);
            if (hasPreviousConsolidated)
            {
                sources.Add(ct => parquetStore.OpenParquetReadAsync(consolidatedBlobPath, ct));
            }

            var stagingConsolidated = _manifestStore.GetStagingConsolidatedPath(task.JobId, entity);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingConsolidated)!);
            ParquetConsolidator.ConsolidationResult result;
            await using (var output = File.Create(stagingConsolidated))
            {
                result = await ParquetConsolidator.ConsolidateAsync(sources, schema, output, cancellationToken: cancellationToken);
            }

            if (result.RowsWithoutId > 0)
            {
                _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                    $"{entity}: {result.RowsWithoutId} fila(s) sin Id válido descartada(s).");
            }

            // Schema + consolidado a ADLS (durable, independiente de SQL), subidos en
            // streaming desde el archivo local temporal.
            await parquetStore.UploadTextAsync(
                schemaBlobPath, JsonSerializer.Serialize(schema, JsonDefaults.Options), cancellationToken);
            await using (var input = File.OpenRead(stagingConsolidated))
            {
                await parquetStore.UploadParquetAsync(consolidatedBlobPath, input, cancellationToken);
            }

            // Delta: solo las filas de los snapshots de esta corrida (deduplicadas), para que la
            // etapa SQL no remergee la tabla entera. Solo con consolidado previo: sin él la tabla
            // destino está vacía y SQL carga el consolidado (el delta sería una copia completa).
            // El consolidado recién generado compite solo por claves: una fila de esta corrida que
            // pierde frente a él (versión anterior de un Id ya cargado o ya borrado) no entra al delta.
            var hasDelta = false;
            if (hasPreviousConsolidated)
            {
                var deltaPath = _manifestStore.GetDeltaPath(task.PipelineId, task.JobId, entity);
                Directory.CreateDirectory(Path.GetDirectoryName(deltaPath)!);
                await using (var delta = File.Create(deltaPath))
                {
                    await ParquetConsolidator.ConsolidateAsync(
                        transientSources, schema, delta,
                        keyOnlySources: [_ => Task.FromResult<Stream>(File.OpenRead(stagingConsolidated))],
                        cancellationToken: cancellationToken);
                }

                hasDelta = true;
            }

            manifestEntities.Add(new IngestEntityManifest
            {
                Entity = entity,
                HasDelta = hasDelta,
                ParquetPath = consolidatedBlobPath,
                Snapshot = lastSnapshot ?? "",
                CsvRows = entityCsvRows.GetValueOrDefault(entity),
                // Con DeleteMode.Delete las filas de borrado no llegan a SQL: no cuentan como filas.
                FilteredRows = pipeline.DeleteMode == DeleteMode.Delete ? result.Rows - result.DeleteRows : result.Rows,
                Watermark = result.MaxWatermark,
                SourceRowCount = entityCsvRows.GetValueOrDefault(entity),
                SchemaPath = schemaBlobPath,
            });
            progress.Report(JobStepCatalog.Consolidate, manifestEntities.Count);
        }

        progress.CompleteStep(JobStepCatalog.Consolidate);
        progress.StartStep(JobStepCatalog.Register);

        // Limpiar el staging local del job (transitorios + consolidados temporales).
        try
        {
            var stagingRoot = _manifestStore.GetStagingRoot(task.JobId);
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
        catch
        {
            // El staging es efímero; si no se puede borrar ahora, no es fatal.
        }

        var updatedTables = new Dictionary<string, TableState>(pipelineState.Tables, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifestEntities)
        {
            updatedTables[entry.Entity] = new TableState
            {
                RowCount = entry.FilteredRows,
                Watermark = entry.Watermark,
                LastLoad = DateTimeOffset.UtcNow,
                LastSnapshot = entry.Snapshot,
                Status = "ok",
            };
        }

        // Si alguna entidad falló, el snapshot NO se da por procesado: el siguiente Incremental o
        // Retry vuelve a leerlo (re-ingerir es idempotente: el consolidado deduplica por Id). Si
        // avanzara, esos datos se perderían en silencio. Una tabla rota de forma permanente se
        // puede pausar para dejar avanzar al resto.
        var processedUpTo = entityFailures.Count > 0 ? pipelineState.LastProcessedSnapshot : lastSnapshot;
        if (entityFailures.Count > 0 && lastSnapshot is not null)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                $"{entityFailures.Count} tabla(s) con fallos: el snapshot {lastSnapshot} no se marca como procesado y se reintentará en el próximo run.");
        }

        _stateStore.Save(pipelineState with
        {
            LastProcessedSnapshot = processedUpTo,
            GlobalWatermark = manifestEntities.Select(e => e.Watermark).Where(w => w.HasValue).Max(),
            Tables = updatedTables,
        });

        _manifestStore.Save(new IngestManifest
        {
            JobId = task.JobId,
            PipelineId = task.PipelineId,
            LastProcessedSnapshot = lastSnapshot,
            Entities = manifestEntities,
        });
        progress.CompleteStep(JobStepCatalog.Register);

        // Entidades que fallaron en TODOS sus intentos (nunca llegaron a consolidarse): se
        // reportan como error para que el operador las vea en el detalle del job.
        var succeededEntities = new HashSet<string>(manifestEntities.Select(m => m.Entity), StringComparer.OrdinalIgnoreCase);
        var fullyFailedEntities = entityFailures.Keys
            .Where(entity => !succeededEntities.Contains(entity))
            .OrderBy(entity => entity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Entidades que SÍ consolidaron datos (de algún snapshot) pero también tuvieron fallos en
        // otro(s) snapshot(s) — antes esto quedaba invisible (la entidad se reportaba "ok" sin
        // más) porque solo se miraba si había tenido éxito en ALGÚN snapshot. El caso real que
        // motivó este fix: un snapshot con el volcado histórico completo falló por
        // OutOfMemoryException, snapshots incrementales posteriores sí tuvieron éxito — la entidad
        // terminó "ok" con una fracción mínima de los datos reales, sin ninguna señal de alerta.
        var partiallyFailedEntities = entityFailures.Keys
            .Where(entity => succeededEntities.Contains(entity))
            .OrderBy(entity => entity, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var consolidatedResults = manifestEntities
            .Select(m =>
            {
                var message = $"{m.FilteredRows} row(s) from snapshot {m.Snapshot}";
                if (entityFailures.TryGetValue(m.Entity, out var partialFailures))
                {
                    message += $" — ADVERTENCIA: fallo parcial, posible dato incompleto ({string.Join("; ", partialFailures)}).";
                }

                return new JobEntityResult
                {
                    Entity = m.Entity,
                    CsvRows = m.CsvRows,
                    Status = "ok",
                    Watermark = m.Watermark,
                    Message = message,
                };
            })
            .Concat(fullyFailedEntities.Select(entity => new JobEntityResult
            {
                Entity = entity,
                CsvRows = entityCsvRows.GetValueOrDefault(entity),
                Status = "error",
                Message = string.Join("; ", entityFailures[entity]),
            }))
            .ToList();

        var hasEntityError = fullyFailedEntities.Count > 0;
        var hasEntityOk = manifestEntities.Count > 0;
        JobStatus? jobStatusAfterIngest = hasEntityError
            ? hasEntityOk ? JobStatus.Partial : JobStatus.Error
            : partiallyFailedEntities.Count > 0
                ? JobStatus.Partial
                : null;

        job = _jobs.Get(task.JobId)!;
        var updatedJob = job with
        {
            Entities = consolidatedResults,
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["ingest"] = hasEntityError ? "error" : "done" },
            StageTimings = job.StageTimings.Set("ingest", started: stageStart, finished: DateTimeOffset.UtcNow),
            Summary = new Dictionary<string, object?>
            {
                ["snapshotsProcessed"] = snapshotsProcessed,
                ["snapshotsSelected"] = selection.ToProcess.Count,
                ["csvRows"] = totalCsvRows,
                ["entities"] = manifestEntities.Count,
                ["entityFailures"] = fullyFailedEntities.Count,
                ["partialEntityFailures"] = partiallyFailedEntities.Count,
                ["stoppedAtIncompleteSnapshot"] = incomplete?.Reason,
            },
        };
        if (jobStatusAfterIngest is not null)
        {
            updatedJob = updatedJob with { Status = jobStatusAfterIngest.Value };
            if (jobStatusAfterIngest == JobStatus.Error)
            {
                // Todas las entidades fallaron: no hay nada que mergear ni reconciliar; cerrar
                // el job aquí (StageRouter no encola SQL cuando el job ya quedó en Error).
                updatedJob = updatedJob with
                {
                    CurrentStage = null,
                    FinishedAt = DateTimeOffset.UtcNow,
                };
            }
        }

        // Las entidades fallan al leerse en el paso de snapshots: ahí se marca el error.
        progress.Finish(hasEntityError ? "error" : "done", failedStep: JobStepCatalog.Snapshots);
        _jobs.Update(job.Id, updatedJob);

        var completionNote = incomplete is null
            ? $"Ingest completed: {snapshotsProcessed} snapshot(s), {manifestEntities.Count} entity(ies)."
            : $"Ingest stopped early after {snapshotsProcessed}/{selection.ToProcess.Count} snapshot(s): {incomplete.Reason}";
        if (fullyFailedEntities.Count > 0)
        {
            completionNote += $" {fullyFailedEntities.Count} entity(ies) failed: {string.Join(", ", fullyFailedEntities)}.";
        }

        if (partiallyFailedEntities.Count > 0)
        {
            completionNote += $" {partiallyFailedEntities.Count} entity(ies) with partial snapshot failures (possible incomplete data): {string.Join(", ", partiallyFailedEntities)}.";
        }

        var completionLevel = jobStatusAfterIngest == JobStatus.Error
            ? EventLevel.Error
            : jobStatusAfterIngest == JobStatus.Partial || incomplete is not null
                ? EventLevel.Warning
                : EventLevel.Info;
        _jobs.AppendEvent(task.JobId, completionLevel, completionNote);
    }
}