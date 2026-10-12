namespace Sluice.Portal.Configuration;

using Microsoft.Extensions.Configuration;
using Sluice.Portal.Models;

public static class AppOptionsConfiguration
{
    public static AppOptions LoadFromEnvironment() => Load(null);

    public static AppOptions Load(IConfiguration? configuration)
    {
        var workerStage = Get(configuration, "WORKER_STAGE");
        // Un nombre mal escrito hacía que el worker procesara las tres colas sin avisar (y,
        // al no ser orquestador, nadie agendaba): se exige el nombre exacto de un stage.
        if (!string.IsNullOrWhiteSpace(workerStage)
            && !Enum.GetNames<PipelineStage>().Contains(workerStage.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"WORKER_STAGE '{workerStage}' no es válido. Valores admitidos: {string.Join(", ", Enum.GetNames<PipelineStage>())}.");
        }

        // Solo "key" o "none": un valor mal escrito ("abierto", "off") no debe dejar el portal
        // abierto ni cerrado sin que nadie lo note.
        var authMode = Get(configuration, "AUTH_MODE")?.Trim() is { Length: > 0 } rawAuthMode
            ? rawAuthMode.ToLowerInvariant()
            : AppOptions.KeyAuthMode;
        if (authMode is not (AppOptions.KeyAuthMode or AppOptions.OpenAuthMode))
        {
            throw new InvalidOperationException(
                $"AUTH_MODE '{authMode}' no es válido. Valores admitidos: {AppOptions.KeyAuthMode}, {AppOptions.OpenAuthMode}.");
        }

        return new AppOptions
        {
            AuthMode = authMode,
            ExecutionMode = Get(configuration, "EXECUTION_MODE") ?? "local",
            DataDir = Get(configuration, "DATA_DIR") ?? Path.Combine(Directory.GetCurrentDirectory(), "data"),
            HeartbeatMaxFailures = GetInt(configuration, "HEARTBEAT_MAX_FAILURES", 3),
            // Ver comentario en AppOptions.cs: 16h da margen sobre una cadencia real de ~12h.
            ChangelogStallHours = GetInt(configuration, "CHANGELOG_STALL_HOURS", 16),
            SessionExpiryHours = GetInt(configuration, "SESSION_EXPIRY_HOURS", 12),
            UiApiKey = Get(configuration, "UI_API_KEY"),
            AlertWebhookUrl = Get(configuration, "ALERT_WEBHOOK_URL"),
            DeadmanPingUrl = Get(configuration, "DEADMAN_PING_URL"),
            WorkerStage = workerStage,
            WorkerId = Get(configuration, "WORKER_ID"),
            TranslateEnums = GetBool(configuration, "TRANSLATE_ENUMS", false),
            LocalSourceRoot = Get(configuration, "LOCAL_SOURCE_ROOT"),
            KnownProxies = SplitList(configuration, "KNOWN_PROXIES")
                .Select(item => System.Net.IPAddress.TryParse(item, out var address) ? address : throw Invalid("KNOWN_PROXIES", item))
                .ToList(),
            KnownNetworks = SplitList(configuration, "KNOWN_NETWORKS")
                .Select(item => System.Net.IPNetwork.TryParse(item, out var network) ? network : throw Invalid("KNOWN_NETWORKS", item))
                .ToList(),
        };
    }

    // Lista separada por comas. Un elemento inválido hace fallar el arranque: mejor que
    // ignorarlo y acabar confiando en cualquier proxy sin saberlo.
    private static IEnumerable<string> SplitList(IConfiguration? configuration, string name) =>
        Get(configuration, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static InvalidOperationException Invalid(string name, string item) =>
        new($"{name}: '{item}' no es un valor válido.");

    private static string? Get(IConfiguration? configuration, string name) =>
        configuration?[name] ?? Environment.GetEnvironmentVariable(name);

    private static int GetInt(IConfiguration? configuration, string name, int defaultValue) =>
        int.TryParse(Get(configuration, name), out var value) ? value : defaultValue;

    // Acepta "true"/"false" (case-insensitive) y "1"/"0", como suelen venir las env vars de Docker.
    private static bool GetBool(IConfiguration? configuration, string name, bool defaultValue)
    {
        var raw = Get(configuration, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim() == "1" || bool.TryParse(raw, out var value) && value;
    }
}