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
    private readonly AlertService? _alerts;
    private readonly SettingsStore? _settings;

    public IngestExecutor(
        IJobService jobs,
        IPipelineService pipelines,
        PipelineStateStore stateStore,
        TablePauseStore pauseStore,
        IAdlsClientFactory adlsFactory,
        IngestManifestStore manifestStore,
        IOptions<AppOptions> options,
        ILogger<IngestExecutor> logger,
        JobProgressStore? progress = null,
        AlertService? alerts = null,
        SettingsStore? settings = null)
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
        _alerts = alerts;
        _settings = settings;
    }

    public async Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        // El camino feliz borra el staging al registrar. Si el ingest falla o el usuario cancela el
        // job, también se borra: si no, sus transitorios se quedaban en el volumen hasta que la
        // retención purgaba el job. Con el lease perdido o en un apagado (token cancelado, job aún
        // Running) no: la tarea puede ser ya de otra réplica, que escribe en el mismo staging/{jobId}.
        // Ese lo limpia la re-ejecución al terminar, o la retención si el job acaba en Error/Cancelled.
        try
        {
            await ExecuteCoreAsync(task, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested || _jobs.Get(task.JobId)?.Status == JobStatus.Cancelled)
        {
            DeleteStaging(task.JobId);
            throw;
        }
    }

    // Limpiar el staging local del job (transitorios + consolidados temporales).
    private void DeleteStaging(string jobId)
    {
        try
        {
            var stagingRoot = _manifestStore.GetStagingRoot(jobId);
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
        catch
        {
            // El staging es efímero; si no se puede borrar ahora, no es fatal.
        }
    }

    private async Task ExecuteCoreAsync(PipelineTask task, CancellationToken cancellationToken)
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

        // El cliente ADLS lee el origen; el parquet/schema durable va al almacén que elija la
        // conexión (parquet_storage): el container de ADLS o el disco del portal.
        var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
        var parquetStore = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
        var pipelineState = _stateStore.GetOrCreate(task.PipelineId);
        var settled = await adls.GetSettledSnapshotAsync(cancellationToken);
        var snapshots = await adls.ListSnapshotFoldersAsync(cancellationToken);
        var isForce = job.Type == JobType.Force;
        var selection = SnapshotSelector.Select(
            snapshots,
            isForce ? null : pipelineState.LastProcessedSnapshot,
            settled);

        // Re-ejecución de este mismo job (el worker cayó tras registrar el ingest y el lease venció):
        // su manifest lista lo ingerido y SQL aún no lo ha mergeado. Se conserva sea cual sea el lago
        // ahora: reingerir reescribiría el manifest solo con los snapshots nuevos y las filas del
        // primer intento no llegarían nunca a SQL. Los snapshots nuevos son del siguiente job.
        var previous = _manifestStore.Load(task.PipelineId, task.JobId);
        var alreadyIngested = previous is { Entities.Count: > 0 };

        if (selection.Blocked && !alreadyIngested)
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

        if (alreadyIngested || selection.ToProcess.Count == 0)
        {
            // Sin nada nuevo, o el ingest de este job ya estaba registrado (ver arriba). Si cayó
            // antes de escribir su Summary (p. ej. durante la poda), Summary y Entities se rehacen
            // desde ese manifest.
            _jobs.AppendEvent(task.JobId, EventLevel.Info, alreadyIngested
                ? $"El ingest de este job ya estaba registrado ({previous!.Entities.Count} tabla(s)): se conserva su manifest para la etapa SQL."
                : JobEventMarkers.NoNewSnapshots);
            var summaryWritten = job.Summary.ContainsKey("snapshotsSelected");
            _jobs.Update(job.Id, job with
            {
                StageStatus = new Dictionary<string, string>(job.StageStatus) { ["ingest"] = "done" },
                StageTimings = job.StageTimings.Set("ingest", started: stageStart, finished: DateTimeOffset.UtcNow),
                Entities = alreadyIngested && !summaryWritten
                    ? previous!.Entities.Select(m => new JobEntityResult
                    {
                        Entity = m.Entity,
                        CsvRows = m.CsvRows,
                        Status = "ok",
                        Watermark = m.Watermark,
                        Message = $"{m.FilteredRows} row(s) from snapshot {m.Snapshot}",
                    }).ToList()
                    : job.Entities,
                // Mismas claves que el cierre normal: la UI distingue "sin datos nuevos" por
                // snapshotsSelected = 0 sin depender del texto del evento.
                Summary = (alreadyIngested, summaryWritten) switch
                {
                    (false, _) => new Dictionary<string, object?>
                    {
                        ["snapshotsProcessed"] = 0,
                        ["snapshotsSelected"] = 0,
                    },
                    (true, false) => new Dictionary<string, object?>
                    {
                        ["csvRows"] = previous!.Entities.Sum(m => m.CsvRows),
                        ["entities"] = previous.Entities.Count,
                    },
                    (true, true) => job.Summary,
                },
            });
            if (!alreadyIngested)
            {
                _manifestStore.Save(new IngestManifest
                {
                    JobId = task.JobId,
                    PipelineId = task.PipelineId,
                    LastProcessedSnapshot = pipelineState.LastProcessedSnapshot,
                });
            }

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
        // Primer error conocido (KnownErrors) de cada entidad que falla.
        var entityCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Valores del CSV que no se leyeron con el tipo de su columna (cargados como NULL), por entidad.
        var unreadableValues = new Dictionary<string, CsvValueIssues>(StringComparer.OrdinalIgnoreCase);
        var totalCsvRows = 0;
        var snapshotsProcessed = 0;
        SnapshotIntegrity? incomplete = null;
        string? incompleteSnapshot = null;
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
                incompleteSnapshot = snapshot;
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
                    var issues = new CsvValueIssues();
                    var rows = CsvEntityReader.ReadRowsAsync(lines, schema, cancellationToken, issues);

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

                    if (!unreadableValues.TryGetValue(entity, out var entityIssues))
                    {
                        entityIssues = new CsvValueIssues();
                        unreadableValues[entity] = entityIssues;
                    }

                    entityIssues.Merge(issues);
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
                    var code = KnownErrors.CodeFrom(ex);
                    if (code is not null)
                    {
                        entityCodes.TryAdd(entity, code);
                    }

                    _logger.LogError(ex, "Ingest stage failed for entity {Entity} in snapshot {Snapshot}", entity, snapshot);
                    _jobs.AppendEvent(task.JobId, EventLevel.Error,
                        $"{entity}: ingest failed for snapshot {snapshot} — {ex.GetType().Name}: {ex.Message}", code);
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
            await ReportSchemaChangesAsync(task.JobId, pipeline.Name, entity, schemaBlobPath, schema, parquetStore, cancellationToken);

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
            var consolidatedExists = await parquetStore.ParquetExistsAsync(consolidatedBlobPath, cancellationToken);
            var hasPreviousConsolidated = !isForce && consolidatedExists;
            if (hasPreviousConsolidated)
            {
                sources.Add(await PreviousConsolidatedSourceAsync(parquetStore, consolidatedBlobPath, task.JobId, entity, cancellationToken));
            }
            else if (!isForce
                && pipelineState.Tables.ContainsKey(entity)
                && await ParquetVersions.HasVersionsAsync(parquetStore, task.PipelineId, entity, cancellationToken))
            {
                // Sin consolidado pero con versiones y aún en el estado: una publicación se cortó
                // entre sus dos renames o alguien lo borró. Seguir lo reconstruiría solo con los
                // snapshots nuevos y la historia se perdería en silencio. Una tabla que archivó una
                // restauración (aún no existía en ese punto) ya no está en el estado y se reconstruye.
                throw new InvalidOperationException(
                    $"{entity}: falta el parquet consolidado pero hay versiones anteriores. Restaura un punto en la página Parquet (Puntos de restauración) y vuelve a ejecutar.");
            }

            var stagingConsolidated = _manifestStore.GetStagingConsolidatedPath(task.JobId, entity);
            Directory.CreateDirectory(Path.GetDirectoryName(stagingConsolidated)!);
            ParquetConsolidator.ConsolidationResult result;
            try
            {
                await using var output = File.Create(stagingConsolidated);
                result = await ParquetConsolidator.ConsolidateAsync(sources, schema, output, cancellationToken: cancellationToken);
            }
            finally
            {
                File.Delete(_manifestStore.GetStagingPreviousConsolidatedPath(task.JobId, entity));
            }

            if (result.RowsWithoutId > 0)
            {
                _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                    $"{entity}: {result.RowsWithoutId} fila(s) sin Id válido descartada(s).");
            }

            // Antes de publicar: el fichero tiene las filas que el consolidador dice haber escrito.
            long writtenRows;
            await using (var written = File.OpenRead(stagingConsolidated))
            {
                writtenRows = await ParquetMaterializer.CountRowsAsync(written, cancellationToken: cancellationToken);
            }

            if (writtenRows != result.Rows)
            {
                throw new InvalidOperationException(
                    $"{entity}: el consolidado tiene {writtenRows:N0} filas y se escribieron {result.Rows:N0}; no se publica y se conserva el anterior.");
            }

            // Las filas de borrado se conservan, así que el consolidado no debería encoger. Solo se
            // avisa: con la versión anterior a salvo, bloquear dejaría el pipeline atascado.
            if (consolidatedExists)
            {
                var previousRows = await parquetStore.CountParquetRowsAsync(consolidatedBlobPath, cancellationToken: cancellationToken);
                if (result.Rows < previousRows)
                {
                    var message = $"{entity}: el consolidado nuevo tiene {result.Rows:N0} filas y el anterior {previousRows:N0}. La versión anterior queda guardada en la página Parquet por si hay que restaurarla.";
                    _jobs.AppendEvent(task.JobId, EventLevel.Warning, message);
                    if (_alerts is not null)
                    {
                        await _alerts.SendAsync($"Pipeline {pipeline.Name}: {message}", cancellationToken);
                    }
                }
            }

            // Schema + consolidado al almacén durable (independiente de SQL): se sube con otro
            // nombre, se valida y sustituye al anterior, que pasa a ser una versión.
            await ParquetVersions.PublishAsync(
                parquetStore, task.PipelineId, entity, stagingConsolidated, result.Rows,
                JsonSerializer.Serialize(schema, JsonDefaults.Options),
                new ParquetVersion
                {
                    ReplacedByJobId = task.JobId,
                    Table = pipelineState.Tables.GetValueOrDefault(entity),
                    PipelineSnapshot = pipelineState.LastProcessedSnapshot,
                },
                DateTimeOffset.UtcNow, cancellationToken);

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

        DeleteStaging(task.JobId);

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

        // El manifest antes que el estado: el estado es el commit. Si el proceso cae entre ambos,
        // el snapshot no consta como procesado: la re-ejecución conserva el manifest ya registrado y
        // el siguiente job vuelve a leer esos snapshots (idempotente: el parquet consolidado
        // deduplica por Id y el MERGE de SQL es un upsert); al revés, la re-ejecución vería el estado
        // avanzado sin manifest y SQL no recibiría nada.
        _manifestStore.Save(new IngestManifest
        {
            JobId = task.JobId,
            PipelineId = task.PipelineId,
            LastProcessedSnapshot = lastSnapshot,
            Entities = manifestEntities,
        });

        _stateStore.Save(pipelineState with
        {
            LastProcessedSnapshot = processedUpTo,
            GlobalWatermark = manifestEntities.Select(e => e.Watermark).Where(w => w.HasValue).Max(),
            Tables = updatedTables,
            // El mismo snapshot sigue incompleto: se conserva desde cuándo (y la última alerta). Otro,
            // o ninguno, rearma el aviso.
            IncompleteSnapshot = incompleteSnapshot is null ? null
                : pipelineState.IncompleteSnapshot?.Name == incompleteSnapshot ? pipelineState.IncompleteSnapshot
                : new IncompleteSnapshot { Name = incompleteSnapshot, FirstSeenAt = DateTimeOffset.UtcNow },
        });
        progress.CompleteStep(JobStepCatalog.Register);

        // La poda va por puntos de restauración (todas las tablas a la vez), así que una vez por job.
        // Si falla, la ingesta ya está hecha: solo se avisa y se reintenta en la siguiente. Con
        // settings.json ilegible no se poda: los días por defecto podrían borrar, sin vuelta atrás,
        // versiones que el ajuste guardado conserva.
        AppSettings? settings = null;
        if (_settings is not null && !_settings.TryGet(out settings))
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                "settings.json no se puede leer: no se podan las versiones del parquet hasta que se corrija.");
        }
        else
        {
            try
            {
                await ParquetVersions.PruneAsync(parquetStore, task.PipelineId, settings?.ParquetVersionDays ?? 0, DateTimeOffset.UtcNow, pipelineState.RetiredSnapshots, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _jobs.AppendEvent(task.JobId, EventLevel.Warning, $"No se pudieron podar las versiones del parquet: {ex.Message}");
            }
        }

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

        // Entidades consolidadas con valores del CSV que no se pudieron leer: se cargaron como NULL,
        // así que el dato en SQL no es el del origen. El snapshot sí se da por procesado (releerlo
        // daría lo mismo), pero la tabla lo indica y el job queda en Partial.
        var entitiesWithUnreadableValues = unreadableValues
            .Where(e => e.Value.Total > 0 && succeededEntities.Contains(e.Key))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var (entity, issues) in entitiesWithUnreadableValues)
        {
            _jobs.AppendEvent(task.JobId, EventLevel.Warning,
                $"{entity}: {issues.Total} valor(es) del CSV no se pudieron leer con el tipo de su columna y se cargaron como NULL — {issues}.");
        }

        var consolidatedResults = manifestEntities
            .Select(m =>
            {
                var message = $"{m.FilteredRows} row(s) from snapshot {m.Snapshot}";
                if (entityFailures.TryGetValue(m.Entity, out var partialFailures))
                {
                    message += $"{JobEntityResultMerger.WarningMarker} fallo parcial, posible dato incompleto ({string.Join("; ", partialFailures)}).";
                }

                if (unreadableValues.TryGetValue(m.Entity, out var issues) && issues.Total > 0)
                {
                    message += $"{JobEntityResultMerger.WarningMarker} {issues.Total} valor(es) ilegible(s) cargado(s) como NULL ({issues}).";
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
                Code = entityCodes.GetValueOrDefault(entity),
            }))
            .ToList();

        var hasEntityError = fullyFailedEntities.Count > 0;
        var hasEntityOk = manifestEntities.Count > 0;
        JobStatus? jobStatusAfterIngest = hasEntityError
            ? hasEntityOk ? JobStatus.Partial : JobStatus.Error
            : partiallyFailedEntities.Count > 0 || entitiesWithUnreadableValues.Count > 0
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
                ["unreadableValues"] = entitiesWithUnreadableValues.Sum(e => e.Value.Total),
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

        if (entitiesWithUnreadableValues.Count > 0)
        {
            completionNote += $" {entitiesWithUnreadableValues.Count} entity(ies) with CSV values loaded as NULL: {string.Join(", ", entitiesWithUnreadableValues.Select(e => e.Key))}.";
        }

        var completionLevel = jobStatusAfterIngest == JobStatus.Error
            ? EventLevel.Error
            : jobStatusAfterIngest == JobStatus.Partial || incomplete is not null
                ? EventLevel.Warning
                : EventLevel.Info;
        _jobs.AppendEvent(task.JobId, completionLevel, completionNote);

        // Como la etapa SQL: un ingest que termina en Partial o Error avisa por el webhook, no solo
        // en el detalle del job.
        if (jobStatusAfterIngest is not null && _alerts is not null)
        {
            await _alerts.SendPipelineFailureAsync(new PipelineFailureAlert
            {
                PipelineId = pipeline.Id,
                PipelineName = pipeline.Name,
                JobId = task.JobId,
                JobType = job.Type.ToString(),
                Stage = "ingest",
                Message = completionNote,
                Status = jobStatusAfterIngest == JobStatus.Partial ? PipelineFailureAlert.Warning : PipelineFailureAlert.Error,
            }, cancellationToken);
        }
    }

    // La consolidación lee cada fuente dos veces. En ADLS, abrir el consolidado anterior es descargarlo
    // entero, así que se baja una sola vez a staging (se borra al consolidar la entidad) y las
    // dos pasadas leen esa copia. En el disco del portal ya es un fichero local y se lee tal cual.
    private async Task<Func<CancellationToken, Task<Stream>>> PreviousConsolidatedSourceAsync(
        IParquetStore parquetStore, string consolidatedBlobPath, string jobId, string entity, CancellationToken cancellationToken)
    {
        if (parquetStore is LocalParquetStore)
        {
            return ct => parquetStore.OpenParquetReadAsync(consolidatedBlobPath, ct);
        }

        var local = _manifestStore.GetStagingPreviousConsolidatedPath(jobId, entity);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        try
        {
            await using var remote = await parquetStore.OpenParquetReadAsync(consolidatedBlobPath, cancellationToken);
            await using var file = File.Create(local);
            await remote.CopyToAsync(file, cancellationToken);
        }
        catch
        {
            File.Delete(local);
            throw;
        }

        return _ => Task.FromResult<Stream>(File.OpenRead(local));
    }

    // Cambios de esquema respecto al consolidado anterior (el schema.json que se va a sobrescribir):
    // avisos en el job y, si cambia un tipo, alerta. Leer el anterior no debe tumbar la ingesta.
    private async Task ReportSchemaChangesAsync(
        string jobId, string pipelineName, string entity, string schemaBlobPath, CdmEntitySchema schema,
        IParquetStore parquetStore, CancellationToken cancellationToken)
    {
        CdmEntitySchema? previous;
        try
        {
            var json = await parquetStore.ReadTextAsync(schemaBlobPath, cancellationToken);
            previous = json is null ? null : JsonSerializer.Deserialize<CdmEntitySchema>(json, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "No se pudo leer el esquema anterior de {Entity}.", entity);
            return;
        }

        if (previous is null)
        {
            return;
        }

        var diff = SchemaDiff.Compare(previous, schema);
        if (diff.Added.Count > 0)
        {
            _jobs.AppendEvent(jobId, EventLevel.Info,
                $"{entity}: columnas nuevas en el origen: {string.Join(", ", diff.Added)} (se añaden en SQL).", SchemaDiff.ColumnsAddedCode);
        }

        if (diff.Removed.Count > 0)
        {
            _jobs.AppendEvent(jobId, EventLevel.Warning,
                $"{entity}: columnas que ya no llegan del origen: {string.Join(", ", diff.Removed)} (en SQL conservan su último valor).", SchemaDiff.ColumnsRemovedCode);
        }

        if (diff.TypeChanged.Count > 0)
        {
            var detail = string.Join(", ", diff.TypeChanged.Select(c => $"{c.Column} {c.Before} → {c.After}"));
            _jobs.AppendEvent(jobId, EventLevel.Warning,
                $"{entity}: cambio de tipo en el origen: {detail}. SQL mantiene el tipo anterior; revisa la tabla.", SchemaDiff.TypeChangedCode);
            if (_alerts is not null)
            {
                await _alerts.SendAsync($"Pipeline {pipelineName}: cambio de tipo en {entity} ({detail}).", cancellationToken);
            }
        }
    }
}