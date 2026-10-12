using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Sluice.Portal.UatTests.Infrastructure;

// Cómo abrir el navegador para un escenario.
public sealed record UatBrowser
{
    public bool Authenticated { get; init; } = true;
    public bool Mobile { get; init; }
    public bool Dark { get; init; }
}

// Levanta el portal real (Kestrel en un puerto libre) en modo monolito, con DATA_DIR y
// LOCAL_SOURCE_ROOT temporales y vacíos, y un Chromium. La sesión se abre una vez por la pantalla
// de login y se reutiliza en cada escenario (los escenarios de acceso usan el login a mano).
public sealed class UatAppFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _log = new();
    private Process? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string? _storageState;
    private int _sources;

    public string BaseUrl { get; private set; } = "";
    public string ApiKey { get; } = "uat-" + Guid.NewGuid().ToString("N");
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "sluice-uat-data-" + Guid.NewGuid().ToString("N"));
    public string LocalRoot { get; } = Path.Combine(Path.GetTempPath(), "sluice-uat-source-" + Guid.NewGuid().ToString("N"));
    public string ArtifactsRoot { get; private set; } = "";
    public UatSql? Sql { get; private set; }

    public UatSql RequireSql() => Sql ?? throw new InvalidOperationException($"{UatSql.Variable} no está definida.");

    public async Task InitializeAsync()
    {
        var (repo, dll) = LocateApp();
        ArtifactsRoot = Environment.GetEnvironmentVariable("UAT_ARTIFACTS") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(repo, "TestResults", "uat");
        Directory.CreateDirectory(ArtifactsRoot);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LocalRoot);

        Sql = await UatSql.CreateAsync();

        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        StartApp(dll);
        await WaitForHealthAsync();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("UAT_HEADED") != "1",
        });
        _storageState = await LoginThroughUiAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();

        try
        {
            if (_app is { HasExited: false })
            {
                _app.Kill(entireProcessTree: true);
                _app.WaitForExit(5000);
            }
        }
        catch { /* best effort */ }

        if (Sql is not null)
        {
            try { await Sql.DropAsync(); } catch { /* el SQL de pruebas puede haberse ido */ }
        }

        foreach (var dir in new[] { DataDir, LocalRoot })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temporal */ }
        }
    }

    public async Task<IBrowserContext> NewContextAsync(UatBrowser options, string? videoDir)
    {
        var browser = _browser ?? throw new InvalidOperationException("Browser not initialized.");
        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            StorageState = options.Authenticated ? _storageState : null,
            ViewportSize = options.Mobile ? new ViewportSize { Width = 375, Height = 812 } : new ViewportSize { Width = 1440, Height = 900 },
            IsMobile = options.Mobile,
            HasTouch = options.Mobile,
            ColorScheme = options.Dark ? ColorScheme.Dark : ColorScheme.Light,
            AcceptDownloads = true,
            RecordVideoDir = videoDir,
            RecordVideoSize = videoDir is null ? null : new RecordVideoSize { Width = 1280, Height = 800 },
        });
    }

    // Un origen Synapse Link simulado nuevo, en su propia carpeta bajo LOCAL_SOURCE_ROOT.
    public SynapseLinkFolder NewSourceFolder(string name) =>
        new(LocalRoot, $"{name}-{Interlocked.Increment(ref _sources)}");

    public string LogTail(int lines = 60)
    {
        var sb = new StringBuilder();
        foreach (var line in _log.TakeLast(lines))
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private async Task<string> LoginThroughUiAsync()
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        try
        {
            var page = await context.NewPageAsync();
            var login = new Pages.LoginPage(page);
            await login.OpenAsync();
            await login.SignInAsync(ApiKey);
            await page.Locator("[data-blazor-interactive]").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30000 });
            return await context.StorageStateAsync();
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static (string repo, string dll) LocateApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sluice.Portal.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("No se encontró Sluice.Portal.sln subiendo desde el output de tests.");
        }

        // Mismo framework y configuración que este proyecto: nunca un build viejo de otra carpeta.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = output.Name;
        var configuration = output.Parent?.Name ?? "Debug";
        var dll = Path.Combine(dir.FullName, "Sluice.Portal", "bin", configuration, tfm, "Sluice.Portal.dll");
        if (!File.Exists(dll))
        {
            throw new InvalidOperationException($"No se encontró {dll}; compila la solución antes de correr la UAT.");
        }

        return (dir.FullName, dll);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StartApp(string dll)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        // Development sirve los static web assets desde el manifest del build (ver BlazorAppFixture).
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DATA_DIR"] = DataDir;
        psi.Environment["LOCAL_SOURCE_ROOT"] = LocalRoot;
        psi.Environment["EXECUTION_MODE"] = "local";
        psi.Environment["UI_API_KEY"] = ApiKey;

        _app = new Process { StartInfo = psi };
        var logFile = Path.Combine(ArtifactsRoot, "portal.log");
        File.WriteAllText(logFile, "");
        void Sink(string? line)
        {
            if (line is null) return;
            _log.Enqueue(line);
            try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { }
        }

        _app.OutputDataReceived += (_, e) => Sink(e.Data);
        _app.ErrorDataReceived += (_, e) => Sink(e.Data);
        _app.Start();
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();
    }

    private async Task WaitForHealthAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var i = 0; i < 60; i++)
        {
            if (_app is { HasExited: true })
            {
                throw new InvalidOperationException($"El portal terminó (exit {_app.ExitCode}) antes de estar listo. Log:\n{LogTail()}");
            }

            try
            {
                if ((await http.GetAsync($"{BaseUrl}/health")).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch { /* aún arrancando */ }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"El portal no respondió /health en 60 s. Log:\n{LogTail()}");
    }
}

[CollectionDefinition(Name)]
public sealed class UatCollection : ICollectionFixture<UatAppFixture>
{
    public const string Name = "uat";
}
