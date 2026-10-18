using System.Globalization;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Scheduler;

public sealed class PipelineSchedulerService : BackgroundService
{
    private readonly AppOptions _options;
    private readonly SettingsStore _settings;
    private readonly IPipelineService _pipelines;
    private readonly IOrchestrationService _orchestration;
    private readonly IJobService _jobs;
    private readonly AlertService _alerts;
    private readonly DeadmanService _deadman;
    private readonly ISnapshotCatalogService _snapshots;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly ILogger<PipelineSchedulerService> _logger;
    private readonly IActivityLog _activity;
    private readonly OrphanedJobWatchdog _watchdog;
    private readonly QueueStallWatch _queueStall;
    private readonly PipelineRunQueue _runQueue;
    private readonly Dictionary<string, DateTimeOffset> _stallAlertsSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lagAlertsSent = new(StringComparer.OrdinalIgnoreCase);

    public PipelineSchedulerService(
        IOptions<AppOptions> options,
        SettingsStore settings,
        IPipelineService pipelines,
        IOrchestrationService orchestration,
        IJobService jobs,
        AlertService alerts,
        DeadmanService deadman,
        ISnapshotCatalogService snapshots,
        IAdlsClientFactory adlsFactory,
        ILogger<PipelineSchedulerService> logger,
        IActivityLog activity,
        OrphanedJobWatchdog watchdog,
        QueueStallWatch queueStall,
        PipelineRunQueue runQueue)
    {
        _watchdog = watchdog;
        _queueStall = queueStall;
        _runQueue = runQueue;
        _options = options.Value;
        _settings = settings;
        _pipelines = pipelines;
        _orchestration = orchestration;
        _jobs = jobs;
        _alerts = alerts;
        _deadman = deadman;
        _snapshots = snapshots;
        _adlsFactory = adlsFactory;
        _logger = logger;
        _activity = activity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Solo el nodo orquestador agenda. En distribuido los workers (con WORKER_STAGE)
        // únicamente procesan su cola; si todos agendaran, varias réplicas crearían jobs
        // duplicados del mismo pipeline (el chequeo ListRunning() es eventual entre nodos).
        if (!_options.IsOrchestrator)
        {
            _logger.LogInformation(
                "Scheduler deshabilitado en nodo worker (stage={Stage}); solo el orquestador agenda.",
                _options.WorkerStage);
            return;
        }

        // Se relee el intervalo en cada ciclo (desde SettingsStore) para que un cambio
        // guardado en /settings aplique sin reiniciar el contenedor.
        while (!stoppingToken.IsCancellationRequested)
        {
            var pollMinutes = Math.Max(1, _settings.Get().SchedulerPollMinutes);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(pollMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un fallo de un ciclo (job.json corrupto, IO transitorio sobre el volumen) no debe
                // tumbar el host: con BackgroundServiceExceptionBehavior=StopHost (el default) caería
                // el contenedor api. Se registra y se reintenta en el siguiente poll.
                _logger.LogError(ex, "El ciclo del scheduler falló; se reintenta en el próximo poll.");
            }
        }
    }

