using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Workers;

public sealed class WorkerHostService : BackgroundService
{
    private readonly AppOptions _options;
    private readonly ITaskStore _tasks;
    private readonly IStageRouter _router;
    private readonly IIngestExecutor _ingest;
    private readonly ISqlExecutor _sql;
    private readonly IReconcileExecutor _reconcile;
    private readonly WorkerRegistry _workers;
    private readonly IJobService _jobs;
    private readonly SettingsStore _settings;
    private readonly PipelineStore _pipelines;
    private readonly AlertService _alerts;
    private readonly ILogger<WorkerHostService> _logger;
    private readonly IActivityLog _activity;

    public WorkerHostService(
        IOptions<AppOptions> options,
        ITaskStore tasks,
        IStageRouter router,
        IIngestExecutor ingest,
        ISqlExecutor sql,
        IReconcileExecutor reconcile,
        WorkerRegistry workers,
        IJobService jobs,
        SettingsStore settings,
        PipelineStore pipelines,
        AlertService alerts,
        ILogger<WorkerHostService> logger,
        IActivityLog activity)
    {
        _options = options.Value;
        _tasks = tasks;
        _router = router;
        _ingest = ingest;
        _sql = sql;
        _reconcile = reconcile;
        _workers = workers;
        _jobs = jobs;
        _settings = settings;
        _pipelines = pipelines;
        _alerts = alerts;
        _logger = logger;
        _activity = activity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.IsDistributed && string.IsNullOrWhiteSpace(_options.WorkerStage))
        {
            _logger.LogInformation("Worker host disabled in distributed mode without WORKER_STAGE.");
            return;
        }

        var workerId = _options.WorkerId ?? Environment.MachineName;
        var stages = ResolveStages();

        _logger.LogInformation("Worker host started as {WorkerId} stages={Stages} mode={Mode}",
            workerId, string.Join(",", stages), _options.ExecutionMode);

        // Registro inicial como ocioso: Touch solo refresca un fichero que ya existe, así que sin
        // esto un worker recién arrancado no aparecía en el Panel hasta reclamar su primera tarea.
        _workers.SetIdle(workerId, stages[0]);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var lastTouch = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Señal de vida para el Dashboard aunque el worker esté ocioso (WorkerRegistry
            // purga los que llevan StaleAfter sin ella). Con trabajo, la da el latido de la tarea.
            if (DateTimeOffset.UtcNow - lastTouch >= TimeSpan.FromSeconds(30))
            {
                _workers.Touch(workerId);
                lastTouch = DateTimeOffset.UtcNow;
            }

            foreach (var stage in stages)
            {
                try
                {
                    await ProcessStageAsync(stage, workerId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Defensa en profundidad: ningún error transitorio de cola/IO debe propagar.
                    // Con BackgroundServiceExceptionBehavior=StopHost (default) eso mataría el
                    // container; aquí se loguea y se continúa al siguiente tick.
                    _logger.LogError(ex, "Error transitorio procesando stage {Stage}; se continúa.", stage);
                }
            }
        }
    }

