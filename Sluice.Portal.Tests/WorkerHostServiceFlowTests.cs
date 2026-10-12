using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Tests.Fakes;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests;

// Camino real de una tarea reclamada (ProcessStageAsync): ejecutor del stage → complete + router,
// o ejecutor que falla → tarea en error + job fallido. TaskStore/JobStore reales en un DATA_DIR temporal.
public sealed class WorkerHostServiceFlowTests : IDisposable
{
    private const string WorkerId = "w1";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-worker-flow-" + Guid.NewGuid().ToString("N"));
    private readonly JobService _jobs;
    private readonly TaskStore _tasks;
    private readonly WorkerRegistry _registry;
    private readonly RecordingExecutor _ingest = new();
    private readonly RecordingExecutor _sql = new();
    private readonly RecordingExecutor _reconcile = new();
    private readonly RecordingRouter _router = new();
    private readonly RecordingActivityLog _activity = new();
    private readonly WorkerHostService _host;

    public WorkerHostServiceFlowTests()
    {
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new AppOptions { DataDir = _dataDir, WorkerId = WorkerId });
        var settings = new SettingsStore(options);
        _jobs = new JobService(new JobStore(options));
        _tasks = new TaskStore(options, settings);
        _registry = new WorkerRegistry(options);
        _host = new WorkerHostService(
            options,
            _tasks,
            _router,
            _ingest,
            _sql,
            _reconcile,
            _registry,
            _jobs,
            settings,
            new PipelineStore(options),
            // Sin webhook configurado AlertService solo escribe al log: no hace HTTP.
            new AlertService(options, settings, NullLogger<AlertService>.Instance, new RecordingHttpClientFactory(new RecordingWebhookHandler())),
            NullLogger<WorkerHostService>.Instance,
            _activity);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(PipelineStage.Ingest)]
    [InlineData(PipelineStage.Sql)]
    [InlineData(PipelineStage.Reconcile)]
    public async Task Tarea_reclamada_ejecuta_el_executor_de_su_stage_la_completa_y_la_rutea(PipelineStage stage)
    {
        CreateRunningJob("job-1");
        await _tasks.EnqueueAsync(NewTask("job-1", stage));

        await ProcessStageAsync(stage);

        var executed = Assert.Single(AllExecutors.SelectMany(e => e.Tasks));
        Assert.Equal($"task-job-1-{stage.ToString().ToLowerInvariant()}", executed.Id);
        Assert.Single(ExecutorFor(stage).Tasks); // lo ejecutó el executor de ese stage y no otro
        Assert.Equal(WorkerId, executed.ClaimedBy);

        Assert.Equal(0, _tasks.CountByStatus(stage, PipelineTaskStatus.Pending));
        Assert.Equal(0, _tasks.CountByStatus(stage, PipelineTaskStatus.Claimed));
        Assert.Equal(1, _tasks.CountByStatus(stage, PipelineTaskStatus.Done));

        var routed = Assert.Single(_router.Routed);
        Assert.Equal(executed.Id, routed.Id);
        Assert.Equal(PipelineTaskStatus.Done, routed.Status);

        Assert.Equal(JobStatus.Running, _jobs.Get("job-1")!.Status);
        var worker = Assert.Single(_registry.ListAll());
        Assert.Equal((WorkerId, WorkerState.Idle), (worker.Id, worker.Status));
    }

    [Fact]
    public async Task Executor_que_lanza_marca_la_tarea_en_error_y_el_job_como_fallido_sin_rutear()
    {
        CreateRunningJob("job-2");
        await _tasks.EnqueueAsync(NewTask("job-2", PipelineStage.Ingest));
        _ingest.Throw = new InvalidOperationException("csv read failed");

        await ProcessStageAsync(PipelineStage.Ingest);

        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Claimed));
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Done));
        var failed = (await _tasks.GetAsync("task-job-2-ingest", PipelineStage.Ingest))!;
        Assert.Equal(PipelineTaskStatus.Error, failed.Status);
        Assert.Equal("csv read failed", failed.ErrorMessage);

        var job = _jobs.Get("job-2")!;
        Assert.Equal(JobStatus.Error, job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("error", job.StageStatus["ingest"]);
        Assert.Equal("pending", job.StageStatus["sql"]);
        Assert.Equal((EventLevel.Error, "csv read failed"), (job.Events[^1].Level, job.Events[^1].Message));
        var entry = Assert.Single(_activity.Entries);
        Assert.Equal(("job.failed", "error"), (entry.Action, entry.Level));

        Assert.Empty(_router.Routed);
        Assert.Equal(WorkerState.Idle, Assert.Single(_registry.ListAll()).Status);
    }

    [Fact]
    public async Task Sin_tareas_pendientes_no_ejecuta_nada()
    {
        await ProcessStageAsync(PipelineStage.Sql);

        Assert.Empty(AllExecutors.SelectMany(e => e.Tasks));
        Assert.Empty(_router.Routed);
        Assert.Empty(_registry.ListAll());
    }

    [Fact]
    public async Task Cancelacion_por_apagado_no_falla_la_tarea_ni_el_job()
    {
        CreateRunningJob("job-3");
        await _tasks.EnqueueAsync(NewTask("job-3", PipelineStage.Ingest));
        using var shutdown = new CancellationTokenSource();
        _ingest.OnExecute = () =>
        {
            shutdown.Cancel();
            shutdown.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessStageAsync(PipelineStage.Ingest, shutdown.Token));

        // La tarea sigue en claimed/ (se reclamará al vencer el lease) y el job no se marca fallido.
        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Claimed));
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Error));
        Assert.Equal(JobStatus.Running, _jobs.Get("job-3")!.Status);
        Assert.Empty(_router.Routed);
    }

    private IEnumerable<RecordingExecutor> AllExecutors => [_ingest, _sql, _reconcile];

    private RecordingExecutor ExecutorFor(PipelineStage stage) => stage switch
    {
        PipelineStage.Ingest => _ingest,
        PipelineStage.Sql => _sql,
        _ => _reconcile,
    };

    private Task ProcessStageAsync(PipelineStage stage, CancellationToken ct = default) =>
        _host.ProcessStageAsync(stage, WorkerId, ct);

    private void CreateRunningJob(string id) =>
        _jobs.Create(new Job
        {
            Id = id,
            PipelineId = "pipe-1",
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "pending", ["sql"] = "pending", ["reconcile"] = "pending" },
        });

    private static PipelineTask NewTask(string jobId, PipelineStage stage) =>
        new() { Id = $"task-{jobId}-{stage.ToString().ToLowerInvariant()}", Stage = stage, PipelineId = "pipe-1", JobId = jobId };

    // Un mismo tipo hace de executor de los tres stages; cada stage recibe su propia instancia.
    // Un worker recién arrancado y ocioso debe verse en el Panel: antes solo se registraba al
    // reclamar su primera tarea (Touch no crea el fichero) y no aparecía nunca.
    [Fact]
    public async Task Startup_registers_the_worker_as_idle()
    {
        await _host.StartAsync(CancellationToken.None);
        try
        {
            WorkerStatus? status = null;
            for (var i = 0; i < 50 && status is null; i++)
            {
                status = _registry.ListAll().SingleOrDefault(w => w.Id == WorkerId);
                if (status is null)
                {
                    await Task.Delay(100);
                }
            }

            Assert.NotNull(status);
            Assert.Equal(WorkerState.Idle, status.Status);
        }
        finally
        {
            await _host.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingExecutor : IIngestExecutor, ISqlExecutor, IReconcileExecutor
    {
        public List<PipelineTask> Tasks { get; } = [];
        public Exception? Throw { get; set; }
        public Action? OnExecute { get; set; }

        public Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
        {
            Tasks.Add(task);
            OnExecute?.Invoke();
            return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
        }
    }

    private sealed class RecordingRouter : IStageRouter
    {
        public List<PipelineTask> Routed { get; } = [];

        public Task RouteCompletedTaskAsync(PipelineTask task, CancellationToken cancellationToken = default)
        {
            Routed.Add(task);
            return Task.CompletedTask;
        }
    }
}
