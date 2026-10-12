using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Scheduler;

// Janitor de snapshots Synapse Link en ADLS origen. Solo actúa en el orquestador.
public sealed class SnapshotRetentionSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly AppOptions _options;
    private readonly SettingsStore _settings;
    private readonly SnapshotRetentionService _retention;
    private readonly AlertService _alerts;
    private readonly ILogger<SnapshotRetentionSweepService> _logger;
    private readonly IActivityLog _activity;

    public SnapshotRetentionSweepService(
        IOptions<AppOptions> options,
        SettingsStore settings,
        SnapshotRetentionService retention,
        AlertService alerts,
        ILogger<SnapshotRetentionSweepService> logger,
        IActivityLog activity)
    {
        _options = options.Value;
        _settings = settings;
        _retention = retention;
        _alerts = alerts;
        _logger = logger;
        _activity = activity;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsOrchestrator)
        {
            _logger.LogInformation(
                "Retención de snapshots deshabilitada en nodo worker (stage={Stage}); solo el orquestador purga.",
                _options.WorkerStage);
            return;
        }

        await SweepAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await SweepAsync(stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _retention.SweepAsync(DateTimeOffset.UtcNow, _settings.Get(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Snapshot retention sweep failed");
            _activity.LogSystem(
                "snapshot-retention.error",
                $"El barrido de retención de snapshots falló: {ex.Message}",
                level: "error");
            await _alerts.SendAsync($"Snapshot retention sweep failed: {ex.Message}", cancellationToken);
        }
    }
}