namespace Sluice.Portal.Configuration;

public sealed class AppOptions
{
    public string ExecutionMode { get; set; } = "local";
    public string DataDir { get; set; } = "data";

    // Azure (ADLS) y SQL se configuran POR CONEXIÓN en el portal (Sources/Destinations),
    // no por variables de entorno. Los parámetros operativos editables (poll del scheduler,
    // lease de tareas, % de divergencia de reconcile) viven en /settings (ver SettingsStore).
    public int HeartbeatMaxFailures { get; set; } = 3;
    // Default holgado sobre una cadencia real típica de exportación de Synapse Link (~12h,
    // dos volcados al día): con 4h (valor anterior) el banner de "changelog stalled" y la
    // alerta de background (PipelineSchedulerService) se disparaban como falso positivo durante
    // la mayor parte de cada ciclo, no solo ante un export genuinamente atascado. Ajustar por
    // env var CHANGELOG_STALL_HOURS si la cadencia real del pipeline es distinta.
    public int ChangelogStallHours { get; set; } = 16;
    public int SessionExpiryHours { get; set; } = 12;

    public string? UiApiKey { get; set; }

    public string? AlertWebhookUrl { get; set; }
    public string? DeadmanPingUrl { get; set; }

    public string? WorkerStage { get; set; }
    public string? WorkerId { get; set; }

    // Traducción de enums (equivalente a translate_enums del pipeline DVLinkToSQL): cuando está
    // activo, por cada columna optionset se añade al final de la tabla una columna [<col>_$label]
    // con el ExternalValue (idioma 1033, excluye sysdatastatecode), leyendo el optionset del
    // snapshot más reciente. Opt-in: off por defecto para no alterar el esquema sin pedirlo.
    public bool TranslateEnums { get; set; }

    // Raíz de las carpetas del origen simulado (tipo de conexión "local", solo pruebas UAT).
    // Sin valor, ese tipo no se ofrece en la UI y la factoría lo rechaza: nunca en producción.
    public string? LocalSourceRoot { get; set; }

    public bool IsLocalSourceEnabled => !string.IsNullOrWhiteSpace(LocalSourceRoot);

    // Modo demo (DEMO_MODE, docker-compose.demo.yml): genera datos sintéticos de F&O en
    // LOCAL_SOURCE_ROOT y crea la base, las conexiones y el pipeline contra el SQL Server de la
    // demo (DEMO_SQL_PASSWORD). Nunca en producción.
    public bool DemoMode { get; set; }
    public string? DemoSqlPassword { get; set; }
    public int DemoSnapshotMinutes { get; set; } = 5;

    // Proxies/redes de confianza para X-Forwarded-For/Proto (KNOWN_PROXIES, KNOWN_NETWORKS).
    // Vacías = se acepta de cualquier origen (comportamiento previo, se avisa al arrancar).
    public IReadOnlyList<System.Net.IPAddress> KnownProxies { get; set; } = [];
    public IReadOnlyList<System.Net.IPNetwork> KnownNetworks { get; set; } = [];

    public bool IsDistributed => string.Equals(ExecutionMode, "distributed", StringComparison.OrdinalIgnoreCase);

    // AUTH_MODE: "key" (por defecto, UI_API_KEY obligatoria fuera de Development) o "none" (acceso
    // abierto, sin clave: la landing ofrece "Entrar"). Solo un AUTH_MODE=none explícito permite
    // arrancar sin clave fuera de Development; ver Program.cs.
    public const string KeyAuthMode = "key";
    public const string OpenAuthMode = "none";

    public string AuthMode { get; set; } = KeyAuthMode;

    public bool IsOpenAccess => string.Equals(AuthMode, OpenAuthMode, StringComparison.OrdinalIgnoreCase);

    public bool IsUiAuthEnabled => !IsOpenAccess && !string.IsNullOrWhiteSpace(UiApiKey);

    // El nodo orquestador agenda y encola tareas (PipelineSchedulerService). En
    // monolito (local) el único contenedor agenda y procesa. En distribuido lo hace
    // el 'api' (sin WORKER_STAGE); los workers (con stage) solo procesan su cola y NO
    // agendan, para que varias réplicas no creen jobs duplicados del mismo pipeline.
    // Es la simetría inversa de WorkerHostService, que se deshabilita en el 'api'.
    public bool IsOrchestrator => !IsDistributed || string.IsNullOrWhiteSpace(WorkerStage);
}