    // internal: los tests ejercitan un ciclo sin esperar al primer tick del timer.
    internal async Task PollAsync(CancellationToken ct)
    {
        await _deadman.PingAsync(ct);
        await CheckChangelogStallsAsync(ct);

        // Antes de agendar: un job huérfano en Running bloquearía el auto-run de su pipeline.
        try
        {
            var orphaned = await _watchdog.SweepAsync(DateTimeOffset.UtcNow, ct);
            if (orphaned.Count > 0)
            {
                _activity.LogSystem("job.orphaned", $"{orphaned.Count} job(s) sin tareas vivas marcado(s) como Error.", level: "warning");
            }

            foreach (var job in orphaned)
            {
                await _alerts.SendPipelineFailureAsync(new PipelineFailureAlert
                {
                    PipelineId = job.PipelineId,
                    PipelineName = _pipelines.Get(job.PipelineId)?.Name,
                    JobId = job.Id,
                    JobType = job.Type.ToString(),
                    Stage = job.CurrentStage?.ToString().ToLowerInvariant() ?? "unknown",
                    Message = $"Sin ninguna tarea pendiente ni en curso desde hace más de {OrphanedJobWatchdog.Grace.TotalMinutes:0} min (probable caída de un worker entre etapas): marcado como Error. Usa Reintentar para relanzarlo.",
                }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "El vigilante de jobs huérfanos falló; se reintenta en el próximo poll.");
        }

        // En distribuido cada etapa tiene sus workers: si una se queda sin ninguno vivo, sus tareas
        // esperan para siempre y el job sigue "en curso" sin que nada lo delate.
        if (_options.IsDistributed)
        {
            try
            {
                foreach (var stall in _queueStall.Check())
                {
                    var message = $"Cola parada: {stall.PendingTasks} tarea(s) de la etapa {stall.Stage} pendiente(s) desde hace más de {QueueStallWatch.Threshold.TotalMinutes:0} min sin ningún worker vivo de esa etapa. Comprueba el contenedor del worker {stall.Stage}.";
                    _activity.LogSystem("queue.stalled", message, level: "error");
                    await _alerts.SendAsync(message, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "La vigilancia de colas paradas falló; se reintenta en el próximo poll.");
            }
        }

        await StartQueuedRunsAsync(ct);

        foreach (var pipeline in _pipelines.List().Where(p => p.Enabled))
        {
            if (_jobs.ListRunning().Any(j => j.PipelineId == pipeline.Id))
            {
                continue;
            }

            try
            {
                var catalog = await _snapshots.GetCatalogAsync(pipeline.Id, ct);
                await CheckPipelineLagAsync(pipeline, catalog, ct);
                if (catalog.Blocked)
                {
                    _logger.LogDebug(
                        "Scheduler skip {PipelineId}: {Reason}",
                        pipeline.Id,
                        catalog.BlockReason);
                    continue;
                }

                if (catalog.PendingSnapshots.Count == 0)
                {
                    _logger.LogDebug("Scheduler skip {PipelineId}: no pending snapshots", pipeline.Id);
                    continue;
                }

                _logger.LogInformation(
                    "Scheduler auto-run for pipeline {PipelineId} ({Count} snapshot(s))",
                    pipeline.Id,
                    catalog.PendingSnapshots.Count);
                var job = await _orchestration.RunPipelineAsync(pipeline.Id, JobType.Incremental, ct);
                _activity.LogSystem("scheduler.run", $"Auto-run del scheduler en el pipeline {pipeline.Id} ({catalog.PendingSnapshots.Count} snapshot(s), job {job.Id}).", pipeline.Id, job.Id);
            }
            catch (PipelineBusyException ex)
            {
                // Un job recién cancelado aún detiene su stage: se reintenta en el próximo poll.
                _logger.LogInformation("Scheduler skip {PipelineId}: {Reason}", pipeline.Id, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduler failed for pipeline {PipelineId}", pipeline.Id);
                _activity.LogSystem("scheduler.error", $"El scheduler falló en el pipeline {pipeline.Id}: {ex.Message}", pipeline.Id, level: "error");
                await _alerts.SendAsync($"Scheduler failed for {pipeline.Id}: {ex.Message}", ct);
            }
        }
    }

    // Lo que se dejó en cola con el pipeline ocupado arranca en cuanto queda libre, antes que el
    // auto-run (que ya no lo lanza: el pipeline pasa a tener un job en curso). También en pipelines
    // con el scheduler desactivado, porque la ejecución manual siempre se permite, y aunque el job
    // anterior fallara o se cancelara. Se reserva antes de crear el job (ya no se puede quitar de la
    // cola) y la reserva se descarta después: si el proceso cae entre medias, la siguiente vuelta la
    // retoma, así que el peor caso es lanzarla dos veces, mejor que perder la petición.
    private async Task StartQueuedRunsAsync(CancellationToken ct)
    {
        foreach (var pipeline in _pipelines.List())
        {
            if (_runQueue.Get(pipeline.Id) is null
                || _jobs.ListRunning().Any(j => j.PipelineId == pipeline.Id)
                || _runQueue.TryClaim(pipeline.Id) is not { } queued)
            {
                continue;
            }

            try
            {
                var job = await _orchestration.RunPipelineAsync(pipeline.Id, queued.Type, ct);
                _runQueue.Complete(pipeline.Id);
                var requestedAt = queued.RequestedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                _jobs.AppendEvent(job.Id, EventLevel.Info, $"Lanzado desde la cola (pedido el {requestedAt} UTC).");
                _activity.LogSystem("pipeline.queue.started", $"Arranca {queued.Type} en cola del pipeline {pipeline.Id} (job {job.Id}).", pipeline.Id, job.Id);
            }
            catch (PipelineBusyException ex)
            {
                // Un job recién cancelado aún detiene su stage: vuelve a la cola para el próximo poll.
                _runQueue.Release(pipeline.Id);
                _logger.LogInformation("Queued run waits {PipelineId}: {Reason}", pipeline.Id, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Como un Run manual que falla al arrancar: se avisa y se quita, para no reintentarlo
                // (y alertar) en cada poll.
                _runQueue.Complete(pipeline.Id);
                var message = $"No se pudo lanzar {queued.Type} en cola del pipeline {pipeline.Id}: {ex.Message}";
                _logger.LogWarning(ex, "Queued run failed for pipeline {PipelineId}", pipeline.Id);
                _activity.LogSystem("pipeline.queue.failed", message, pipeline.Id, level: "error");
                await _alerts.SendAsync(message, ct);
            }
        }
    }

    // Pipeline atrasado: el lago tiene un snapshot cerrado sin procesar desde hace más de
    // PipelineLagAlertHours. Cubre lo que el aviso de changelog no ve: Synapse exporta bien, pero el
    // pipeline no avanza (jobs que fallan, el scheduler no consigue lanzarlos). Se mide desde que el
    // snapshot se cerró (el inicio de la carpeta siguiente), no desde su nombre, para no avisar solo
    // por el intervalo de carpetas. Usa el catálogo del ciclo, así que no se evalúa con un job en
    // curso, y no cuenta la carga inicial (nada procesado aún). Una alerta por episodio, en memoria
    // como la del changelog: se rearma al ponerse al día.
    private async Task CheckPipelineLagAsync(Models.Pipeline pipeline, SnapshotCatalogResult catalog, CancellationToken ct)
    {
        if (catalog.Blocked)
        {
            return;
        }

        var hours = _settings.Get().PipelineLagAlertHours;
        var now = DateTimeOffset.UtcNow;
        var closedAt = hours <= 0 || catalog.LastProcessedSnapshot is null ? null : OldestPendingClosedAt(catalog);
        if (closedAt is not { } since || (now - since).TotalHours < hours)
        {
            _lagAlertsSent.Remove(pipeline.Id);
            return;
        }

        if (!_lagAlertsSent.Add(pipeline.Id))
        {
            return;
        }

        var message = $"Pipeline {pipeline.Name} atrasado: {catalog.PendingSnapshots.Count} snapshot(s) cerrado(s) sin procesar; " +
            $"el más antiguo ({catalog.PendingSnapshots.Min(StringComparer.Ordinal)}) espera desde hace {(now - since).TotalHours:0} h (umbral {hours} h).";
        _activity.LogSystem("pipeline.lag", message, pipeline.Id, level: "warning");
        await _alerts.SendAsync(message, ct);
    }

    // Cuándo se cerró el snapshot pendiente más antiguo: cuando empezó la carpeta siguiente.
    private static DateTimeOffset? OldestPendingClosedAt(SnapshotCatalogResult catalog)
    {
        if (catalog.PendingSnapshots.Count == 0)
        {
            return null;
        }

        var folders = catalog.Snapshots.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var oldest = catalog.PendingSnapshots.Min(StringComparer.Ordinal)!;
        var next = folders.FirstOrDefault(f => string.CompareOrdinal(f, oldest) > 0);
        return next is not null && SnapshotFolderTime.TryParse(next, out var closedAt) ? closedAt : null;
    }

    private async Task CheckChangelogStallsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stallWindow = TimeSpan.FromHours(Math.Max(1, _options.ChangelogStallHours));
        var sourcesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pipeline in _pipelines.List().Where(p => p.Enabled))
        {
            if (!sourcesSeen.Add(pipeline.SourceId))
            {
                continue;
            }

            try
            {
                var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, ct);
                var lastModified = await adls.GetChangelogLastModifiedAsync(ct);
                if (!_snapshots.IsChangelogStalled(lastModified, now))
                {
                    _stallAlertsSent.Remove(pipeline.SourceId);
                    continue;
                }

                if (_stallAlertsSent.TryGetValue(pipeline.SourceId, out var sentAt)
                    && now - sentAt < stallWindow)
                {
                    continue;
                }

                _stallAlertsSent[pipeline.SourceId] = now;
                await _alerts.SendAsync(
                    $"Changelog stall detected for source '{pipeline.SourceId}' " +
                    $"(last modified {lastModified:u}, threshold {_options.ChangelogStallHours}h).",
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Changelog stall check failed for source {SourceId}", pipeline.SourceId);
            }
        }
    }
}