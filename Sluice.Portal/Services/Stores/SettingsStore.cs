using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services.Stores;

// Ajustes operativos globales, editables desde el portal (página /settings) en vez
// de variables de entorno. Se persisten como un único documento JSON en DATA_DIR.
public sealed record AppSettings
{
    public int SchedulerPollMinutes { get; init; } = 5;
    public int TaskLeaseSeconds { get; init; } = 120;
    public double ReconDivergencePct { get; init; } = 1.0;

    // Corrección automática (opt-in): al terminar una corrida con tablas que superan el umbral (o
    // con diferencias de valores), se lanza UN re-sync en modo recarga de esas tablas. El propio
    // re-sync no vuelve a disparar otro (un intento por corrida). Clave ausente = false.
    public bool AutoRemediate { get; init; }

    // Antigüedad (días) más allá de la cual el janitor de retención purga jobs y sus
    // artefactos derivados. 0 o negativo desactiva la purga. El piso de seguridad
    // (últimos K por pipeline) es constante en RetentionService.
    public int RetentionDays { get; init; } = 90;

    // Antigüedad (días) de carpetas snapshot CSV ya procesadas en el container origen
    // de Synapse Link. 0 o negativo desactiva la purga. El piso K es constante en
    // SnapshotRetentionService.
    public int SourceSnapshotRetentionDays { get; init; } = 30;

    // Simulación: registra elegibles sin borrar (snapshot-retention.dry-run en activity log).
    public bool SourceSnapshotRetentionDryRun { get; init; } = true;

    // No purga snapshots origen si falta algún parquet consolidado del pipeline.
    public bool SourceSnapshotRetentionRequireParquet { get; init; } = true;

    // Webhook HTTP (p. ej. Azure Logic App) para alertas de fallo de pipeline.
    public string? AlertWebhookUrl { get; init; }

    // Opt-in: sin esto activo, no se usa AlertWebhookUrl de settings (sí puede usarse ALERT_WEBHOOK_URL por env).
    public bool AlertEnabled { get; init; }
}

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private AppSettings? _cache;
    private DateTime _cachedWriteTimeUtc;

    public SettingsStore(IOptions<AppOptions> options)
    {
        var dataDir = options.Value.DataDir;
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "settings.json");
    }

    // Caché invalidada por fecha de modificación: evita reabrir/deserializar el archivo
    // en cada llamada (hot path de TaskStore/scheduler), pero sigue captando cambios
    // guardados desde la UI o por otra réplica (vía mtime del volumen compartido).
    // Si el archivo no existe o está corrupto, devuelve los valores por defecto.
    public AppSettings Get() => TryGet(out var settings) ? settings : new AppSettings();

    // Como Get, pero devuelve false si settings.json existe y no se puede leer: quien borra
    // datos según los settings (retención) no debe actuar con los valores por defecto.
    public bool TryGet(out AppSettings settings)
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                settings = new AppSettings();
                return true;
            }

            var writeTime = File.GetLastWriteTimeUtc(_path);
            if (_cache is not null && writeTime == _cachedWriteTimeUtc)
            {
                settings = _cache;
                return true;
            }

            try
            {
                var json = File.ReadAllText(_path);
                using var doc = JsonDocument.Parse(json);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
                loaded = ApplyMissingDefaults(loaded, doc.RootElement);
                _cache = loaded;
                _cachedWriteTimeUtc = writeTime;
                settings = loaded;
                return true;
            }
            catch
            {
                settings = new AppSettings();
                return false;
            }
        }
    }

    // System.Text.Json no aplica los inicializadores del record cuando falta la clave en
    // settings.json (bool → false, int → 0). Solo rellenamos campos de retención origen
    // ausentes; un valor explícito en el JSON (p. ej. dry-run desactivado) se respeta.
    private static AppSettings ApplyMissingDefaults(AppSettings settings, JsonElement root)
    {
        var defaults = new AppSettings();
        return settings with
        {
            SourceSnapshotRetentionDays = root.TryGetProperty("sourceSnapshotRetentionDays", out _)
                ? settings.SourceSnapshotRetentionDays
                : defaults.SourceSnapshotRetentionDays,
            SourceSnapshotRetentionDryRun = root.TryGetProperty("sourceSnapshotRetentionDryRun", out _)
                ? settings.SourceSnapshotRetentionDryRun
                : defaults.SourceSnapshotRetentionDryRun,
            SourceSnapshotRetentionRequireParquet = root.TryGetProperty("sourceSnapshotRetentionRequireParquet", out _)
                ? settings.SourceSnapshotRetentionRequireParquet
                : defaults.SourceSnapshotRetentionRequireParquet,
            AlertEnabled = root.TryGetProperty("alertEnabled", out _)
                ? settings.AlertEnabled
                : defaults.AlertEnabled,
        };
    }

    // Con un lease menor el heartbeat (un tercio del lease, mínimo 5 s) apenas cabe: las tareas en
    // curso se darían por vencidas y otra réplica las reclamaría.
    public const int MinTaskLeaseSeconds = 30;

    // Devuelve la clave de recurso (SharedResource) del primer valor fuera de rango, o null si
    // todo es válido. La UI la muestra localizada; Save la usa para rechazar el guardado.
    public static string? Validate(AppSettings settings) => settings switch
    {
        { SchedulerPollMinutes: < 1 } => "Page.Settings.Validation.PollMinutes",
        { TaskLeaseSeconds: < MinTaskLeaseSeconds } => "Page.Settings.Validation.LeaseSeconds",
        { ReconDivergencePct: < 0 or > 100 } => "Page.Settings.Validation.Divergence",
        { AlertWebhookUrl: { } url } when !IsHttpsUrl(url) => "Page.Settings.Validation.WebhookHttps",
        _ => null,
    };

    public static bool IsHttpsUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    public void Save(AppSettings settings)
    {
        if (Validate(settings) is { } error)
        {
            throw new InvalidOperationException($"Ajustes no válidos ({error}).");
        }

        lock (_lock)
        {
            AtomicJsonFile.Write(_path, settings);
            _cache = settings;
            _cachedWriteTimeUtc = File.GetLastWriteTimeUtc(_path);
        }
    }
}
