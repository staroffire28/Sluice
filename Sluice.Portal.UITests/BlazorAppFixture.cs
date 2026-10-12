using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.UITests;

// Arranca el portal Blazor Server real (Kestrel en un puerto libre) sobre un DATA_DIR
// temporal sembrado, espera /health y lanza Chromium. E2E necesita un servidor HTTP real
// (no el TestServer en memoria de WebApplicationFactory) porque el navegador abre HTTP + SignalR.
public sealed class BlazorAppFixture : IAsyncLifetime
{
    public const string PipelineId = "pipe-ui";
    public const string DeletablePipelineId = "pipe-del";
    public const string DeletablePipelineName = "Deletable Pipeline";
    public const string EditablePipelineId = "pipe-edit";
    public const string EditablePipelineName = "Editable Pipeline";
    public const string DeletableSourceId = "conn-src-del";
    public const string DeletableSourceName = "Deletable Source";
    public const string EditableSourceId = "conn-src-edit";
    public const string EditableSourceName = "Editable Source";
    public const string DeletableDestId = "conn-dst-del";
    public const string DeletableDestName = "Deletable Dest";
    public const string EditableDestId = "conn-dst-edit";
    public const string EditableDestName = "Editable Dest";
    public const string ErrorJobId = "job-ui-error";
    public const string OkJobId = "job-ui-ok";
    // Partial sin FinishedAt: ingest parcial y el pipeline sigue en SQL (job en curso).
    public const string PartialRunningJobId = "job-ui-partial-running";
    // Pipeline (sin definición) de los jobs en curso sembrados: un job en curso bloquea los
    // Run de su pipeline, y pipe-ui se ejecuta en Pipelines_Run_StartsJob.
    public const string LivePipelineId = "pipe-ui-live";
    // Jobs en curso con progress.json: uno en ingest y otro en SQL (ingest ya terminado).
    public const string LiveIngestJobId = "job-ui-live-ingest";
    public const string LiveSqlJobId = "job-ui-live-sql";
    // Igual, con progress.json (prepare hecho, merge 3/10): para el fallo transitorio de lectura.
    public const string ProgressJobId = "job-ui-progress";
    // Reconcile y Diff terminados (etapa única): sus resultados viven en el reporte del job.
    public const string ReconcileJobId = "job-ui-reconcile";
    public const string DiffJobId = "job-ui-diff";
    public const string UiKey = "uitest-ui-key";
    public static readonly string[] TableNames = { "account", "contact" };

    private Process? _app;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string _dataDir = "";
    private readonly ConcurrentQueue<string> _log = new();

    public string BaseUrl { get; private set; } = "";
    public string DataDir => _dataDir;

    public string ProgressPath(string jobId) => Path.Combine(_dataDir, LivePipelineId, "runs", jobId, "progress.json");
    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialized.");

