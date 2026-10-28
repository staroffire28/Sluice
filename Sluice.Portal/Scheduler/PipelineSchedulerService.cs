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
    private readonly RunQueueService _runQueue;
    private readonly PipelineStateStore _stateStore;
    private readonly Dictionary<string, DateTimeOffset> _stallAlertsSent = new(StringComparer.OrdinalIgnoreCase);
    // Aviso de snapshot incompleto ya registrado en el activity log (pipeline → ronda de aviso), para
    // no repetirlo en cada poll mientras el webhook no lo acepte.
    private readonly Dictionary<string, string> _incompleteLogged = new(StringComparer.OrdinalIgnoreCase);
    // Lo mismo para el aviso de bloqueo por changelog (pipeline → ronda de aviso).
    private readonly Dictionary<string, string> _changelogBlockLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lagAlertsNoticed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lagAlertsSent = new(StringComparer.OrdinalIgnoreCase);
    // Restore a medias: aviso registrado y alerta entregada, una vez por episodio. Se rearman en cuanto
    // el estado del pipeline ya no tiene el restore a medias.
    private readonly HashSet<string> _restoreHoldNoticed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _restoreHoldAlerted = new(StringComparer.OrdinalIgnoreCase);
    // Destino marcado por la migración (faltan sus campos): igual, una vez por episodio. Se rearman en
    // cuanto el pipeline vuelve a arrancar.
    private readonly HashSet<string> _fieldsRequiredNoticed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fieldsRequiredAlerted = new(StringComparer.OrdinalIgnoreCase);
    private bool _alertDeliveryFailedThisPoll;

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
        RunQueueService runQueue,
        PipelineStateStore stateStore)
    {
        _stateStore = stateStore;
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
        _alertDeliveryFailedThisPoll = false;
        // Un vigilante del ciclo que falla: el poll sigue, pero no hace ping al dead-man.
        var degraded = false;
        await CheckChangelogStallsAsync(ct);

        // Antes de agendar: un job huérfano en Running bloquearía el auto-run de su pipeline.
        try
        {
            var orphaned = await _watchdog.SweepAsync(DateTimeOffset.UtcNow, ct);
            if (orphaned.Count > 0)
            {
                _activity.LogSystem("job.orphaned", $"{orphaned.Count} job(s) sin tareas vivas marcado(s) como Error.", null, null, "warning",
                    "Activity.Job.Orphaned", orphaned.Count);
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
            degraded = true;
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
                    if (stall.FirstNotice)
                    {
                        _activity.LogSystem("queue.stalled", message, null, null, "error",
                            "Activity.Queue.Stalled", stall.PendingTasks, stall.Stage.ToString(), QueueStallWatch.Threshold.TotalMinutes);
                    }

                    if (await TrySendPendingAlertAsync(message, ct))
                    {
                        _queueStall.MarkReported(stall.Stage);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                degraded = true;
                _logger.LogError(ex, "La vigilancia de colas paradas falló; se reintenta en el próximo poll.");
            }
        }

        await StartQueuedRunsAsync(ct);

        var evaluated = 0;
        var failed = 0;
        foreach (var pipeline in _pipelines.List().Where(p => p.Enabled))
        {
            if (_jobs.ListRunning().Any(j => j.PipelineId == pipeline.Id))
            {
                // Con un job en curso no hay restore a medias (no arranca nada mientras haya uno
                // pendiente): el episodio terminó.
                _restoreHoldNoticed.Remove(pipeline.Id);
                _restoreHoldAlerted.Remove(pipeline.Id);
                _fieldsRequiredNoticed.Remove(pipeline.Id);
                _fieldsRequiredAlerted.Remove(pipeline.Id);
                continue;
            }

            evaluated++;
            try
            {
                // Dentro del try: un pipeline_state.json ilegible cuenta como fallo de este pipeline,
                // no tumba el poll de los demás.
                var state = _stateStore.GetOrCreate(pipeline.Id);
                if (state.RestoreInProgress is null)
                {
                    _restoreHoldNoticed.Remove(pipeline.Id);
                    _restoreHoldAlerted.Remove(pipeline.Id);
                }

                var catalog = await _snapshots.GetCatalogAsync(pipeline.Id, ct);
                try
                {
                    if (catalog.Blocked)
                    {
                        _logger.LogDebug(
                            "Scheduler skip {PipelineId}: {Reason}",
                            pipeline.Id,
                            catalog.BlockReason);
                        await HoldForChangelogBlockAsync(pipeline, catalog, state, ct);
                        continue;
                    }

                    if (state.ChangelogBlock is not null)
                    {
                        _stateStore.Update(pipeline.Id, s => s with { ChangelogBlock = null });
                    }

                    if (catalog.PendingSnapshots.Count == 0)
                    {
                        _logger.LogDebug("Scheduler skip {PipelineId}: no pending snapshots", pipeline.Id);
                        continue;
                    }

                    if (await HoldForIncompleteSnapshotAsync(pipeline, catalog, ct))
                    {
                        continue;
                    }

                    _logger.LogInformation(
                        "Scheduler auto-run for pipeline {PipelineId} ({Count} snapshot(s))",
                        pipeline.Id,
                        catalog.PendingSnapshots.Count);
                    await _orchestration.RunPipelineAsync(AuditActor.Scheduler, pipeline.Id, JobType.Incremental, ct);
                    _fieldsRequiredNoticed.Remove(pipeline.Id);
                    _fieldsRequiredAlerted.Remove(pipeline.Id);
                }
                finally
                {
                    await CheckPipelineLagAsync(pipeline, catalog, ct);
                }
            }
            catch (PipelineBusyException ex)
            {
                // Un job recién cancelado aún detiene su stage: se reintenta en el próximo poll.
                _logger.LogInformation("Scheduler skip {PipelineId}: {Reason}", pipeline.Id, ex.Message);
            }
            catch (RestorePendingException ex)
            {
                // Retención, no fallo: hasta que alguien termine el restore no arranca nada. Se avisa una
                // vez por episodio y la alerta cuenta como dada solo si llega al webhook.
                _logger.LogInformation("Scheduler hold {PipelineId}: {Reason}", pipeline.Id, ex.Message);
                var message = $"Pipeline {pipeline.Name} parado: {ex.Message}";
                if (_restoreHoldNoticed.Add(pipeline.Id))
                {
                    _activity.LogSystem("restore.pending", message, pipeline.Id, null, "warning", "Activity.Restore.Pending", pipeline.Name, ex.Message);
                }

                if (!_restoreHoldAlerted.Contains(pipeline.Id) && await TrySendPendingAlertAsync(message, ct))
                {
                    _restoreHoldAlerted.Add(pipeline.Id);
                }
            }
            catch (ConnectionFieldsRequiredException ex)
            {
                // Retención, no fallo: es lo esperado tras la migración hasta que alguien escriba los
                // campos del destino. Igual que el restore a medias, una vez por episodio.
                _logger.LogInformation("Scheduler hold {PipelineId}: {Reason}", pipeline.Id, ex.Message);
                var message = $"Pipeline {pipeline.Name} parado: {ex.Message}";
                if (_fieldsRequiredNoticed.Add(pipeline.Id))
                {
                    _activity.LogSystem("connection.fields-required", message, pipeline.Id, null, "warning",
                        "Activity.Connection.FieldsRequiredHold", pipeline.Name, ex.ConnectionName);
                }

                if (!_fieldsRequiredAlerted.Contains(pipeline.Id) && await TrySendPendingAlertAsync(message, ct))
                {
                    _fieldsRequiredAlerted.Add(pipeline.Id);
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Scheduler failed for pipeline {PipelineId}", pipeline.Id);
                _activity.LogSystem("scheduler.error", $"El scheduler falló en el pipeline {pipeline.Id}: {ex.Message}", pipeline.Id, null, "error",
                    "Activity.Scheduler.Error", pipeline.Id, ex.Message);
                await _alerts.SendAsync($"Scheduler failed for {pipeline.Id}: {ex.Message}", ct);
            }
        }

        // Al final, y solo si el ciclo terminó sin excepción ni fallos de sus vigilantes, y no fallaron
        // todos los pipelines evaluados (el fallo de alguno suelto ya alerta por su cuenta): un scheduler
        // que falla en cada poll deja de hacer ping y el monitor externo lo da por caído.
        if (!degraded && (evaluated == 0 || failed < evaluated))
        {
            await _deadman.PingAsync(ct);
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

            Job job;
            try
            {
                job = await _orchestration.StartQueuedAsync(pipeline.Id, queued, ct);
                _runQueue.Complete(pipeline.Id);
                _restoreHoldNoticed.Remove(pipeline.Id);
                _restoreHoldAlerted.Remove(pipeline.Id);
            }
            catch (PipelineBusyException ex)
            {
                // Un job recién cancelado aún detiene su stage: vuelve a la cola para el próximo poll.
                _runQueue.Release(pipeline.Id);
                _logger.LogInformation("Queued run waits {PipelineId}: {Reason}", pipeline.Id, ex.Message);
                continue;
            }
            catch (RestorePendingException ex)
            {
                // Restore a medias: espera en la cola hasta que se termine, sin alertar. Un solo aviso
                // por episodio, compartido con el del auto-run.
                _runQueue.Release(pipeline.Id);
                _logger.LogInformation("Queued run waits {PipelineId}: {Reason}", pipeline.Id, ex.Message);
                if (_restoreHoldNoticed.Add(pipeline.Id))
                {
                    _activity.LogSystem("restore.pending", $"Pipeline {pipeline.Name} parado: {ex.Message}", pipeline.Id, null, "warning",
                        "Activity.Restore.Pending", pipeline.Name, ex.Message);
                }

                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Como un Run manual que falla al arrancar: se avisa y se quita, para no reintentarlo
                // (y alertar) en cada poll.
                _runQueue.Complete(pipeline.Id);
                var message = $"No se pudo lanzar {queued.Type} en cola del pipeline {pipeline.Id}: {ex.Message}";
                _logger.LogWarning(ex, "Queued run failed for pipeline {PipelineId}", pipeline.Id);
                await _alerts.SendAsync(message, ct);
                continue;
            }

            // Fuera del try de arriba: el job ya arrancó, así que un fallo al anotarlo no es un
            // "no se pudo lanzar".
            try
            {
                var requestedAt = queued.RequestedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                _jobs.AppendEvent(job.Id, EventLevel.Info, $"Lanzado desde la cola (pedido el {requestedAt} UTC).", null, "Event.Job.LaunchedFromQueue", requestedAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Queued run {JobId} started, but its queue event could not be saved", job.Id);
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
            _lagAlertsNoticed.Remove(pipeline.Id);
            _lagAlertsSent.Remove(pipeline.Id);
            return;
        }

        if (_lagAlertsSent.Contains(pipeline.Id))
        {
            return;
        }

        // Se registra una vez por episodio; la alerta cuenta como dada solo si llega al webhook,
        // así que con el webhook caído se reintenta en cada poll.
        var oldestPending = catalog.PendingSnapshots.Min(StringComparer.Ordinal);
        var message = $"Pipeline {pipeline.Name} atrasado: {catalog.PendingSnapshots.Count} snapshot(s) cerrado(s) sin procesar; " +
            $"el más antiguo ({oldestPending}) espera desde hace {(now - since).TotalHours:0} h (umbral {hours} h).";
        if (_lagAlertsNoticed.Add(pipeline.Id))
        {
            _activity.LogSystem("pipeline.lag", message, pipeline.Id, null, "warning",
                "Activity.Pipeline.Lag", pipeline.Name, catalog.PendingSnapshots.Count, oldestPending, (now - since).TotalHours, hours);
        }

        if (await TrySendPendingAlertAsync(message, ct))
        {
            _lagAlertsSent.Add(pipeline.Id);
        }
    }

    // Alertas deduplicadas que se reintentan en cada poll hasta llegar: tras el primer fallo de entrega
    // del poll las demás esperan al siguiente, para no bloquear el ciclo con un webhook caído.
    private Task<bool> TrySendPendingAlertAsync(string message, CancellationToken ct) =>
        TrySendPendingAlertAsync(() => _alerts.SendAsync(message, ct));

    private async Task<bool> TrySendPendingAlertAsync(Func<Task<AlertDeliveryResult>> send)
    {
        if (_alertDeliveryFailedThisPoll)
        {
            return false;
        }

        var sent = (await send()).Success;
        _alertDeliveryFailedThisPoll = !sent;
        return sent;
    }

    public static readonly TimeSpan ChangelogBlockGrace = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan ChangelogBlockRepeat = TimeSpan.FromHours(24);

    // Pipeline bloqueado por el changelog (vacío, o con una carpeta que no está): mientras siga así no
    // se procesa nada, y antes solo quedaba un LogDebug. Como el snapshot incompleto: desde cuándo y la
    // última alerta van en el estado del pipeline; tras la gracia se avisa como warning, y de nuevo cada
    // 24 h. Al desbloquearse, el ciclo borra el episodio.
    private async Task HoldForChangelogBlockAsync(Models.Pipeline pipeline, SnapshotCatalogResult catalog, PipelineState state, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var current = state.ChangelogBlock;
        if (current is null)
        {
            current = new ChangelogBlock { FirstSeenAt = now };
            _stateStore.Update(pipeline.Id, s => s with { ChangelogBlock = current });
        }

        var waiting = now - current.FirstSeenAt;
        var due = waiting >= ChangelogBlockGrace
            && (current.LastAlertedAt is not { } last || now - last >= ChangelogBlockRepeat);
        if (!due)
        {
            return;
        }

        var waitText = FormatWait(waiting);
        var message = $"Pipeline {pipeline.Name} parado: el changelog de Synapse Link lo bloquea desde hace {waitText} ({catalog.BlockReason}). " +
            "No se procesa nada hasta que el changelog nombre una carpeta existente; revisa la exportación.";
        var round = $"{current.FirstSeenAt:O}|{current.LastAlertedAt:O}";
        if (_changelogBlockLogged.GetValueOrDefault(pipeline.Id) != round)
        {
            _changelogBlockLogged[pipeline.Id] = round;
            _activity.LogSystem("changelog.blocked", message, pipeline.Id, null, "warning",
                "Activity.Changelog.Blocked", pipeline.Name, waitText, catalog.BlockReason);
        }

        if (await TrySendPendingAlertAsync(message, ct))
        {
            _stateStore.Update(pipeline.Id, s => s.ChangelogBlock is { } block
                ? s with { ChangelogBlock = block with { LastAlertedAt = now } }
                : s);
        }
    }

    public static readonly TimeSpan IncompleteSnapshotGrace = TimeSpan.FromMinutes(45);
    public static readonly TimeSpan IncompleteSnapshotRepeat = TimeSpan.FromHours(24);

    // Snapshot cerrado pero incompleto (el volcado de Synapse no terminó): el ingest se pararía en él y
    // el job cerraría Ok sin avanzar, en cada poll. Se valida aquí el pendiente más antiguo (solo
    // metadatos, más barato que el job) y, mientras siga incompleto, no se lanza nada. Tras la gracia
    // se avisa como warning, y de nuevo cada 24 h; desde cuándo y la última alerta quedan en el estado
    // del pipeline, así que sobreviven a un reinicio. Otro snapshot, o uno ya completo, lo rearma.
    private async Task<bool> HoldForIncompleteSnapshotAsync(Models.Pipeline pipeline, SnapshotCatalogResult catalog, CancellationToken ct)
    {
        var oldest = catalog.PendingSnapshots.Min(StringComparer.Ordinal)!;
        var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, ct);
        var integrity = await adls.ValidateSnapshotAsync(oldest, ct);
        var state = _stateStore.GetOrCreate(pipeline.Id);
        if (integrity.IsComplete)
        {
            if (state.IncompleteSnapshot is not null)
            {
                _stateStore.Update(pipeline.Id, s => s with { IncompleteSnapshot = null });
            }

            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var current = state.IncompleteSnapshot?.Name == oldest
            ? state.IncompleteSnapshot
            : new IncompleteSnapshot { Name = oldest, FirstSeenAt = now };
        if (current != state.IncompleteSnapshot)
        {
            _stateStore.Update(pipeline.Id, s => s with { IncompleteSnapshot = current });
        }

        _logger.LogInformation("Scheduler hold {PipelineId}: snapshot {Snapshot} incompleto ({Reason})", pipeline.Id, oldest, integrity.Reason);

        var waiting = now - current.FirstSeenAt;
        var due = waiting >= IncompleteSnapshotGrace
            && (current.LastAlertedAt is not { } last || now - last >= IncompleteSnapshotRepeat);
        if (due)
        {
            var waitText = FormatWait(waiting);
            var message = $"Pipeline {pipeline.Name} parado: el snapshot {oldest} está cerrado pero incompleto desde hace {waitText} ({integrity.Reason}). " +
                "No se procesa nada más hasta que el volcado de Synapse Link se complete; revisa la exportación.";
            var round = $"{oldest}|{current.LastAlertedAt:O}";
            if (_incompleteLogged.GetValueOrDefault(pipeline.Id) != round)
            {
                _incompleteLogged[pipeline.Id] = round;
                _activity.LogSystem("snapshot.incomplete", message, pipeline.Id, null, "warning",
                    "Activity.Snapshot.Incomplete", pipeline.Name, oldest, waitText, integrity.Reason);
            }

            if (await TrySendPendingAlertAsync(() => _alerts.SendSnapshotIncompleteAsync(pipeline.Id, pipeline.Name, oldest, message, ct)))
            {
                _stateStore.Update(pipeline.Id, s => s.IncompleteSnapshot?.Name == oldest
                    ? s with { IncompleteSnapshot = s.IncompleteSnapshot with { LastAlertedAt = now } }
                    : s);
            }
        }

        return true;
    }

    private static string FormatWait(TimeSpan wait) =>
        wait.TotalHours >= 1 ? $"{wait.TotalHours:0} h" : $"{wait.TotalMinutes:0} min";

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

                var sent = await TrySendPendingAlertAsync(
                    $"Changelog stall detected for source '{pipeline.SourceId}' " +
                    $"(last modified {lastModified:u}, threshold {_options.ChangelogStallHours}h).",
                    ct);
                if (sent)
                {
                    _stallAlertsSent[pipeline.SourceId] = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Changelog stall check failed for source {SourceId}", pipeline.SourceId);
            }
        }
    }
}