    private IReadOnlyList<PipelineStage> ResolveStages()
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkerStage)
            && Enum.TryParse<PipelineStage>(_options.WorkerStage, ignoreCase: true, out var single))
        {
            return [single];
        }

        return Enum.GetValues<PipelineStage>();
    }

    // Intervalo del latido; solo lo fijan los tests (en producción, un tercio del lease).
    internal TimeSpan? HeartbeatInterval { get; set; }

    // internal: los tests ejercitan el flujo de una tarea sin el bucle de polling.
    internal async Task ProcessStageAsync(PipelineStage stage, string workerId, CancellationToken ct)
    {
        var task = await _tasks.TryClaimAsync(stage, workerId, ct);
        if (task is null)
        {
            return;
        }

        // Job cancelado mientras la tarea esperaba, o reclamada tras caer el worker que la ejecutaba:
        // se libera sin ejecutarla (el router no avanza un job cancelado).
        if (_jobs.Get(task.JobId)?.Status == JobStatus.Cancelled)
        {
            await _tasks.CompleteAsync(task.Id, stage, workerId, ct);
            return;
        }

        _workers.RegisterHeartbeat(workerId, stage, task.Id, task.PipelineId, task.JobId);

        // Latido del lease mientras corre el stage: un stage largo (ingest full) no debe
        // perder su lease y ser reclamado por otra réplica. Si el latido detecta que la tarea
        // dejó de ser nuestra, cancela el stage (stageCts) para no ejecutarlo en paralelo.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Dos motivos para cortar el stage: lease perdido (la tarea ya es de otro worker) o job
        // cancelado (sigue siendo nuestra y hay que liberarla).
        var jobCancelled = false;
        var heartbeat = HeartbeatLoopAsync(
            task.Id, stage, workerId, task.JobId, stageCts.Cancel, () =>
            {
                jobCancelled = true;
                stageCts.Cancel();
            },
            heartbeatCts.Token, HeartbeatInterval);

        // El latido se detiene ANTES de completar o fallar la tarea: un latido tardío podría
        // reescribir claimed/X después de que la tarea pasara a done/ o error/.
        async Task StopHeartbeatAsync()
        {
            heartbeatCts.Cancel();
            await heartbeat;
        }

        try
        {
            switch (stage)
            {
                case PipelineStage.Ingest:
                    await _ingest.ExecuteAsync(task, stageCts.Token);
                    break;
                case PipelineStage.Sql:
                    await _sql.ExecuteAsync(task, stageCts.Token);
                    break;
                case PipelineStage.Reconcile:
                    await _reconcile.ExecuteAsync(task, stageCts.Token);
                    break;
            }

            await StopHeartbeatAsync();
            var job = await _tasks.CompleteAsync(task.Id, stage, workerId, ct);
            if (job is not null)
            {
                await _router.RouteCompletedTaskAsync(job, ct);
            }
        }
        catch (Exception) when (jobCancelled && !ct.IsCancellationRequested)
        {
            // Job cancelado: el stage se cortó (por la cancelación o por cómo la tradujo el driver,
            // p. ej. una SqlException). Ni es un fallo ni avanza; se libera la tarea para que el
            // pipeline admita otro job.
            await StopHeartbeatAsync();
            await _tasks.CompleteAsync(task.Id, stage, workerId, ct);
            _jobs.AppendEvent(task.JobId, EventLevel.Warning, $"Etapa {stage} detenida por la cancelación del job.", null, "Event.Job.StageStoppedByCancel", stage);
            _logger.LogInformation("Stage {Stage} de la tarea {TaskId} detenido: job cancelado.", stage, task.Id);
        }
        catch (OperationCanceledException) when (stageCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Lease perdido: la tarea la continúa quien la reclamó; no se marca el job como fallido.
            _logger.LogWarning("Stage {Stage} de la tarea {TaskId} abandonado por pérdida del lease.", stage, task.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Una cancelación por apagado no es un fallo de la tarea: se propaga (el bucle de
            // ExecuteAsync termina) y la tarea, aún en claimed/, se reclama al vencer el lease.
            _logger.LogError(ex, "Task {TaskId} failed in stage {Stage}", task.Id, stage);
            await StopHeartbeatAsync();
            await _tasks.FailAsync(task.Id, stage, workerId, ex.Message, ct);
            await MarkJobFailedAsync(task.JobId, stage, ex.Message, ct, KnownErrors.CodeFrom(ex));
        }
        finally
        {
            await StopHeartbeatAsync();
            _workers.SetIdle(workerId, stage);
        }
    }

    // interval solo lo fijan los tests; en producción es un tercio del lease (mínimo 5 s).
    internal async Task HeartbeatLoopAsync(
        string taskId,
        PipelineStage stage,
        string workerId,
        string jobId,
        Action onLeaseLost,
        Action onJobCancelled,
        CancellationToken ct,
        TimeSpan? interval = null)
    {
        var leaseSeconds = Math.Max(15, _settings.Get().TaskLeaseSeconds);
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(Math.Max(5, leaseSeconds / 3)));
        var cancelSignalled = false;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (await _tasks.HeartbeatAsync(taskId, stage, workerId, ct) is null)
                    {
                        // La tarea ya no es nuestra (otra réplica la reclamó tras vencer el lease, o
                        // se canceló el job): seguir ejecutando duplicaría el trabajo.
                        _logger.LogWarning("Lease perdido para la tarea {TaskId}: se abandona el stage.", taskId);
                        onLeaseLost();
                        return;
                    }

                    _workers.Touch(workerId);

                    // Cancelado desde la UI o la API: la cancelación ya no borra la tarea en vuelo,
                    // es el latido quien la ve y detiene el stage. Sigue renovando el lease hasta que
                    // el stage se detenga de verdad, para que el pipeline siga ocupado mientras tanto.
                    if (!cancelSignalled && _jobs.Get(jobId)?.Status == JobStatus.Cancelled)
                    {
                        cancelSignalled = true;
                        onJobCancelled();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Un fallo puntual (IO transitorio) no debe apagar el latido para siempre: sin
                    // latido el lease vence y otra réplica ejecutaría el mismo stage en paralelo.
                    _logger.LogWarning(ex, "Heartbeat falló para la tarea {TaskId}; se reintenta en el próximo tick.", taskId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // El stage terminó: se cancela el latido.
        }
    }

    internal async Task MarkJobFailedAsync(string jobId, PipelineStage stage, string message, CancellationToken ct, string? code = null)
    {
        var job = _jobs.Get(jobId);
        if (job is null)
        {
            return;
        }

        var stageKey = stage.ToString().ToLowerInvariant();
        var stageStatus = new Dictionary<string, string>(job.StageStatus)
        {
            [stageKey] = "error",
        };

        _jobs.Update(job.Id, job with
        {
            Status = JobStatus.Error,
            FinishedAt = DateTimeOffset.UtcNow,
            StageStatus = stageStatus,
        });
        _jobs.AppendEvent(jobId, EventLevel.Error, message, code);
        _activity.LogSystem("job.failed", $"Job {jobId} falló en la etapa {stage}: {message}", job.PipelineId, jobId, "error",
            "Activity.Job.Failed", jobId, stage.ToString(), message);

        var pipeline = _pipelines.GetById(job.PipelineId);
        await _alerts.SendPipelineFailureAsync(new PipelineFailureAlert
        {
            PipelineId = job.PipelineId,
            PipelineName = pipeline?.Name,
            JobId = jobId,
            JobType = job.Type.ToString(),
            Stage = stageKey,
            Message = message,
        }, ct);
    }
}