    public async Task<IBrowserContext> NewAuthenticatedContextAsync()
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseUrl });
        var response = await context.APIRequest.PostAsync(
            "/auth/login",
            new APIRequestContextOptions
            {
                Data = JsonSerializer.Serialize(new { key = UiKey }),
                Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            });

        if (!response.Ok && response.Status is not 302 and not 200)
        {
            throw new InvalidOperationException($"Login failed: HTTP {response.Status} {await response.TextAsync()}");
        }

        return context;
    }

    public async Task InitializeAsync()
    {
        var (dll, workdir) = LocateApp();
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-uitest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);

        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        // Datos sin secretos (jobs + estado de tablas) por archivo JSON, con el mismo
        // serializador del portal para garantizar compatibilidad de formato.
        SeedFileBackedData();

        StartApp(dll, workdir, port);
        await WaitForHealthAsync();

        // Conexiones + pipeline por API (la creación cifra los secretos at-rest).
        await SeedApiDataAsync();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
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

        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
        catch { /* el temp es efímero */ }
    }

    private static (string dll, string workdir) LocateApp()
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

        // Mismo framework que estos tests (carpeta del output, p. ej. "net10.0"): así nunca se
        // arranca un build viejo de otro TFM que siga en bin/ tras cambiar de versión de .NET.
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;
        var binRoot = Path.Combine(dir.FullName, "Sluice.Portal", "bin");
        var dll = Directory.Exists(binRoot)
            ? Directory.GetFiles(binRoot, "Sluice.Portal.dll", SearchOption.AllDirectories)
                .Where(p => p.Replace('\\', '/').Contains($"/{tfm}/"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (dll is null)
        {
            throw new InvalidOperationException("No se encontró Sluice.Portal.dll; compila la solución antes de correr los UI tests.");
        }

        return (dll, Path.GetDirectoryName(dll)!);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StartApp(string dll, string workdir, int port)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            WorkingDirectory = workdir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        // Development: sirve los static web assets (JS de Blazor + _content de FluentUI) desde
        // el manifest del build. En Production se esperan publicados en wwwroot (dotnet publish),
        // y al correr el DLL de Debug no se sirven → FluentUI no inicializa y la UI rompe.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DATA_DIR"] = _dataDir;
        psi.Environment["EXECUTION_MODE"] = "local";
        psi.Environment["UI_API_KEY"] = UiKey;

        _app = new Process { StartInfo = psi };
        // Drenamos stdout/stderr de forma asíncrona para no llenar el buffer del pipe
        // (si no se consume, el proceso hijo se bloquea al escribir logs).
        var logFile = Environment.GetEnvironmentVariable("UITEST_APP_LOG");
        void Sink(string? line)
        {
            if (line is null) return;
            _log.Enqueue(line);
            if (!string.IsNullOrEmpty(logFile))
            {
                try { File.AppendAllText(logFile, line + "\n"); } catch { }
            }
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
                throw new InvalidOperationException(
                    $"La app terminó (exit {_app.ExitCode}) antes de estar lista. Log:\n{LogTail()}");
            }

            try
            {
                var resp = await http.GetAsync($"{BaseUrl}/health");
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch { /* aún arrancando */ }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"La app no respondió /health en 60s. Log:\n{LogTail()}");
    }

    private async Task SeedApiDataAsync()
    {
        var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), UseCookies = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        (await http.PostAsJsonAsync("/auth/login", new { Key = UiKey })).EnsureSuccessStatusCode();

        var src = new Connection
        {
            Id = "conn-src-ui",
            Name = "UI Source",
            Role = ConnectionRole.Source,
            Type = "adls",
            Config = new Dictionary<string, string?>
            {
                ["account"] = "uitestbogus",
                ["container"] = "c",
                ["parquet_container"] = "p",
                ["tenant_id"] = "00000000-0000-0000-0000-000000000000",
                ["client_id"] = "00000000-0000-0000-0000-000000000000",
                ["client_secret"] = "uitest-secret",
            },
        };
        var dst = new Connection
        {
            Id = "conn-dst-ui",
            Name = "UI Dest",
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
            Config = new Dictionary<string, string?>
            {
                ["host"] = "sql-host-inalcanzable.invalid",
                ["database"] = "SynapseLink",
                ["user"] = "sa",
                ["password"] = "uitest-pass",
                ["sql_version"] = "2022",
            },
        };

        (await http.PostAsJsonAsync("/api/connections", src)).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("/api/connections", dst)).EnsureSuccessStatusCode();

        // Orígenes desechable/editable (no usados por ningún pipeline → borrables).
        foreach (var (id, name) in new[] { (DeletableSourceId, DeletableSourceName), (EditableSourceId, EditableSourceName) })
        {
            (await http.PostAsJsonAsync("/api/connections", new Connection
            {
                Id = id,
                Name = name,
                Role = ConnectionRole.Source,
                Type = "adls",
                Config = new Dictionary<string, string?> { ["account"] = "bogus" },
            })).EnsureSuccessStatusCode();
        }

        // Destinos desechable/editable (no usados por ningún pipeline → borrables).
        foreach (var (id, name) in new[] { (DeletableDestId, DeletableDestName), (EditableDestId, EditableDestName) })
        {
            (await http.PostAsJsonAsync("/api/connections", new Connection
            {
                Id = id,
                Name = name,
                Role = ConnectionRole.Destination,
                Type = "sqlserver",
                Config = new Dictionary<string, string?> { ["host"] = "bogus", ["user"] = "sa", ["password"] = "x" },
            })).EnsureSuccessStatusCode();
        }

        var pipeline = new Pipeline
        {
            Id = PipelineId,
            Name = "UI Pipeline",
            SourceId = "conn-src-ui",
            DestinationId = "conn-dst-ui",
            Enabled = false,
        };
        (await http.PostAsJsonAsync("/api/pipelines", pipeline)).EnsureSuccessStatusCode();

        // Pipeline desechable de id conocido, para el flujo de borrado por UI.
        var deletable = new Pipeline
        {
            Id = DeletablePipelineId,
            Name = DeletablePipelineName,
            SourceId = "conn-src-ui",
            DestinationId = "conn-dst-ui",
            Enabled = false,
        };
        (await http.PostAsJsonAsync("/api/pipelines", deletable)).EnsureSuccessStatusCode();

        // Pipeline para el flujo de edición (renombrar).
        var editable = new Pipeline
        {
            Id = EditablePipelineId,
            Name = EditablePipelineName,
            SourceId = "conn-src-ui",
            DestinationId = "conn-dst-ui",
            Enabled = false,
        };
        (await http.PostAsJsonAsync("/api/pipelines", editable)).EnsureSuccessStatusCode();
    }

    private void SeedFileBackedData()
    {
        var errorJob = new Job
        {
            Id = ErrorJobId,
            PipelineId = PipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Error,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "error", ["sql"] = "pending", ["reconcile"] = "pending",
            },
            Events = [new JobEvent { At = DateTimeOffset.UtcNow.AddMinutes(-4), Level = EventLevel.Error, Message = "Fallo simulado (seed UI test)." }],
        };

        var okJob = new Job
        {
            Id = OkJobId,
            PipelineId = PipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Ok,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done",
            },
        };

        var partialRunningJob = new Job
        {
            Id = PartialRunningJobId,
            PipelineId = LivePipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Partial,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "error", ["sql"] = "running", ["reconcile"] = "pending",
            },
        };

        WriteJson(Path.Combine(_dataDir, "jobs", ErrorJobId + ".json"), errorJob);
        WriteJson(Path.Combine(_dataDir, "jobs", OkJobId + ".json"), okJob);
        WriteJson(Path.Combine(_dataDir, "jobs", PartialRunningJobId + ".json"), partialRunningJob);
        WriteJson(Path.Combine(_dataDir, "jobs", ProgressJobId + ".json"), partialRunningJob with { Id = ProgressJobId });
        WriteJson(ProgressPath(ProgressJobId), new JobProgress
        {
            JobId = ProgressJobId,
            Stage = "sql",
            CurrentStep = JobStepCatalog.Merge,
            Steps = new Dictionary<string, StepProgress>
            {
                [JobStepCatalog.Prepare] = new() { Status = "done", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), FinishedAt = DateTimeOffset.UtcNow.AddSeconds(-50) },
                [JobStepCatalog.Merge] = new() { Status = "running", StartedAt = DateTimeOffset.UtcNow.AddSeconds(-50), Done = 3, Total = 10 },
            },
        });

        var liveIngestJob = new Job
        {
            Id = LiveIngestJobId,
            PipelineId = LivePipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "running", ["sql"] = "pending", ["reconcile"] = "pending",
            },
        };
        WriteJson(Path.Combine(_dataDir, "jobs", LiveIngestJobId + ".json"), liveIngestJob);
        WriteJson(ProgressPath(LiveIngestJobId), new JobProgress
        {
            JobId = LiveIngestJobId,
            Stage = "ingest",
            CurrentStep = JobStepCatalog.Snapshots,
            Steps = new Dictionary<string, StepProgress>
            {
                [JobStepCatalog.Discover] = new() { Status = "done", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-2) },
                [JobStepCatalog.Snapshots] = new() { Status = "running", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), Done = 46, Total = 201, Rows = 987 },
            },
        });

        WriteJson(Path.Combine(_dataDir, "jobs", LiveSqlJobId + ".json"), liveIngestJob with
        {
            Id = LiveSqlJobId,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "running", ["reconcile"] = "pending" },
            Summary = new Dictionary<string, object?> { ["snapshotsProcessed"] = 3, ["snapshotsSelected"] = 3, ["csvRows"] = 321 },
        });
        WriteJson(ProgressPath(LiveSqlJobId), new JobProgress
        {
            JobId = LiveSqlJobId,
            Stage = "sql",
            CurrentStep = JobStepCatalog.Merge,
            Steps = new Dictionary<string, StepProgress>
            {
                [JobStepCatalog.Prepare] = new() { Status = "done", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
                [JobStepCatalog.Merge] = new() { Status = "running", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1), Done = 2, Total = 5, Rows = 654 },
            },
        });

        // En el pipeline sin definición: un ReconcileReport en pipe-ui cambiaría su "última
        // conciliación" para el resto de tests.
        var countOnlyJob = new Job
        {
            Id = ReconcileJobId,
            PipelineId = LivePipelineId,
            Type = JobType.Reconcile,
            Status = JobStatus.Ok,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "skipped", ["sql"] = "skipped", ["reconcile"] = "done",
            },
            Summary = new Dictionary<string, object?> { ["maxDivergencePct"] = 1.67, ["reconcilePartial"] = false },
        };
        WriteJson(Path.Combine(_dataDir, "jobs", ReconcileJobId + ".json"), countOnlyJob);
        WriteJson(Path.Combine(_dataDir, "reports", "reconcile", $"recon-{ReconcileJobId}.json"), new ReconcileReport
        {
            Id = $"recon-{ReconcileJobId}",
            PipelineId = LivePipelineId,
            JobId = ReconcileJobId,
            MaxDivergencePct = 1.67,
            Entities = [new ReconcileEntityResult { Entity = "custtable", SourceRows = 120, DestinationRows = 118, DivergencePct = 1.67 }],
        });

        WriteJson(Path.Combine(_dataDir, "jobs", DiffJobId + ".json"), countOnlyJob with
        {
            Id = DiffJobId,
            Type = JobType.Diff,
            Summary = new Dictionary<string, object?> { ["diffEntities"] = 1, ["diffTotalDelta"] = 3 },
        });
        WriteJson(Path.Combine(_dataDir, "reports", "diff", $"diff-{DiffJobId}.json"), new DiffReport
        {
            Id = $"diff-{DiffJobId}",
            PipelineId = LivePipelineId,
            JobId = DiffJobId,
            Entities = [new DiffEntityResult { Entity = "vendtable", SourceRows = 50, DestinationRows = 47, Delta = 3 }],
        });

        var state = new PipelineState
        {
            PipelineId = PipelineId,
            Tables = new Dictionary<string, TableState>
            {
                [TableNames[0]] = new TableState { RowCount = 100, Status = "ok", LastLoad = DateTimeOffset.UtcNow },
                [TableNames[1]] = new TableState { RowCount = 50, Status = "ok", LastLoad = DateTimeOffset.UtcNow },
            },
        };
        WriteJson(Path.Combine(_dataDir, PipelineId, "state", "pipeline_state.json"), state);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonDefaults.Options));
    }

    private string LogTail()
    {
        var sb = new StringBuilder();
        foreach (var line in _log.TakeLast(40))
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }
}

[CollectionDefinition("blazor-ui")]
public sealed class BlazorUiCollection : ICollectionFixture<BlazorAppFixture> { }
