using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using ApexCharts;
using Microsoft.FluentUI.AspNetCore.Components;
using Sluice.Portal;
using Sluice.Portal.Components;
using Sluice.Portal.Configuration;
using Sluice.Portal.Endpoints;
using Sluice.Portal.Middleware;
using Sluice.Portal.Scheduler;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Analytics;
using Sluice.Portal.Services.Auth;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Reporting;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Workers;

JobReportPdfService.ConfigureQuestPdf();

var builder = WebApplication.CreateBuilder(args);

var appOptions = AppOptionsConfiguration.Load(builder.Configuration);
builder.Services.AddSingleton<IOptions<AppOptions>>(new OptionsWrapper<AppOptions>(appOptions));

// X-Forwarded-* solo se acepta de proxies de confianza: con KNOWN_PROXIES/KNOWN_NETWORKS, de esos;
// sin ellos, solo de loopback (el valor por defecto de ASP.NET). Antes, sin configurarlos, se aceptaba
// de cualquiera y una IP falsa por intento esquivaba el límite de intentos de login.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (appOptions.KnownProxies.Count == 0 && appOptions.KnownNetworks.Count == 0)
    {
        return;
    }

    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var proxy in appOptions.KnownProxies)
    {
        options.KnownProxies.Add(proxy);
    }

    foreach (var network in appOptions.KnownNetworks)
    {
        options.KnownIPNetworks.Add(network);
    }
});

// Una clave junto a AUTH_MODE=none es contradictorio: no se adivina cuál de las dos se quería.
if (appOptions.IsOpenAccess && !string.IsNullOrWhiteSpace(appOptions.UiApiKey))
{
    throw new InvalidOperationException(
        "AUTH_MODE=none y UI_API_KEY no pueden ir juntos: quita la clave para el acceso abierto o AUTH_MODE para exigirla.");
}

// El modo demo escribe sus datos sintéticos en LOCAL_SOURCE_ROOT y crea su base en el SQL de la demo.
if (appOptions.DemoMode && (!appOptions.IsLocalSourceEnabled || string.IsNullOrWhiteSpace(appOptions.DemoSqlPassword)))
{
    throw new InvalidOperationException(
        "DEMO_MODE necesita LOCAL_SOURCE_ROOT y DEMO_SQL_PASSWORD (usa docker-compose.demo.yml).");
}

