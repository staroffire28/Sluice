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
    private readonly Dictionary<string, DateTimeOffset> _stallAlertsSent = new(StringComparer.OrdinalIgnoreCase);

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
        OrphanedJobWatchdog watchdog)
    {
        _watchdog = watchdog;
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
            if (orphaned > 0)
            {
                _activity.LogSystem("job.orphaned", $"{orphaned} job(s) sin tareas vivas marcado(s) como Error.", level: "warning");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "El vigilante de jobs huérfanos falló; se reintenta en el próximo poll.");
        }

        foreach (var pipeline in _pipelines.List().Where(p => p.Enabled))
        {
            if (_jobs.ListRunning().Any(j => j.PipelineId == pipeline.Id))
            {
                continue;
            }

            try
            {
                var catalog = await _snapshots.GetCatalogAsync(pipeline.Id, ct);
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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduler failed for pipeline {PipelineId}", pipeline.Id);
                _activity.LogSystem("scheduler.error", $"El scheduler falló en el pipeline {pipeline.Id}: {ex.Message}", pipeline.Id, level: "error");
                await _alerts.SendAsync($"Scheduler failed for {pipeline.Id}: {ex.Message}", ct);
            }
        }
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