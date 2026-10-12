using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Scheduler;

// Janitor de retención: barre periódicamente jobs antiguos y sus artefactos derivados.
// Solo actúa en el nodo orquestador (igual que el scheduler): en distribuido los
// workers no deben purgar el volumen compartido.
public sealed class RetentionSweepService : BackgroundService
{
    // Cadencia fija: la retención no necesita precisión, basta con acotar el crecimiento.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly AppOptions _options;
    private readonly SettingsStore _settings;
    private readonly RetentionService _retention;
    private readonly AlertService _alerts;
    private readonly ILogger<RetentionSweepService> _logger;
    private readonly IActivityLog _activity;

    public RetentionSweepService(
        IOptions<AppOptions> options,
        SettingsStore settings,
        RetentionService retention,
        AlertService alerts,
        ILogger<RetentionSweepService> logger,
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
                "Retención deshabilitada en nodo worker (stage={Stage}); solo el orquestador purga.",
                _options.WorkerStage);
            return;
        }

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

            await SweepAsync();
        }
    }

    // internal: los tests ejercitan un ciclo sin esperar al primer tick del timer.
    internal Task SweepAsync()
    {
        try
        {
            // El barrido lee settings frescos: un cambio de RetentionDays en /settings
            // aplica en el siguiente ciclo sin reiniciar el contenedor. Si settings.json
            // existe pero no se puede leer, se salta el ciclo: con los valores por defecto
            // (90 días) se podría purgar más de lo configurado.
            if (!_settings.TryGet(out var settings))
            {
                _logger.LogWarning("Retention sweep skipped: settings.json could not be read");
                return Task.CompletedTask;
            }

            _retention.Sweep(DateTimeOffset.UtcNow, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retention sweep failed");
            _activity.LogSystem("retention.error", $"El barrido de retención falló: {ex.Message}", level: "error");
            return _alerts.SendAsync($"Retention sweep failed: {ex.Message}", CancellationToken.None);
        }

        return Task.CompletedTask;
    }
}
