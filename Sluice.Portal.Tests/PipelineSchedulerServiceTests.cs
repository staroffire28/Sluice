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

// Lógica de decisión de un ciclo del scheduler (PollAsync) y la puerta de orquestador.
public sealed class PipelineSchedulerServiceTests : IDisposable
{
    private const string Webhook = "https://hook.example/scheduler";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-scheduler-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingOrchestration _orchestration = new();
    private readonly StubCatalog _catalog = new();
    private readonly RecordingActivityLog _activity = new();
    private readonly RecordingWebhookHandler _webhook = new();
    private readonly StubPipelines _pipelines = new();
    private readonly IOptions<AppOptions> _options;
    private readonly JobService _jobs;

    public PipelineSchedulerServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _options = Options.Create(new AppOptions { DataDir = _dataDir, AlertWebhookUrl = Webhook });
        _jobs = new JobService(new JobStore(_options));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Poll_pipeline_habilitado_con_snapshots_pendientes_arranca_un_job_incremental()
    {
        _pipelines.Add("pipe-1", enabled: true);
        _catalog.Set("pipe-1", Catalog(pending: ["2026-06-01T08.00.00Z", "2026-06-01T20.00.00Z"]));

        await PollAsync(CreateService());

        var run = Assert.Single(_orchestration.Runs);
        Assert.Equal(("pipe-1", JobType.Incremental), run);
        var entry = Assert.Single(_activity.Entries, e => e.Action == "scheduler.run");
        Assert.Contains("2 snapshot(s)", entry.Message);
        Assert.Empty(_webhook.Bodies);
    }

    [Fact]
    public async Task Poll_pipeline_deshabilitado_no_se_ejecuta_ni_se_consulta()
    {
        _pipelines.Add("pipe-off", enabled: false);
        _catalog.Set("pipe-off", Catalog(pending: ["2026-06-01T08.00.00Z"]));

        await PollAsync(CreateService());

        Assert.Empty(_orchestration.Runs);
        Assert.Empty(_catalog.Queried);
    }

    [Fact]
    public async Task Poll_pipeline_con_job_en_curso_no_arranca_otro()
    {
        _pipelines.Add("pipe-busy", enabled: true);
        _pipelines.Add("pipe-free", enabled: true);
        _catalog.Set("pipe-busy", Catalog(pending: ["2026-06-01T08.00.00Z"]));
        _catalog.Set("pipe-free", Catalog(pending: ["2026-06-01T08.00.00Z"]));
        // StartedAt reciente: el watchdog (margen de 15 min) no lo da por huérfano.
        _jobs.Create(new Job { Id = "job-busy", PipelineId = "pipe-busy", Status = JobStatus.Running });

        await PollAsync(CreateService());

        Assert.Equal(["pipe-free"], _orchestration.Runs.Select(r => r.PipelineId));
        Assert.DoesNotContain("pipe-busy", _catalog.Queried);
    }

    [Fact]
    public async Task Poll_catalogo_bloqueado_o_sin_pendientes_no_arranca_job()
    {
        _pipelines.Add("pipe-blocked", enabled: true);
        _pipelines.Add("pipe-idle", enabled: true);
        _catalog.Set("pipe-blocked", Catalog(pending: ["2026-06-01T08.00.00Z"], blocked: true));
        _catalog.Set("pipe-idle", Catalog(pending: []));

        await PollAsync(CreateService());

        Assert.Empty(_orchestration.Runs);
        Assert.Equal(["pipe-blocked", "pipe-idle"], _catalog.Queried.Order());
        Assert.DoesNotContain(_activity.Entries, e => e.Action == "scheduler.run");
    }

    [Fact]
    public async Task Poll_fallo_en_un_pipeline_no_detiene_a_los_demas_y_dispara_alerta()
    {
        _pipelines.Add("pipe-bad", enabled: true);
        _pipelines.Add("pipe-good", enabled: true);
        _catalog.Set("pipe-bad", Catalog(pending: ["2026-06-01T08.00.00Z"]));
        _catalog.Set("pipe-good", Catalog(pending: ["2026-06-01T08.00.00Z"]));
        _orchestration.FailFor = "pipe-bad";

        await PollAsync(CreateService());

        Assert.Equal(["pipe-bad", "pipe-good"], _orchestration.Runs.Select(r => r.PipelineId));
        var error = Assert.Single(_activity.Entries, e => e.Action == "scheduler.error");
        Assert.Equal("error", error.Level);
        Assert.Contains("pipe-bad", error.Message);
        Assert.Single(_activity.Entries, e => e.Action == "scheduler.run" && e.Message.Contains("pipe-good"));

        await _webhook.WaitForRequestsAsync(1);
        var body = Assert.Single(_webhook.Bodies);
        Assert.Contains("Scheduler failed for pipe-bad", body);
    }