// Fail-closed fuera de desarrollo: sin claves, el portal quedaría abierto. Solo un AUTH_MODE=none
// explícito lo permite (acceso abierto a propósito, se avisa abajo). En desarrollo se permite.
if (!builder.Environment.IsDevelopment() && !appOptions.IsOpenAccess)
{
    if (!appOptions.IsUiAuthEnabled)
    {
        throw new InvalidOperationException(
            "UI_API_KEY es obligatorio fuera de Development: la UI y /api/* quedarían sin protección.");
    }

    if (AuthConstants.IsPlaceholderKey(appOptions.UiApiKey))
    {
        throw new InvalidOperationException(
            "UI_API_KEY no puede usar el valor por defecto (changeme-ui) fuera de Development.");
    }
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddFluentUIComponents(config =>
{
    // La v4 aplicaba siempre 10px de separación en FluentStack; la v5 no. Se conserva el
    // espaciado del portal (las pilas que fijan su propio gap no se ven afectadas).
    config.DefaultValues.For<FluentStack>().Set(p => p.HorizontalGap, "10px");
    config.DefaultValues.For<FluentStack>().Set(p => p.VerticalGap, "10px");
    config.Localizer = new SharedFluentLocalizer();
});
builder.Services.AddScoped<ThemeState>();
builder.Services.AddApexCharts();

// i18n: inglés por defecto, español opcional. La cultura se persiste por cookie
// (la fija /culture/set) y la elige el CookieRequestCultureProvider.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
var supportedCultures = new[] { new CultureInfo("en"), new CultureInfo("es") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

// Data Protection cifra los secretos de conexión at-rest. El keyring se persiste
// en el volumen compartido (DATA_DIR/keys) para que todas las réplicas lo compartan
// y descifren entre sí; su protección recae en el volumen (sin certificado).
var keysDir = Path.Combine(appOptions.DataDir, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .SetApplicationName("Sluice.Portal")
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));

builder.Services.AddSingleton<IConnectionSecretProtector, ConnectionSecretProtector>();

builder.Services.AddSingleton<ITaskStore, TaskStore>();
builder.Services.AddSingleton<WorkerRegistry>();
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton<PipelineStore>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<PipelineStateStore>();
builder.Services.AddSingleton<TablePauseStore>();
builder.Services.AddSingleton<ColumnSelectionStore>();
builder.Services.AddSingleton<ReportStore>();
builder.Services.AddSingleton<SettingsStore>();

builder.Services.AddSingleton<IConnectionResolver, ConnectionResolver>();
builder.Services.AddSingleton<IAdlsClientFactory, AdlsClientFactory>();
builder.Services.AddSingleton<ISnapshotCatalogService, SnapshotCatalogService>();
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<ISqlMergeExecutor, SqlMergeExecutor>();
builder.Services.AddSingleton<IngestManifestStore>();
builder.Services.AddSingleton<JobProgressStore>();
builder.Services.AddSingleton<IParquetArtifactService, ParquetArtifactService>();
builder.Services.AddSingleton<IPipelineResetService, PipelineResetService>();
builder.Services.AddSingleton<TableCatalogService>();
builder.Services.AddSingleton<IConnectionService, ConnectionService>();
builder.Services.AddSingleton<IPipelineService, PipelineService>();
builder.Services.AddSingleton<IJobService, JobService>();
builder.Services.AddSingleton<OrphanedJobWatchdog>();
builder.Services.AddSingleton<QueueStallWatch>();
builder.Services.AddSingleton<IOrchestrationService, OrchestrationService>();
builder.Services.AddSingleton<IReconcileService, ReconcileService>();
builder.Services.AddSingleton<IJobReportPdfService, JobReportPdfService>();
builder.Services.AddScoped<JobReportDownloader>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton<SnapshotRetentionService>();
builder.Services.AddSingleton<IStorageOverviewService, StorageOverviewService>();
builder.Services.AddSingleton<DocsService>();
builder.Services.AddSingleton<IAnalyticsService, AnalyticsService>();
builder.Services.AddSingleton<IActivityLog, ActivityLogStore>();

builder.Services.AddSingleton<IStageRouter, StageRouter>();
builder.Services.AddSingleton<IIngestExecutor, IngestExecutor>();
builder.Services.AddSingleton<ISqlExecutor, SqlExecutor>();
builder.Services.AddSingleton<IReconcileExecutor, ReconcileExecutor>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<DeadmanService>();

builder.Services.AddHostedService<WorkerHostService>();
builder.Services.AddHostedService<PipelineSchedulerService>();
builder.Services.AddHostedService<RetentionSweepService>();
builder.Services.AddHostedService<SnapshotRetentionSweepService>();
if (appOptions.DemoMode)
{
    builder.Services.AddHostedService<Sluice.Portal.Services.Demo.DemoBootstrapService>();
}

builder.Services.AddSingleton<SessionTokenService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<ReadinessProbe>();
builder.Services.AddSingleton<IAuthService, AuthService>();

DataDirectoryInitializer.EnsureLayout(appOptions);

var app = builder.Build();

if (appOptions.IsOpenAccess)
{
    app.Logger.LogWarning(
        "AUTH_MODE=none: open access, the UI and /api/* require no key. Use only on an internal network or behind an authenticating proxy.");
}
else if (!appOptions.IsUiAuthEnabled)
{
    app.Logger.LogWarning(
        "UI_API_KEY not configured: UI and /api/* are unprotected (development only).");
}

if (!app.Environment.IsDevelopment() && appOptions.KnownProxies.Count == 0 && appOptions.KnownNetworks.Count == 0)
{
    app.Logger.LogWarning(
        "KNOWN_PROXIES/KNOWN_NETWORKS not configured: X-Forwarded-For is only trusted from loopback. Behind a proxy in another container, set KNOWN_NETWORKS or every user shares the proxy's IP in the login throttle.");
}

if (appOptions.IsUiAuthEnabled && appOptions.UiApiKey!.Length < AuthConstants.RecommendedKeyLength)
{
    app.Logger.LogWarning(
        "UI_API_KEY is shorter than {Length} characters: use a long random key (for example `openssl rand -base64 32`).",
        AuthConstants.RecommendedKeyLength);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // El portal va detrás de un proxy con TLS: HSTS obliga al navegador a usar siempre https
    // (solo se envía en respuestas https, que UseForwardedHeaders reconoce vía X-Forwarded-Proto).
    app.UseHsts();
}

// Una vez: las conexiones SQL anteriores siguen confiando en el certificado (ver ConnectionMigrations).
Sluice.Portal.Services.Connections.ConnectionMigrations.PinTrustServerCertificate(
    app.Services.GetRequiredService<ConnectionStore>(), appOptions.DataDir);

app.UseForwardedHeaders();
app.UseRequestLocalization();
app.UseStaticFiles();
app.UseDualAuth();
app.UseAntiforgery();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapCultureEndpoints();
app.MapManagementEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;