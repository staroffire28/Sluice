using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
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

    // Lo que muta recibe quién actúa (AuditActor) y queda en el registro de actividad: el
    // resultado, el rechazo o el error.
    Task<Job> RunPipelineAsync(AuditActor actor, string pipelineId, JobType type, CancellationToken cancellationToken = default);
    Task<Job> RetryJobAsync(AuditActor actor, string jobId, CancellationToken cancellationToken = default);
    Task<Job> ResyncTableAsync(AuditActor actor, string pipelineId, string entity, ResyncMode mode, CancellationToken cancellationToken = default);

    // Varias tablas en un solo job (un solo job activo por pipeline); con el actor AutoFix es la
    // corrección automática. Por defecto no soportado, para los dobles de test que no lo ejercitan.
    Task<Job> ResyncTablesAsync(AuditActor actor, string pipelineId, IReadOnlyList<string> entities, ResyncMode mode, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<Job> ReloadFromParquetAsync(AuditActor actor, string pipelineId, CancellationToken cancellationToken = default);

    // Devuelve todo el pipeline al punto de restauración de un snapshot y recarga SQL de las
    // tablas que volvieron atrás (null si ninguna volvió atrás y solo se archivaron tablas).
    Task<Job?> RestorePointAsync(AuditActor actor, string pipelineId, string snapshot, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // Comparación de valores (hash por fila) de una tabla o, con entity null, de todo el pipeline.
    Task<Job> CompareValuesAsync(AuditActor actor, string pipelineId, string? entity, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task CancelAsync(AuditActor actor, string jobId, CancellationToken cancellationToken = default);

    // Si el pipeline tiene un job activo (o uno cancelado que aún se detiene): un Run se rechazaría.
    Task<bool> IsBusyAsync(string pipelineId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // Deja una ejecución en cola para cuando el pipeline quede libre; la lanza el scheduler.
    QueuedRun QueueRun(AuditActor actor, string pipelineId, JobType type) =>
        throw new NotSupportedException();

    // Lo quitado de la cola, o null si no había nada (o ya estaba reservado para arrancar).
    QueuedRun? DequeueRun(AuditActor actor, string pipelineId) =>
        throw new NotSupportedException();

    // El scheduler lanza lo que estaba en cola. Actúa la cola (AuditActor.Queue); el job guarda
    // como TriggeredBy a quien lo pidió.
    Task<Job> StartQueuedAsync(string pipelineId, QueuedRun queued, CancellationToken cancellationToken = default) =>
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
    private readonly IAuditLog _audit;
    private readonly PipelineStateStore? _stateStore;
    private readonly RunQueueService? _runQueue;
    private readonly SettingsStore? _settings;

    public OrchestrationService(
        IOptions<AppOptions> options,
        IPipelineService pipelines,
        IJobService jobs,
        ITaskStore tasks,
        WorkerRegistry workers,
        IngestManifestStore manifests,
        ReportStore reports,
        IParquetArtifactService parquet,
        IAuditLog audit,
        PipelineStateStore? stateStore = null,
        RunQueueService? runQueue = null,
        SettingsStore? settings = null)
    {
        _audit = audit;
        _stateStore = stateStore;
        _runQueue = runQueue;
        _settings = settings;
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
    // Un job que ya no está en curso no se toca (no lanza): queda como rechazo en el registro.
    public Task CancelAsync(AuditActor actor, string jobId, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "job.cancel",
            _jobs.Get(jobId)?.PipelineId,
            () => CancelCoreAsync(jobId, cancellationToken),
            cancelled => cancelled
                ? new AuditResult($"Cancelación del job {jobId}.", MessageKey: "Activity.Job.Cancelled", MessageArgs: [jobId])
                : new AuditResult($"El job {jobId} no estaba en curso: no se canceló.", Outcome: AuditOutcome.Rejected,
                    MessageKey: "Activity.Job.NotRunning", MessageArgs: [jobId]),
            $"No se pudo cancelar el job {jobId}",
            failureKey: "Activity.Failed.JobCancel", failureArgs: [jobId],
            jobId: jobId);

    private async Task<bool> CancelCoreAsync(string jobId, CancellationToken cancellationToken)
    {
        // Partial sin FinishedAt también está en curso (ingest parcial que sigue con SQL).
        var job = _jobs.Get(jobId);
        if (job is null || !JobProgressHelper.IsActive(job))
        {
            return false;
        }

        await _tasks.CancelJobTasksAsync(jobId, cancellationToken);

        // Releer tras el await (como el watchdog): el executor en vuelo pudo guardar entretanto
        // tiempos, Summary o entidades, y un Update con la copia previa los pisaría.
        job = _jobs.Get(jobId);
        if (job is null || !JobProgressHelper.IsActive(job))
        {
            return false;
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
        _jobs.AppendEvent(jobId, EventLevel.Warning, "Job cancelado por el usuario.", null, "Event.Job.CancelledByUser");
        return true;
    }

    public Task<Job> RunPipelineAsync(AuditActor actor, string pipelineId, JobType type, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "pipeline.run",
            pipelineId,
            () => StartAsync(actor, pipelineId, type, entity: null, cancellationToken),
            job => new AuditResult($"Ejecución {type} del pipeline {pipelineId} (job {job.Id}).", job.Id,
                MessageKey: "Activity.Pipeline.Run", MessageArgs: [type.ToString(), pipelineId, job.Id]),
            $"No se pudo iniciar {type} en {pipelineId}",
            failureKey: "Activity.Failed.PipelineRun", failureArgs: [type.ToString(), pipelineId]);

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
    public QueuedRun QueueRun(AuditActor actor, string pipelineId, JobType type) =>
        _audit.Audit(
            actor,
            "pipeline.queue",
            pipelineId,
            () => Enqueue(actor, pipelineId, type),
            _ => new AuditResult($"{type} en cola en el pipeline {pipelineId}: arranca cuando termine el job en curso.",
                MessageKey: "Activity.Queue.Queued", MessageArgs: [type.ToString(), pipelineId]),
            $"No se pudo dejar {type} en cola en {pipelineId}",
            failureKey: "Activity.Failed.Queue", failureArgs: [type.ToString(), pipelineId]);

    private QueuedRun Enqueue(AuditActor actor, string pipelineId, JobType type)
    {
        var queue = _runQueue
            ?? throw new InvalidOperationException("Queueing a run requires the run queue.");
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        if (!PipelineRunQueue.QueueableTypes.Contains(type))
        {
            throw new InvalidOperationException($"{type} no se puede dejar en cola.");
        }

        var run = new QueuedRun(type, DateTimeOffset.UtcNow, actor);
        return queue.TryEnqueue(pipelineId, run) switch
        {
            null => run,
            { } queued when queued.Type == type => throw new InvalidOperationException($"{type} ya está en cola para este pipeline."),
            { } queued => throw new InvalidOperationException(
                $"Ya hay {queued.Type} en cola para este pipeline: quítalo de la cola antes de encolar {type}."),
        };
    }

    public QueuedRun? DequeueRun(AuditActor actor, string pipelineId) =>
        (_runQueue ?? throw new InvalidOperationException("Dequeueing a run requires the run queue."))
            .Dequeue(actor, pipelineId);

    public Task<Job> StartQueuedAsync(string pipelineId, QueuedRun queued, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            AuditActor.Queue,
            "pipeline.queue.started",
            pipelineId,
            () => StartAsync(queued.RequestedBy ?? AuditActor.Queue, pipelineId, queued.Type, entity: null, cancellationToken),
            job => new AuditResult(
                $"Arranca {queued.Type} en cola del pipeline {pipelineId} (job {job.Id}), pedido por {queued.RequestedBy?.Label ?? "un actor sin registrar"}.",
                job.Id,
                MessageKey: "Activity.Queue.Started",
                MessageArgs: [queued.Type.ToString(), pipelineId, job.Id, (object?)queued.RequestedBy?.Label ?? new EventPart("Activity.Queue.UnknownActor")]),
            $"No se pudo lanzar {queued.Type} en cola del pipeline {pipelineId}",
            failureKey: "Activity.Failed.QueueStart", failureArgs: [queued.Type.ToString(), pipelineId]);

    // Comparación de valores bajo demanda: de una tabla o, sin entity, de todas las del pipeline.
    public Task<Job> CompareValuesAsync(AuditActor actor, string pipelineId, string? entity, CancellationToken cancellationToken = default)
    {
        entity = string.IsNullOrWhiteSpace(entity) ? null : entity.Trim();
        return _audit.AuditAsync(
            actor,
            "reconcile.values",
            pipelineId,
            () => StartAsync(actor, pipelineId, JobType.CompareValues, entity, cancellationToken),
            job => new AuditResult($"Comparación de valores de {entity ?? "todas las tablas"} (job {job.Id}).", job.Id,
                MessageKey: "Activity.Compare.Started", MessageArgs: [(object?)entity ?? new EventPart("Activity.Compare.AllTables"), job.Id]),
            "No se pudo iniciar la comparación de valores",
            failureKey: "Activity.Failed.Compare",
            target: entity);
    }

    private async Task<Job> StartAsync(AuditActor actor, string pipelineId, JobType type, string? entity, CancellationToken cancellationToken)
    {
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");

        // Nota: Pipeline.Enabled significa "scheduler activo", NO "ejecutable". El Run manual
        // siempre se permite (crear pipeline → correr a mano → activar scheduler al validar).

        // Force tiene su propio camino (con la misma guarda y el mismo lock, que no es reentrante).
        if (type == JobType.Force)
        {
            return await EnqueueParquetReloadAsync(actor, pipelineId, JobType.Force, cancellationToken);
        }

        // Guardia anti-doble-run: si ya hay un job en curso para este pipeline, no se arranca
        // otro (evita dos jobs concurrentes pisándose). Mismo criterio que usa el scheduler.
        // El lock hace atómico "comprobar + crear" frente a otros Run simultáneos.
        using var runLock = await AcquireRunLockAsync(pipelineId, cancellationToken);
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);
        ThrowIfRestorePending(pipelineId);

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
            TriggeredBy = actor,
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
    // StartAsync, salvo que el fallo fuera en SQL tras un ingest completo (entonces recarga
    // SQL desde el parquet consolidado). Crea un Job NUEVO; el job fallido se conserva como historial
    // (no se edita ni se borra). La guarda anti-doble-run de StartAsync evita arrancar si
    // ya hay uno en curso. La tarea fallida en tasks/{stage}/error/ queda como evidencia.
    public Task<Job> RetryJobAsync(AuditActor actor, string jobId, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "pipeline.retry",
            _jobs.Get(jobId)?.PipelineId,
            () => RetryCoreAsync(actor, jobId, cancellationToken),
            job => new AuditResult($"Reintento del job {jobId} (nuevo job {job.Id}).", job.Id,
                MessageKey: "Activity.Job.Retried", MessageArgs: [jobId, job.Id]),
            $"No se pudo reintentar el job {jobId}",
            failureKey: "Activity.Failed.JobRetry", failureArgs: [jobId],
            target: jobId);

    private async Task<Job> RetryCoreAsync(AuditActor actor, string jobId, CancellationToken cancellationToken)
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
            return await EnqueueParquetReloadAsync(actor, job.PipelineId, JobType.Force, cancellationToken);
        }

        // Ingest terminó pero SQL no (falló, o el watchdog cerró el job con SQL en pending/running):
        // el snapshot ya cuenta como procesado, así que un Incremental no encontraría nada nuevo y
        // reportaría Ok sin cargar los datos. Se recarga SQL desde el parquet consolidado, que ya
        // contiene lo ingerido.
        if (job.StageStatus.GetValueOrDefault("ingest") == "done"
            && job.StageStatus.GetValueOrDefault("sql") != "done")
        {
            return await EnqueueParquetReloadAsync(actor, job.PipelineId, JobType.Force, cancellationToken);
        }

        // Ingest y SQL hechos pero Reconcile no (falló o el watchdog cerró el job): los datos ya
        // están cargados y un Incremental no conciliaría nada. Se reintenta solo la conciliación.
        if (job.StageStatus.GetValueOrDefault("ingest") == "done"
            && job.StageStatus.GetValueOrDefault("sql") == "done"
            && job.StageStatus.GetValueOrDefault("reconcile") != "done")
        {
            return await StartAsync(actor, job.PipelineId, JobType.Reconcile, entity: null, cancellationToken);
        }

        // ResyncTable arranca en SQL con un manifest sintético de una entidad; reintentarlo como
        // tal sin contexto sería frágil. Se reintenta como Incremental (re-ejecución estándar
        // del pipeline). El resto de tipos (Incremental/Reconcile/Diff) arrancan en Ingest.
        var retryType = job.Type == JobType.ResyncTable
            ? JobType.Incremental
            : job.Type;
        return await StartAsync(actor, job.PipelineId, retryType, entity: null, cancellationToken);
    }

    // Re-sincronización de UNA tabla sin reprocesar el pipeline: arranca en el stage SQL
    // (sin ingest) re-mergeando el parquet consolidado durable ya existente; el StageRouter
    // encadena SQL → Reconcile como siempre. Modo Reload = upsert + borrado de huérfanas, sin
    // vaciar la tabla (corrige ambas direcciones, ver SqlExecutor); Remerge = solo upsert.
    public Task<Job> ResyncTableAsync(AuditActor actor, string pipelineId, string entity, ResyncMode mode, CancellationToken cancellationToken = default) =>
        ResyncTablesAsync(actor, pipelineId, string.IsNullOrWhiteSpace(entity) ? [] : [entity], mode, cancellationToken);

    // Varias tablas en un solo job: la corrección automática re-sincroniza de una vez todas las que
    // divergen (un solo job activo por pipeline). El payload "entity" las lleva separadas por comas.
    public Task<Job> ResyncTablesAsync(AuditActor actor, string pipelineId, IReadOnlyList<string> entities, ResyncMode mode, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "table.resync",
            pipelineId,
            () => ResyncTablesCoreAsync(actor, pipelineId, entities, mode, cancellationToken),
            job => new AuditResult($"Re-sync {mode} de {string.Join(", ", entities)} en {pipelineId} (job {job.Id}).", job.Id,
                MessageKey: "Activity.Resync.Started", MessageArgs: [mode.ToString(), string.Join(", ", entities), pipelineId, job.Id]),
            $"No se pudo iniciar el re-sync de {string.Join(", ", entities)}",
            failureKey: "Activity.Failed.Resync", failureArgs: [string.Join(", ", entities)],
            target: string.Join(",", entities));

    private async Task<Job> ResyncTablesCoreAsync(AuditActor actor, string pipelineId, IReadOnlyList<string> entities, ResyncMode mode, CancellationToken cancellationToken)
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
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);
        ThrowIfRestorePending(pipelineId);

        return await EnqueueResyncAsync(actor, pipelineId, entities, mode, cancellationToken);
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
    public Task<Job?> RestorePointAsync(AuditActor actor, string pipelineId, string snapshot, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "pipeline.restore",
            pipelineId,
            () => RestorePointCoreAsync(actor, pipelineId, snapshot, cancellationToken),
            job => new AuditResult(
                job is null
                    ? $"Restaurado el punto {snapshot} en {pipelineId}, sin tablas que recargar en SQL."
                    : $"Restaurado el punto {snapshot} en {pipelineId}; recarga en SQL en el job {job.Id}.",
                job?.Id,
                MessageKey: job is null ? "Activity.Restore.NoReload" : "Activity.Restore.WithReload",
                MessageArgs: [snapshot, pipelineId, job?.Id]),
            $"No se pudo restaurar el punto {snapshot} en {pipelineId}",
            failureKey: "Activity.Failed.Restore", failureArgs: [snapshot, pipelineId],
            target: snapshot);

    private async Task<Job?> RestorePointCoreAsync(AuditActor actor, string pipelineId, string snapshot, CancellationToken cancellationToken)
    {
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        var stateStore = _stateStore
            ?? throw new InvalidOperationException("Restoring a restore point requires the pipeline state store.");

        using var runLock = await AcquireRunLockAsync(pipelineId, cancellationToken);
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);

        var state = stateStore.GetOrCreate(pipelineId);
        if (state.RestoreInProgress is { } halfDone && halfDone.Snapshot != snapshot)
        {
            throw new InvalidOperationException(
                $"El restore al punto {halfDone.Snapshot} quedó a medias: termínalo restaurando ese mismo punto antes de elegir otro.");
        }

        var from = state.RestoreInProgress is { } inProgress ? inProgress.FromSnapshot : state.LastProcessedSnapshot;
        if (state.PendingRestore.Count > 0)
        {
            state = await FinishPendingRestoreAsync(stateStore, pipelineId, state, from, cancellationToken);
        }

        // La lista de puntos puede llevar abierta un rato: si la poda ya se llevó las versiones del
        // punto, el plan archivaría las tablas en vez de devolverlas. El de un restore a medias no se
        // poda (la ingesta espera a que termine), aunque el tiempo lo saque de los conservados. Con
        // settings.json ilegible tampoco se poda, así que sus versiones siguen ahí.
        var versions = await _parquet.ListAllVersionsAsync(pipelineId, cancellationToken);
        if (state.RestoreInProgress?.Snapshot != snapshot
            && _settings is not null
            && _settings.TryGet(out var settings)
            && !ParquetVersions.KeptPoints(versions, settings.ParquetVersionDays, DateTimeOffset.UtcNow, state.RetiredSnapshots).Any(p => p.Snapshot == snapshot))
        {
            throw new InvalidOperationException(
                $"El punto {snapshot} ya no se conserva (la poda se llevó sus versiones): vuelve a abrir la lista de puntos.");
        }

        var plan = ParquetVersions.Plan(versions, state.Tables, snapshot);
        if (plan.Restore.Count == 0 && plan.Archive.Count == 0 && state.PendingReload.Count == 0)
        {
            if (state.RestoreInProgress is not null)
            {
                stateStore.Save(state with { RestoreInProgress = null });
                return null;
            }

            throw new InvalidOperationException($"El pipeline ya está en el punto {snapshot}.");
        }

        // Cada tabla queda pendiente de recarga y marcada (versión y estado de entonces) antes de
        // moverla: si el restore falla después, su versión de entonces ya se consumió y el reintento
        // no la vería en su plan. Los snapshots de las versiones que se retiran se guardan antes por
        // lo mismo (siguen contando en el escalonado de los puntos).
        var tables = new Dictionary<string, TableState>(state.Tables, StringComparer.OrdinalIgnoreCase);
        var pendingReload = new List<string>(state.PendingReload);
        var pendingRestore = new Dictionary<string, PendingRestoreTable>(StringComparer.OrdinalIgnoreCase);
        var rewound = state with { LastProcessedSnapshot = snapshot, Tables = tables, PendingReload = pendingReload, PendingRestore = pendingRestore, RestoreInProgress = new() { Snapshot = snapshot, FromSnapshot = from }, RetiredSnapshots = ParquetVersions.Retire(state.RetiredSnapshots, versions, plan) };
        stateStore.Save(rewound);
        foreach (var (entity, version) in plan.Restore)
        {
            if (!pendingReload.Contains(entity, StringComparer.OrdinalIgnoreCase))
            {
                pendingReload.Add(entity);
            }

            var current = state.Tables.GetValueOrDefault(entity);
            pendingRestore[entity] = new PendingRestoreTable { VersionId = version.Id, Table = version.Table, FromTable = current };
            stateStore.Save(rewound);

            await _parquet.RestoreVersionAsync(pipelineId, entity, version.Id, CurrentAsVersion(current, from), cancellationToken);
            ParquetVersions.SetTable(tables, entity, version.Table);
            pendingRestore.Remove(entity);
            stateStore.Save(rewound);
        }

        foreach (var entity in plan.Archive)
        {
            await _parquet.ArchiveCurrentAsync(pipelineId, entity, CurrentAsVersion(state.Tables.GetValueOrDefault(entity), from), cancellationToken);
            tables.Remove(entity);
            pendingReload.RemoveAll(e => string.Equals(e, entity, StringComparison.OrdinalIgnoreCase));
            stateStore.Save(rewound);
        }

        stateStore.Save(rewound with { RestoreInProgress = null });
        var toReload = ParquetVersions.ToReload(plan, state.PendingReload);
        if (toReload.Count == 0)
        {
            return null;
        }

        // La recarga de SQL vacía PendingReload (SqlExecutor), así que un re-sync fallido las deja
        // pendientes para el siguiente intento.
        var job = await EnqueueResyncAsync(actor, pipelineId, toReload, ResyncMode.Reload, cancellationToken);
        _jobs.AppendEvent(job.Id, EventLevel.Info,
            $"Restaurado el punto del snapshot {snapshot}: {plan.Restore.Count} tabla(s) vuelven a su versión de entonces"
            + (plan.Archive.Count > 0 ? $" y {plan.Archive.Count} que aún no existían se archivan ({string.Join(", ", plan.Archive)})" : "")
            + $"; se recargan en SQL {toReload.Count} tabla(s) ({string.Join(", ", toReload)}).",
            null, "Event.Restore.Done", snapshot, plan.Restore.Count,
            plan.Archive.Count > 0 ? new EventPart("Event.Restore.ArchivedPart", plan.Archive.Count, string.Join(", ", plan.Archive)) : "",
            toReload.Count, string.Join(", ", toReload));
        return job;
    }

    // Termina las tablas que el restore a medias del mismo punto marcó y no llegó a cerrar: si su
    // versión sigue en la lista el fichero no llegó a moverse y se mueve; si no, ya está en su sitio.
    // Después el estado vuelve a cuadrar con los ficheros y el plan nuevo sale bien.
    private async Task<PipelineState> FinishPendingRestoreAsync(
        PipelineStateStore stateStore, string pipelineId, PipelineState state, string? from, CancellationToken cancellationToken)
    {
        var versions = await _parquet.ListAllVersionsAsync(pipelineId, cancellationToken);
        var tables = new Dictionary<string, TableState>(state.Tables, StringComparer.OrdinalIgnoreCase);
        var pendingRestore = new Dictionary<string, PendingRestoreTable>(state.PendingRestore, StringComparer.OrdinalIgnoreCase);
        var finished = state with { Tables = tables, PendingRestore = pendingRestore };
        foreach (var (entity, mark) in state.PendingRestore)
        {
            var notMoved = versions
                .Where(v => string.Equals(v.Key, entity, StringComparison.OrdinalIgnoreCase))
                .Any(v => v.Value.Any(version => version.Id == mark.VersionId));
            if (notMoved)
            {
                await _parquet.RestoreVersionAsync(pipelineId, entity, mark.VersionId, CurrentAsVersion(mark.FromTable, from), cancellationToken);
            }

            ParquetVersions.SetTable(tables, entity, mark.Table);
            pendingRestore.Remove(entity);
            stateStore.Save(finished);
        }

        return finished;
    }

    private static ParquetVersion CurrentAsVersion(TableState? table, string? pipelineSnapshot) => new()
    {
        Table = table,
        PipelineSnapshot = string.CompareOrdinal(table?.LastSnapshot, pipelineSnapshot) > 0
            ? table!.LastSnapshot
            : pipelineSnapshot,
    };

    // Crea y encola el job de re-sync. Quien llama ya tiene el lock de ejecución del pipeline.
    private async Task<Job> EnqueueResyncAsync(AuditActor actor, string pipelineId, IReadOnlyList<string> entities, ResyncMode mode, CancellationToken cancellationToken)
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
        var automatic = actor == AuditActor.AutoFix;

        var job = new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.ResyncTable,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Sql,
            TriggeredBy = actor,
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
                TriggeredBy = automatic ? AutoRemediation.TriggeredByAuto : AutoRemediation.TriggeredByUser,
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

    // Un restore a medias deja marcas que solo valen mientras nadie toque esas tablas: hasta que
    // RestorePointAsync las termine no arranca otro job, o su estado pisaría lo que ese job escriba.
    private void ThrowIfRestorePending(string pipelineId)
    {
        if (_stateStore?.GetOrCreate(pipelineId) is { } state
            && (state.PendingRestore.Count > 0 || state.RestoreInProgress is not null))
        {
            throw new RestorePendingException(state.RestoreInProgress?.Snapshot);
        }
    }

    private Task<IDisposable> AcquireRunLockAsync(string pipelineId, CancellationToken cancellationToken) =>
        PipelineRunLock.AcquireAsync(_options.DataDir, pipelineId, PipelineRunLock.DefaultTimeout, cancellationToken);

    // Sufijo aleatorio tras la marca de tiempo: con resolución de segundo, dos jobs del mismo
    // pipeline en el mismo segundo compartían id y se pisaban los archivos.
    private static string NewJobId(string pipelineId, string? kind = null) =>
        $"job-{pipelineId}-{(kind is null ? "" : kind + "-")}{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    // Alias de API: mismo comportamiento que Force (recarga desde parquet consolidado).
    public Task<Job> ReloadFromParquetAsync(AuditActor actor, string pipelineId, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "pipeline.reload-from-parquet",
            pipelineId,
            () => EnqueueParquetReloadAsync(actor, pipelineId, JobType.Force, cancellationToken),
            job => new AuditResult($"Recarga desde parquet del pipeline {pipelineId} (job {job.Id}).", job.Id,
                MessageKey: "Activity.Reload.Started", MessageArgs: [pipelineId, job.Id]),
            $"No se pudo iniciar la recarga desde parquet de {pipelineId}",
            failureKey: "Activity.Failed.Reload", failureArgs: [pipelineId]);

    // Recarga SQL completa desde parquet consolidado (sin re-leer CSV de snapshots).
    // Force y reload-from-parquet comparten este camino: el origen durable es el parquet en ADLS.
    private async Task<Job> EnqueueParquetReloadAsync(
        AuditActor actor,
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
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException("Pipeline not found.");
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId, "Ya hay un job en ejecución para este pipeline.", cancellationToken);
        ThrowIfRestorePending(pipelineId);

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
            TriggeredBy = actor,
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