    [Fact]
    public async Task Poll_stall_del_changelog_alerta_una_sola_vez_dentro_de_la_ventana()
    {
        _pipelines.Add("pipe-1", enabled: true);
        _catalog.Set("pipe-1", Catalog(pending: []));
        _catalog.Stalled = true;
        var service = CreateService();

        await PollAsync(service);
        await PollAsync(service);

        await _webhook.WaitForRequestsAsync(1);
        var body = Assert.Single(_webhook.Bodies);
        // System.Text.Json escapa las comillas simples ('): se comprueba sin ellas.
        Assert.Contains("Changelog stall detected for source", body);
        Assert.Contains("src-pipe-1", body);
    }

    [Fact]
    public async Task Poll_ping_al_deadman_antes_de_agendar()
    {
        var pinged = new RecordingWebhookHandler();
        var options = Options.Create(new AppOptions { DataDir = _dataDir, DeadmanPingUrl = "https://ping.example/hb" });

        await PollAsync(CreateService(deadman: new DeadmanService(options, new RecordingHttpClientFactory(pinged), NullLogger<DeadmanService>.Instance)));

        // GET sin cuerpo: basta con que la petición haya salido hacia la URL configurada.
        await pinged.WaitForRequestsAsync(1);
    }

    [Fact]
    public async Task Nodo_worker_no_agenda_ExecuteAsync_termina_sin_sondear()
    {
        var workerOptions = Options.Create(new AppOptions { DataDir = _dataDir, ExecutionMode = "distributed", WorkerStage = "ingest" });
        _pipelines.Add("pipe-1", enabled: true);
        using var service = CreateService(workerOptions);

        await service.StartAsync(CancellationToken.None);

        // En un worker ExecuteAsync retorna enseguida (en un orquestador queda esperando el primer poll).
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.ExecuteTask.IsCompletedSuccessfully);
        Assert.Empty(_orchestration.Runs);
        Assert.Equal(0, _pipelines.ListCalls);
    }

    [Fact]
    public async Task Nodo_orquestador_espera_el_primer_poll_y_se_detiene_limpio()
    {
        _pipelines.Add("pipe-1", enabled: true);
        _catalog.Set("pipe-1", Catalog(pending: ["2026-06-01T08.00.00Z"]));
        using var service = CreateService();

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.False(service.ExecuteTask!.IsCompleted); // espera el primer poll (>= 1 min)

        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompleted);
        Assert.False(service.ExecuteTask.IsFaulted);
        Assert.Empty(_orchestration.Runs);
    }

    private static Task PollAsync(PipelineSchedulerService service) =>
        service.PollAsync(CancellationToken.None);

    private PipelineSchedulerService CreateService(IOptions<AppOptions>? options = null, DeadmanService? deadman = null)
    {
        options ??= _options;
        var settings = new SettingsStore(options);
        var tasks = new TaskStore(options, settings);
        var alerts = new AlertService(options, settings, NullLogger<AlertService>.Instance, new RecordingHttpClientFactory(_webhook));
        deadman ??= new DeadmanService(options, new RecordingHttpClientFactory(new RecordingWebhookHandler()), NullLogger<DeadmanService>.Instance);

        return new PipelineSchedulerService(
            options,
            settings,
            _pipelines,
            _orchestration,
            _jobs,
            alerts,
            deadman,
            _catalog,
            new StubAdlsFactory(),
            NullLogger<PipelineSchedulerService>.Instance,
            _activity,
            new OrphanedJobWatchdog(_jobs, tasks));
    }

    private static SnapshotCatalogResult Catalog(IReadOnlyList<string> pending, bool blocked = false) =>
        new(
            SettledSnapshot: null,
            LastProcessedSnapshot: null,
            ChangelogLastModified: null,
            IsChangelogStalled: false,
            Snapshots: [],
            PendingSnapshots: pending,
            UnclosedSnapshots: [],
            Blocked: blocked,
            BlockReason: blocked ? "snapshot sin cerrar" : null,
            EligibleRetentionPurgeCount: 0,
            RetentionDryRun: true);

    private sealed class StubPipelines : IPipelineService
    {
        private readonly List<PipelineModel> _items = [];

        public int ListCalls { get; private set; }

        public void Add(string id, bool enabled) =>
            _items.Add(new PipelineModel { Id = id, Name = id, SourceId = $"src-{id}", DestinationId = $"dst-{id}", Enabled = enabled });

        public IReadOnlyList<PipelineModel> List()
        {
            ListCalls++;
            return _items;
        }

        public PipelineModel? Get(string id) => _items.FirstOrDefault(p => p.Id == id);
        public PipelineModel Create(PipelineModel pipeline) => throw new NotSupportedException();
        public PipelineModel? Update(string id, PipelineModel pipeline) => throw new NotSupportedException();
        public bool Delete(string id, bool wipeState, out string? error) => throw new NotSupportedException();
        public PipelineState GetState(string pipelineId) => throw new NotSupportedException();
        public IReadOnlyList<object> GetTables(string pipelineId) => throw new NotSupportedException();
    }

    private sealed class StubCatalog : ISnapshotCatalogService
    {
        private readonly Dictionary<string, SnapshotCatalogResult> _catalogs = [];

        public List<string> Queried { get; } = [];
        public bool Stalled { get; set; }

        public void Set(string pipelineId, SnapshotCatalogResult catalog) => _catalogs[pipelineId] = catalog;

        public Task<SnapshotCatalogResult> GetCatalogAsync(string pipelineId, CancellationToken cancellationToken = default)
        {
            Queried.Add(pipelineId);
            return Task.FromResult(_catalogs[pipelineId]);
        }

        public Task<SnapshotRetentionPreviewResult> GetRetentionPreviewAsync(string pipelineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool IsChangelogStalled(DateTimeOffset? changelogLastModified, DateTimeOffset now) => Stalled;
    }

    private sealed class RecordingOrchestration : IOrchestrationService
    {
        public List<(string PipelineId, JobType Type)> Runs { get; } = [];
        public string? FailFor { get; set; }

        public Task<Job> RunPipelineAsync(string pipelineId, JobType type, CancellationToken cancellationToken = default)
        {
            Runs.Add((pipelineId, type));
            if (pipelineId == FailFor)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(new Job { Id = $"job-{pipelineId}", PipelineId = pipelineId, Type = type });
        }

        public object GetMode() => throw new NotSupportedException();
        public object GetQueueCounts() => throw new NotSupportedException();
        public QueueSnapshot GetQueueSnapshot() => throw new NotSupportedException();
        public IReadOnlyList<WorkerStatus> GetWorkers() => throw new NotSupportedException();
        public IReadOnlyList<Job> GetActiveJobs() => throw new NotSupportedException();
        public Task<Job> RetryJobAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Job> ResyncTableAsync(string pipelineId, string entity, ResyncMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Job> ReloadFromParquetAsync(string pipelineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubAdlsFactory : IAdlsClientFactory
    {
        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAdlsClient>(new ChangelogOnlyAdlsClient());

        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // El scheduler solo consulta la fecha del changelog para detectar stalls.
    private sealed class ChangelogOnlyAdlsClient : IAdlsClient
    {
        public Task<DateTimeOffset?> GetChangelogLastModifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        public Task TestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetSettledSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListSnapshotFoldersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SnapshotIntegrity> ValidateSnapshotAsync(string snapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListEntitiesAsync(string snapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CdmEntitySchema> GetEntitySchemaAsync(string entity, string snapshot, string? settledSnapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ReadEntityCsvContentAsync(string snapshot, string entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> CountEntityCsvRowsAsync(string snapshot, string entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamEntityCsvLinesAsync(string snapshot, string entity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> ReadGlobalOptionsetMetadataCsvAsync(string snapshot, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSnapshotFolderAsync(string snapshotFolder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
