using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Scheduler;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Tests.Fakes;
using PipelineModel = Sluice.Portal.Models.Pipeline;

namespace Sluice.Portal.Tests;

// Hosts de barrido de retención: puerta de orquestador, ciclo saltado si settings.json es ilegible
// y fallos del barrido contenidos (log de actividad + alerta) en vez de tumbar el host.
public sealed class RetentionSweepServiceTests : IDisposable
{
    private const int K = RetentionService.KeepLastPerPipeline;

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-retention-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly IOptions<AppOptions> _options;
    private readonly JobStore _jobs;
    private readonly RecordingWebhookHandler _webhook = new();
    private readonly RecordingActivityLog _activity = new();

    public RetentionSweepServiceTests()
    {
        DataDirectoryInitializer.EnsureLayout(_dataDir);
        _options = Options.Create(new AppOptions { DataDir = _dataDir, AlertWebhookUrl = "https://hook.example/retention" });
        _jobs = new JobStore(_options);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    // --- RetentionSweepService ---

    [Fact]
    public async Task Barrido_con_settings_validos_purga_los_jobs_viejos_mas_alla_del_piso()
    {
        SeedOldJobs(K + 2);

        await SweepAsync(CreateRetentionHost());

        Assert.Equal(K, _jobs.ListAll().Count);
        Assert.Empty(_webhook.Bodies);
    }

    [Fact]
    public async Task Barrido_con_settings_json_ilegible_salta_el_ciclo_sin_borrar_nada()
    {
        SeedOldJobs(K + 2);
        File.WriteAllText(Path.Combine(_dataDir, "settings.json"), "{ esto no es json");
        var host = CreateRetentionHost();

        await SweepAsync(host);

        // Con los valores por defecto (90 días) se habría purgado: el ciclo debe omitirse.
        Assert.Equal(K + 2, _jobs.ListAll().Count);
        Assert.Empty(_activity.Entries);
        Assert.Empty(_webhook.Bodies);
    }

    [Fact]
    public async Task Barrido_que_falla_se_registra_y_alerta_sin_propagar()
    {
        SeedOldJobs(K + 2);
        // RetentionService registra "retention.sweep" tras borrar: si ese log revienta, el barrido falla.
        var failingRetention = new RetentionService(
            _options,
            new RecordingActivityLog { ThrowOnAction = "retention.sweep" },
            NullLogger<RetentionService>.Instance);
        var host = CreateRetentionHost(failingRetention);

        await SweepAsync(host);

        var entry = Assert.Single(_activity.Entries);
        Assert.Equal("retention.error", entry.Action);
        Assert.Equal("error", entry.Level);
        Assert.Contains("activity log forzado a fallar", entry.Message);
        await _webhook.WaitForRequestsAsync(1);
        Assert.Contains("Retention sweep failed", Assert.Single(_webhook.Bodies));
    }

    [Fact]
    public async Task Nodo_worker_no_purga_y_el_orquestador_queda_esperando()
    {
        var workerOptions = Options.Create(new AppOptions { DataDir = _dataDir, ExecutionMode = "distributed", WorkerStage = "sql" });
        using var worker = CreateRetentionHost(options: workerOptions);
        using var orchestrator = CreateRetentionHost();

        await worker.StartAsync(CancellationToken.None);
        await orchestrator.StartAsync(CancellationToken.None);

        // El worker retorna enseguida; el orquestador queda esperando su primer barrido (6 h).
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
        await Task.Delay(200);
        Assert.False(orchestrator.ExecuteTask!.IsCompleted);

        await orchestrator.StopAsync(CancellationToken.None);
        Assert.True(orchestrator.ExecuteTask.IsCompleted);
        Assert.False(orchestrator.ExecuteTask.IsFaulted);
    }

    // --- SnapshotRetentionSweepService ---

    [Fact]
    public async Task Snapshots_nodo_worker_no_barre()
    {
        var jobs = new ThrowingRunningJobs();
        var workerOptions = Options.Create(new AppOptions { DataDir = _dataDir, ExecutionMode = "distributed", WorkerStage = "ingest" });
        using var host = CreateSnapshotHost(jobs, workerOptions);

        await host.StartAsync(CancellationToken.None);

        await host.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(0, jobs.Calls);
        Assert.Empty(_activity.Entries);
    }

    [Fact]
    public async Task Snapshots_orquestador_barre_al_arrancar_y_un_fallo_se_registra_y_alerta()
    {
        var jobs = new ThrowingRunningJobs();
        using var host = CreateSnapshotHost(jobs);

        await host.StartAsync(CancellationToken.None);
        await _webhook.WaitForRequestsAsync(1);
        await host.StopAsync(CancellationToken.None);

        // ListRunning lanza dentro del barrido: el host lo contiene y no queda en estado Faulted.
        Assert.Equal(1, jobs.Calls);
        Assert.True(host.ExecuteTask!.IsCompleted);
        Assert.False(host.ExecuteTask.IsFaulted);
        var entry = Assert.Single(_activity.Entries);
        Assert.Equal("snapshot-retention.error", entry.Action);
        Assert.Equal("error", entry.Level);
        Assert.Contains("lectura de jobs rota", entry.Message);
        Assert.Contains("Snapshot retention sweep failed", Assert.Single(_webhook.Bodies));
    }

    // --- helpers ---

    private static Task SweepAsync(RetentionSweepService host) =>
        host.SweepAsync();

    private RetentionSweepService CreateRetentionHost(RetentionService? retention = null, IOptions<AppOptions>? options = null)
    {
        options ??= _options;
        return new RetentionSweepService(
            options,
            new SettingsStore(options),
            retention ?? new RetentionService(options, _activity, NullLogger<RetentionService>.Instance),
            CreateAlerts(options),
            NullLogger<RetentionSweepService>.Instance,
            _activity);
    }

    private SnapshotRetentionSweepService CreateSnapshotHost(IJobService jobs, IOptions<AppOptions>? options = null)
    {
        options ??= _options;
        var retention = new SnapshotRetentionService(
            new NoPipelines(),
            jobs,
            new UnusedAdlsFactory(),
            _activity,
            NullLogger<SnapshotRetentionService>.Instance);
        return new SnapshotRetentionSweepService(
            options,
            new SettingsStore(options),
            retention,
            CreateAlerts(options),
            NullLogger<SnapshotRetentionSweepService>.Instance,
            _activity);
    }

    private AlertService CreateAlerts(IOptions<AppOptions> options) =>
        new(options, new SettingsStore(options), NullLogger<AlertService>.Instance, new RecordingHttpClientFactory(_webhook));

    // Jobs terminados hace 200 días: purgables por antigüedad; solo los que excedan el piso K sobran.
    private void SeedOldJobs(int count)
    {
        var finished = DateTimeOffset.UtcNow.AddDays(-200);
        for (var i = 0; i < count; i++)
        {
            _jobs.Save(new Job
            {
                Id = $"job-old-{i:D3}",
                PipelineId = "pipe-1",
                Status = JobStatus.Ok,
                StartedAt = finished.AddMinutes(i),
                FinishedAt = finished.AddMinutes(i),
            });
        }
    }

    private sealed class ThrowingRunningJobs : IJobService
    {
        public int Calls { get; private set; }

        public IReadOnlyList<Job> ListRunning()
        {
            Calls++;
            throw new IOException("lectura de jobs rota");
        }

        public IReadOnlyList<Job> List(int page = 1, int pageSize = 20, string? pipelineId = null, JobStatus? status = null, DateRange? range = null) => throw new NotSupportedException();
        public int Count(string? pipelineId = null, JobStatus? status = null, DateRange? range = null) => throw new NotSupportedException();
        public Job? Get(string id) => throw new NotSupportedException();
        public Job Create(Job job) => throw new NotSupportedException();
        public Job? Update(string id, Job job) => throw new NotSupportedException();
        public void AppendEvent(string jobId, EventLevel level, string message) => throw new NotSupportedException();
    }

    private sealed class NoPipelines : IPipelineService
    {
        public IReadOnlyList<PipelineModel> List() => [];
        public PipelineModel? Get(string id) => null;
        public PipelineModel Create(PipelineModel pipeline) => throw new NotSupportedException();
        public PipelineModel? Update(string id, PipelineModel pipeline) => throw new NotSupportedException();
        public bool Delete(string id, bool wipeState, out string? error) => throw new NotSupportedException();
        public PipelineState GetState(string pipelineId) => throw new NotSupportedException();
        public IReadOnlyList<object> GetTables(string pipelineId) => throw new NotSupportedException();
    }

    private sealed class UnusedAdlsFactory : IAdlsClientFactory
    {
        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
