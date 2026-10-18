using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public interface IOrchestrationService
{
    object GetMode();
    object GetQueueCounts();
    QueueSnapshot GetQueueSnapshot();
    IReadOnlyList<WorkerStatus> GetWorkers();
    IReadOnlyList<Job> GetActiveJobs();
    Task<Job> RunPipelineAsync(string pipelineId, JobType type, CancellationToken cancellationToken = default);
    Task<Job> RetryJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task<Job> ResyncTableAsync(string pipelineId, string entity, ResyncMode mode, CancellationToken cancellationToken = default);

    // Varias tablas en un solo job (un solo job activo por pipeline). triggeredBy: "user" | "auto".
    // Por defecto no soportado, para los dobles de test que no lo ejercitan.
    Task<Job> ResyncTablesAsync(string pipelineId, IReadOnlyList<string> entities, ResyncMode mode, string triggeredBy, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<Job> ReloadFromParquetAsync(string pipelineId, CancellationToken cancellationToken = default);

    // Devuelve todo el pipeline al punto de restauración de un snapshot y recarga SQL de las
    // tablas que volvieron atrás (null si ninguna volvió atrás y solo se archivaron tablas).
    Task<Job?> RestorePointAsync(string pipelineId, string snapshot, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // Comparación de valores (hash por fila) de una tabla o, con entity null, de todo el pipeline.
    Task<Job> CompareValuesAsync(string pipelineId, string? entity, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task CancelAsync(string jobId, CancellationToken cancellationToken = default);

    // Si el pipeline tiene un job activo (o uno cancelado que aún se detiene): un Run se rechazaría.
    Task<bool> IsBusyAsync(string pipelineId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // Deja una ejecución en cola para cuando el pipeline quede libre; la lanza el scheduler.
    QueuedRun QueueRun(string pipelineId, JobType type) =>
        throw new NotSupportedException();
}

public sealed class OrchestrationService : IOrchestrationService
{
    private readonly AppOptions _options;
    private readonly IPipelineService _pipelines;
    private readonly IJobService _jobs;
    private readonly ITaskStore _tasks;
    private readonly WorkerRegistry _workers;
    private readonly IngestManifestStore _manifests;
    private readonly ReportStore _reports;
    private readonly IParquetArtifactService _parquet;
    private readonly PipelineStateStore? _stateStore;
    private readonly PipelineRunQueue? _runQueue;

    public OrchestrationService(
        IOptions<AppOptions> options,
        IPipelineService pipelines,
        IJobService jobs,
        ITaskStore tasks,
        WorkerRegistry workers,
        IngestManifestStore manifests,
        ReportStore reports,
        IParquetArtifactService parquet,
        PipelineStateStore? stateStore = null,
        PipelineRunQueue? runQueue = null)
    {
        _stateStore = stateStore;
        _runQueue = runQueue;
        _options = options.Value;
        _pipelines = pipelines;
        _jobs = jobs;
        _tasks = tasks;
        _workers = workers;
        _manifests = manifests;
        _reports = reports;
        _parquet = parquet;
    }

    public object GetMode() => new { mode = _options.ExecutionMode, distributed = _options.IsDistributed };

    public object GetQueueCounts() => GetQueueSnapshot();

    public QueueSnapshot GetQueueSnapshot() => new(
        StageCounts(PipelineStage.Ingest),
        StageCounts(PipelineStage.Sql),
        StageCounts(PipelineStage.Reconcile));

    private StageQueueCounts StageCounts(PipelineStage stage) => new(
        _tasks.CountByStatus(stage, PipelineTaskStatus.Pending),
        _tasks.CountByStatus(stage, PipelineTaskStatus.Claimed),
        _tasks.CountByStatus(stage, PipelineTaskStatus.Done),
        _tasks.CountByStatus(stage, PipelineTaskStatus.Error));

    public IReadOnlyList<WorkerStatus> GetWorkers() => _workers.ListAll();

    public IReadOnlyList<Job> GetActiveJobs() => _jobs.ListRunning();

    // Cancelación cooperativa: purga las tareas Pending del job para que no se procesen más
    // stages, y marca el job Cancelled. La tarea Claimed sigue siendo de su worker: su latido ve
    // Cancelled, detiene el stage y la libera; el router no avanza un job cancelado.
    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        // Partial sin FinishedAt también está en curso (ingest parcial que sigue con SQL).
        var job = _jobs.Get(jobId);
        if (job is null || !JobProgressHelper.IsActive(job))
        {
            return;
        }

        await _tasks.CancelJobTasksAsync(jobId, cancellationToken);

        // Releer tras el await (como el watchdog): el executor en vuelo pudo guardar entretanto
        // tiempos, Summary o entidades, y un Update con la copia previa los pisaría.
        job = _jobs.Get(jobId);
        if (job is null || !JobProgressHelper.IsActive(job))
        {
            return;
        }

        var stageStatus = new Dictionary<string, string>(job.StageStatus);
        foreach (var key in stageStatus.Keys.ToList())
        {
            if (stageStatus[key] is "pending" or "running" or "claimed")
            {
                stageStatus[key] = "cancelled";
            }
        }

        _jobs.Update(jobId, job with
        {
            Status = JobStatus.Cancelled,
            FinishedAt = DateTimeOffset.UtcNow,
            StageStatus = stageStatus,
        });
        _jobs.AppendEvent(jobId, EventLevel.Warning, "Job cancelado por el usuario.");
    }

    public Task<Job> RunPipelineAsync(string pipelineId, JobType type, CancellationToken cancellationToken = default) =>
        StartAsync(pipelineId, type, entity: null, cancellationToken);

    public async Task<bool> IsBusyAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        try
        {
            await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "", cancellationToken);
            return false;
        }
        catch (PipelineBusyException)
        {
            return true;
        }
    }

    // Un hueco por pipeline: pedir de nuevo lo mismo avisa de que ya está, y otra cosa se rechaza
    // nombrando lo que hay, para que nada se sustituya sin saberlo.
    public QueuedRun QueueRun(string pipelineId, JobType type)
    {
        var queue = _runQueue
            ?? throw new InvalidOperationException("Queueing a run requires the run queue.");
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        if (!PipelineRunQueue.QueueableTypes.Contains(type))
        {
            throw new InvalidOperationException($"{type} no se puede dejar en cola.");
        }

        var run = new QueuedRun(type, DateTimeOffset.UtcNow);
        return queue.TryEnqueue(pipelineId, run) switch
        {
            null => run,
            { } queued when queued.Type == type => throw new InvalidOperationException($"{type} ya está en cola para este pipeline."),
            { } queued => throw new InvalidOperationException(
                $"Ya hay {queued.Type} en cola para este pipeline: quítalo de la cola antes de encolar {type}."),
        };
    }

    // Comparación de valores bajo demanda: de una tabla o, sin entity, de todas las del pipeline.
    public Task<Job> CompareValuesAsync(string pipelineId, string? entity, CancellationToken cancellationToken = default) =>
        StartAsync(pipelineId, JobType.CompareValues, string.IsNullOrWhiteSpace(entity) ? null : entity.Trim(), cancellationToken);

    private async Task<Job> StartAsync(string pipelineId, JobType type, string? entity, CancellationToken cancellationToken)
    {
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");

        // Nota: Pipeline.Enabled significa "scheduler activo", NO "ejecutable". El Run manual
        // siempre se permite (crear pipeline → correr a mano → activar scheduler al validar).

        // Force tiene su propio camino (con la misma guarda y el mismo lock, que no es reentrante).
        if (type == JobType.Force)
        {
            return await EnqueueParquetReloadAsync(pipelineId, JobType.Force, cancellationToken);
        }

        // Guardia anti-doble-run: si ya hay un job en curso para este pipeline, no se arranca
        // otro (evita dos jobs concurrentes pisándose). Mismo criterio que usa el scheduler.
        // El lock hace atómico "comprobar + crear" frente a otros Run simultáneos.
        using var runLock = await AcquireRunLockAsync(pipelineId, cancellationToken);
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);

        // Reconcile y Diff solo comparan conteos (parquet consolidado vs SQL) y CompareValues compara
        // valores: no ingieren ni mergean. Arrancan directamente en la etapa de conciliación con las
        // otras dos omitidas (como Force/Resync); ReconcileExecutor, sin manifest, compara todos los
        // parquet del pipeline (o solo la entidad del payload, en CompareValues).
        var countOnly = type is JobType.Reconcile or JobType.Diff or JobType.CompareValues;
        var firstStage = countOnly ? PipelineStage.Reconcile : PipelineStage.Ingest;
        var jobId = NewJobId(pipelineId);
        var job = new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = type,
            Status = JobStatus.Running,
            CurrentStage = firstStage,
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = countOnly ? "skipped" : "pending",
                ["sql"] = countOnly ? "skipped" : "pending",
                ["reconcile"] = "pending",
            },
            Events =
            [
                new JobEvent { At = DateTimeOffset.UtcNow, Level = EventLevel.Info, Message = $"Job started ({type})." },
            ],
        };

        _jobs.Create(job);

        var task = new PipelineTask
        {
            Id = $"task-{jobId}-{firstStage.ToString().ToLowerInvariant()}",
            Stage = firstStage,
            PipelineId = pipelineId,
            JobId = jobId,
            Payload = entity is null
                ? new Dictionary<string, object?> { ["jobType"] = type.ToString() }
                : new Dictionary<string, object?> { ["jobType"] = type.ToString(), ["entity"] = entity },
        };

        await _tasks.EnqueueAsync(task, cancellationToken);
        return job;
    }

    // Reintento de un job terminado en error: re-ejecuta el pipeline desde Ingest reusando
    // RunPipelineAsync, salvo que el fallo fuera en SQL tras un ingest completo (entonces recarga
    // SQL desde el parquet consolidado). Crea un Job NUEVO; el job fallido se conserva como historial
    // (no se edita ni se borra). La guarda anti-doble-run de RunPipelineAsync evita arrancar si
    // ya hay uno en curso. La tarea fallida en tasks/{stage}/error/ queda como evidencia.
    public async Task<Job> RetryJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(jobId)
            ?? throw new InvalidOperationException("Job not found.");

        // Un Partial sin FinishedAt sigue corriendo (ingest parcial que continúa con SQL).
        if (JobProgressHelper.IsActive(job))
        {
            throw new InvalidOperationException("El job sigue en curso: espera a que termine o cancélalo antes de reintentarlo.");
        }

        // Solo son reintentables los estados terminales con fallo. Running/Ok/Cancelled no.
        if (job.Status is not (JobStatus.Error or JobStatus.Partial or JobStatus.Blocked))
        {
            throw new InvalidOperationException("Solo se pueden reintentar jobs en estado Error, Parcial o Bloqueado.");
        }

        if (job.Type is JobType.ReloadFromParquet or JobType.Force)
        {
            return await EnqueueParquetReloadAsync(job.PipelineId, JobType.Force, cancellationToken);
        }

        // Ingest terminó pero SQL no (falló, o el watchdog cerró el job con SQL en pending/running):
        // el snapshot ya cuenta como procesado, así que un Incremental no encontraría nada nuevo y
        // reportaría Ok sin cargar los datos. Se recarga SQL desde el parquet consolidado, que ya
        // contiene lo ingerido.
        if (job.StageStatus.GetValueOrDefault("ingest") == "done"
            && job.StageStatus.GetValueOrDefault("sql") != "done")
        {
            return await EnqueueParquetReloadAsync(job.PipelineId, JobType.Force, cancellationToken);
        }

        // Ingest y SQL hechos pero Reconcile no (falló o el watchdog cerró el job): los datos ya
        // están cargados y un Incremental no conciliaría nada. Se reintenta solo la conciliación.
        if (job.StageStatus.GetValueOrDefault("ingest") == "done"
            && job.StageStatus.GetValueOrDefault("sql") == "done"
            && job.StageStatus.GetValueOrDefault("reconcile") != "done")
        {
            return await RunPipelineAsync(job.PipelineId, JobType.Reconcile, cancellationToken);
        }

        // ResyncTable arranca en SQL con un manifest sintético de una entidad; reintentarlo como
        // tal sin contexto sería frágil. Se reintenta como Incremental (re-ejecución estándar
        // del pipeline). El resto de tipos (Incremental/Reconcile/Diff) arrancan en Ingest.
        var retryType = job.Type == JobType.ResyncTable
            ? JobType.Incremental
            : job.Type;
        return await RunPipelineAsync(job.PipelineId, retryType, cancellationToken);
    }

    // Re-sincronización de UNA tabla sin reprocesar el pipeline: arranca en el stage SQL
    // (sin ingest) re-mergeando el parquet consolidado durable ya existente; el StageRouter
    // encadena SQL → Reconcile como siempre. Modo Reload = upsert + borrado de huérfanas, sin
    // vaciar la tabla (corrige ambas direcciones, ver SqlExecutor); Remerge = solo upsert.
    public Task<Job> ResyncTableAsync(string pipelineId, string entity, ResyncMode mode, CancellationToken cancellationToken = default) =>
        ResyncTablesAsync(pipelineId, string.IsNullOrWhiteSpace(entity) ? [] : [entity], mode, AutoRemediation.TriggeredByUser, cancellationToken);

    // Varias tablas en un solo job: la corrección automática re-sincroniza de una vez todas las que
    // divergen (un solo job activo por pipeline). El payload "entity" las lleva separadas por comas.
    public async Task<Job> ResyncTablesAsync(string pipelineId, IReadOnlyList<string> entities, ResyncMode mode, string triggeredBy, CancellationToken cancellationToken = default)
    {
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");

        entities = entities.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (entities.Count == 0)
        {
            throw new InvalidOperationException("Entity is required.");
        }

        // Misma guardia anti-doble-run que el Run completo: un solo job activo por pipeline.
        using var runLock = await AcquireRunLockAsync(pipelineId, cancellationToken);
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);

        return await EnqueueResyncAsync(pipelineId, entities, mode, triggeredBy, cancellationToken);
    }

    // Devuelve el pipeline al punto de un snapshot (ParquetVersions.Plan): cada tabla que cambió desde
    // entonces recupera la versión que tenía, las que aún no existían se archivan (vuelven en la
    // siguiente ejecución si sus snapshots siguen en el lago) y el estado se rebobina a ese snapshot.
    // Las copias sustituidas pasan a versión con el estado actual, así que el punto más reciente
    // deshace la restauración. El estado se rebobina antes de mover nada y se guarda tras cada tabla:
    // si falla a medias, la siguiente ejecución vuelve a leer los snapshots posteriores (el
    // consolidado deduplica por Id) y ninguna tabla ya movida pierde filas. Solo se recargan en SQL
    // las tablas que volvieron atrás. Todo bajo el lock de ejecución: entre mover ficheros y encolar
    // el re-sync no puede arrancar otro job.
    public async Task<Job?> RestorePointAsync(string pipelineId, string snapshot, CancellationToken cancellationToken = default)
    {
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        var stateStore = _stateStore
            ?? throw new InvalidOperationException("Restoring a restore point requires the pipeline state store.");

        using var runLock = await AcquireRunLockAsync(pipelineId, cancellationToken);
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);

        var state = stateStore.GetOrCreate(pipelineId);
        var plan = ParquetVersions.Plan(await _parquet.ListAllVersionsAsync(pipelineId, cancellationToken), state.Tables, snapshot);
        if (plan.Restore.Count == 0 && plan.Archive.Count == 0 && state.PendingReload.Count == 0)
        {
            throw new InvalidOperationException($"El pipeline ya está en el punto {snapshot}.");
        }

        // Cada tabla queda pendiente de recarga antes de moverla: si el restore falla después, su
        // versión de entonces ya se consumió y el reintento no la vería en su plan.
        var tables = new Dictionary<string, TableState>(state.Tables, StringComparer.OrdinalIgnoreCase);
        var pendingReload = new List<string>(state.PendingReload);
        var rewound = state with { LastProcessedSnapshot = snapshot, Tables = tables, PendingReload = pendingReload };
        stateStore.Save(rewound);
        foreach (var (entity, version) in plan.Restore)
        {
            if (!pendingReload.Contains(entity, StringComparer.OrdinalIgnoreCase))
            {
                pendingReload.Add(entity);
                stateStore.Save(rewound);
            }

            await _parquet.RestoreVersionAsync(pipelineId, entity, version.Id, CurrentAsVersion(state, entity), cancellationToken);
            if (version.Table is { } table)
            {
                tables[entity] = table;
            }
            else
            {
                tables.Remove(entity);
            }

            stateStore.Save(rewound);
        }

        foreach (var entity in plan.Archive)
        {
            await _parquet.ArchiveCurrentAsync(pipelineId, entity, CurrentAsVersion(state, entity), cancellationToken);
            tables.Remove(entity);
            pendingReload.RemoveAll(e => string.Equals(e, entity, StringComparison.OrdinalIgnoreCase));
            stateStore.Save(rewound);
        }

        var toReload = ParquetVersions.ToReload(plan, state.PendingReload);
        if (toReload.Count == 0)
        {
            return null;
        }

        // La recarga de SQL vacía PendingReload (SqlExecutor), así que un re-sync fallido las deja
        // pendientes para el siguiente intento.
        var job = await EnqueueResyncAsync(pipelineId, toReload, ResyncMode.Reload, AutoRemediation.TriggeredByUser, cancellationToken);
        _jobs.AppendEvent(job.Id, EventLevel.Info,
            $"Restaurado el punto del snapshot {snapshot}: {plan.Restore.Count} tabla(s) vuelven a su versión de entonces"
            + (plan.Archive.Count > 0 ? $" y {plan.Archive.Count} que aún no existían se archivan ({string.Join(", ", plan.Archive)})" : "")
            + $"; se recargan en SQL {toReload.Count} tabla(s) ({string.Join(", ", toReload)}).");
        return job;
    }

    private static ParquetVersion CurrentAsVersion(PipelineState state, string entity)
    {
        var table = state.Tables.GetValueOrDefault(entity);
        return new()
        {
            Table = table,
            PipelineSnapshot = string.CompareOrdinal(table?.LastSnapshot, state.LastProcessedSnapshot) > 0
                ? table!.LastSnapshot
                : state.LastProcessedSnapshot,
        };
    }

    // Crea y encola el job de re-sync. Quien llama ya tiene el lock de ejecución del pipeline.
    private async Task<Job> EnqueueResyncAsync(string pipelineId, IReadOnlyList<string> entities, ResyncMode mode, string triggeredBy, CancellationToken cancellationToken)
    {
        var jobId = NewJobId(pipelineId, "resync");

        // Manifest sintético de las entidades apuntando a los artefactos durables ya existentes
        // en ADLS (parquet + schema consolidados). SqlExecutor los lee de ahí: no re-ingesta.
        _manifests.Save(new IngestManifest
        {
            JobId = jobId,
            PipelineId = pipelineId,
            Entities = entities.Select(entity => new IngestEntityManifest
            {
                Entity = entity,
                ParquetPath = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity),
                SchemaPath = IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity),
                Snapshot = "",
            }).ToList(),
        });
        var automatic = triggeredBy == AutoRemediation.TriggeredByAuto;

        var job = new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.ResyncTable,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string>
            {
                // El re-sync no ingesta: marca el stage como omitido para que StageFlow no lo
                // muestre en blanco (parecería pendiente). Arranca directo en SQL.
                ["ingest"] = "skipped",
                ["sql"] = "pending",
                ["reconcile"] = "pending",
            },
            Events =
            [
                new JobEvent
                {
                    At = DateTimeOffset.UtcNow,
                    Level = EventLevel.Info,
                    Message = automatic
                        ? $"Corrección automática: re-sync de {string.Join(", ", entities)} ({mode})."
                        : $"Re-sync de tabla {string.Join(", ", entities)} ({mode}).",
                },
            ],
        };

        _jobs.Create(job);

        // Divergencia "antes" desde el último reporte, para el historial de remediación (un registro
        // por tabla, todos con el mismo JobId).
        var latest = _reports.GetLatestReconcile(pipelineId);
        foreach (var entity in entities)
        {
            var before = latest?.Entities
                .FirstOrDefault(e => string.Equals(e.Entity, entity, StringComparison.OrdinalIgnoreCase))?.DivergencePct ?? 0;
            _reports.SaveRemediation(new ReconcileRemediation
            {
                Id = entities.Count == 1 ? $"remed-{jobId}" : $"remed-{jobId}-{entity}",
                PipelineId = pipelineId,
                Entity = entity,
                JobId = jobId,
                Mode = mode,
                TriggeredBy = triggeredBy,
                DivergenceBefore = before,
            });
        }

        var task = new PipelineTask
        {
            Id = $"task-{jobId}-sql",
            Stage = PipelineStage.Sql,
            PipelineId = pipelineId,
            JobId = jobId,
            Payload = new Dictionary<string, object?>
            {
                ["jobType"] = JobType.ResyncTable.ToString(),
                ["entity"] = string.Join(",", entities),
                ["resyncMode"] = mode.ToString(),
                ["auto"] = automatic ? "true" : "false",
            },
        };

        await _tasks.EnqueueAsync(task, cancellationToken);
        return job;
    }

    // Quien espera el lock lo hace mientras el otro arranque termina (Force incluye un listado
    // de ADLS); pasado este margen se rechaza igual que si ya hubiera un job en curso.
    private static readonly TimeSpan RunLockTimeout = TimeSpan.FromSeconds(15);

    private Task<IDisposable> AcquireRunLockAsync(string pipelineId, CancellationToken cancellationToken) =>
        PipelineRunLock.AcquireAsync(_options.DataDir, pipelineId, RunLockTimeout, cancellationToken);

    // Sufijo aleatorio tras la marca de tiempo: con resolución de segundo, dos jobs del mismo
    // pipeline en el mismo segundo compartían id y se pisaban los archivos.
    private static string NewJobId(string pipelineId, string? kind = null) =>
        $"job-{pipelineId}-{(kind is null ? "" : kind + "-")}{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    // Alias de API: mismo comportamiento que Force (recarga desde parquet consolidado).
    public Task<Job> ReloadFromParquetAsync(string pipelineId, CancellationToken cancellationToken = default) =>
        EnqueueParquetReloadAsync(pipelineId, JobType.Force, cancellationToken);

    // Recarga SQL completa desde parquet consolidado (sin re-leer CSV de snapshots).
    // Force y reload-from-parquet comparten este camino: el origen durable es el parquet en ADLS.
    private async Task<Job> EnqueueParquetReloadAsync(
        string pipelineId,
        JobType jobType,
        CancellationToken cancellationToken = default)
    {
        if (jobType is not (JobType.Force or JobType.ReloadFromParquet))
        {
            throw new InvalidOperationException($"Tipo de job no soportado para recarga parquet: {jobType}.");
        }

        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");

        using var runLock = await AcquireRunLockAsync(pipelineId, cancellationToken);
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);

        var artifacts = await _parquet.ListAsync(pipelineId, cancellationToken);
        var entities = artifacts
            .Where(a => a.Path.Count(c => c == '/') == 1 && a.Path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            .Select(a => Path.GetFileNameWithoutExtension(a.Path.Split('/').Last()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entities.Count == 0)
        {
            throw new InvalidOperationException("No hay parquet consolidado en ADLS para este pipeline.");
        }

        var suffix = jobType == JobType.Force ? "force" : "reload";
        var jobId = NewJobId(pipelineId, suffix);
        _manifests.Save(new IngestManifest
        {
            JobId = jobId,
            PipelineId = pipelineId,
            Entities = entities.Select(entity => new IngestEntityManifest
            {
                Entity = entity,
                ParquetPath = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity),
                SchemaPath = IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity),
                Snapshot = "",
            }).ToList(),
        });

        var job = new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.Force,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "skipped",
                ["sql"] = "pending",
                ["reconcile"] = "pending",
            },
            Events =
            [
                new JobEvent
                {
                    At = DateTimeOffset.UtcNow,
                    Level = EventLevel.Info,
                    Message = $"Recarga desde parquet consolidado ({entities.Count} tabla(s)).",
                },
            ],
        };

        _jobs.Create(job);

        var task = new PipelineTask
        {
            Id = $"task-{jobId}-sql",
            Stage = PipelineStage.Sql,
            PipelineId = pipelineId,
            JobId = jobId,
            Payload = new Dictionary<string, object?>
            {
                ["jobType"] = JobType.Force.ToString(),
                ["resyncMode"] = ResyncMode.Reload.ToString(),
            },
        };

        await _tasks.EnqueueAsync(task, cancellationToken);
        return job;
    }
}