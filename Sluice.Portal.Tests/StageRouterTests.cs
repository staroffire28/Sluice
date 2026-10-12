using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests;

// El router leía el job (Running), y si el usuario lo cancelaba antes de que el router lo
// guardara, su Update lo revivía y encolaba el siguiente stage.
public sealed class StageRouterTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-router-" + Guid.NewGuid().ToString("N"));
    private readonly JobService _jobs;

    public StageRouterTests()
    {
        Directory.CreateDirectory(_dataDir);
        _jobs = new JobService(new JobStore(Options.Create(new AppOptions { DataDir = _dataDir })));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Ingest_completado_encola_el_stage_SQL()
    {
        var job = _jobs.Create(new Job { Id = "job-r1", PipelineId = "p1", Status = JobStatus.Running });
        var tasks = new RecordingTaskStore();

        await new StageRouter(tasks, _jobs).RouteCompletedTaskAsync(IngestTask(job.Id));

        Assert.Equal(PipelineStage.Sql, Assert.Single(tasks.Enqueued).Stage);
    }

    // Un ingest con entidades fallidas (pero alguna sana) deja el job en Partial e ingest="error"
    // y el pipeline sigue con SQL. El router forzaba ingest="done" y ocultaba el fallo (en la UI
    // y en RetryJobAsync, que con ingest "done" + sql "error" recarga desde parquet aunque el
    // snapshot fallido no se marcó como procesado).
    [Fact]
    public async Task Ingest_parcial_conserva_el_estado_error_de_la_etapa()
    {
        var job = _jobs.Create(new Job
        {
            Id = "job-r3",
            PipelineId = "p1",
            Status = JobStatus.Partial,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "error", ["sql"] = "pending", ["reconcile"] = "pending" },
        });
        var tasks = new RecordingTaskStore();

        await new StageRouter(tasks, _jobs).RouteCompletedTaskAsync(IngestTask(job.Id));

        Assert.Equal(PipelineStage.Sql, Assert.Single(tasks.Enqueued).Stage);
        var routed = _jobs.Get(job.Id)!;
        Assert.Equal("error", routed.StageStatus["ingest"]);
        Assert.Equal("pending", routed.StageStatus["sql"]);
        Assert.Equal(PipelineStage.Sql, routed.CurrentStage);
    }

    [Fact]
    public async Task Job_cancelado_mientras_se_rutea_no_encola_el_siguiente_stage()
    {
        var stale = _jobs.Create(new Job { Id = "job-r2", PipelineId = "p1", Status = JobStatus.Running });
        _jobs.Update(stale.Id, stale with { Status = JobStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow });
        var tasks = new RecordingTaskStore();

        // El router "lee" la copia de antes de cancelar (la carrera real).
        await new StageRouter(tasks, new StaleReadJobService(_jobs, stale)).RouteCompletedTaskAsync(IngestTask(stale.Id));

        Assert.Empty(tasks.Enqueued);
        Assert.Equal(JobStatus.Cancelled, _jobs.Get(stale.Id)!.Status);
    }

    private static PipelineTask IngestTask(string jobId) => new()
    {
        Id = $"task-{jobId}-ingest",
        Stage = PipelineStage.Ingest,
        PipelineId = "p1",
        JobId = jobId,
    };

    private sealed class StaleReadJobService(IJobService inner, Job stale) : IJobService
    {
        public Job? Get(string id) => id == stale.Id ? stale : inner.Get(id);
        public Job? Update(string id, Job job) => inner.Update(id, job);
        public IReadOnlyList<Job> List(int page = 1, int pageSize = 20, string? pipelineId = null, JobStatus? status = null, DateRange? range = null) =>
            inner.List(page, pageSize, pipelineId, status, range);
        public int Count(string? pipelineId = null, JobStatus? status = null, DateRange? range = null) => inner.Count(pipelineId, status, range);
        public IReadOnlyList<Job> ListRunning() => inner.ListRunning();
        public Job Create(Job job) => inner.Create(job);
        public void AppendEvent(string jobId, EventLevel level, string message) => inner.AppendEvent(jobId, level, message);
    }

    private sealed class RecordingTaskStore : ITaskStore
    {
        public List<PipelineTask> Enqueued { get; } = [];

        public Task EnqueueAsync(PipelineTask task, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(task);
            return Task.CompletedTask;
        }

        public Task<PipelineTask?> TryClaimAsync(PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PipelineTask?> GetAsync(string taskId, PipelineStage stage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PipelineTask?> HeartbeatAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PipelineTask?> CompleteAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PipelineTask?> FailAsync(string taskId, PipelineStage stage, string workerId, string error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public int CountByStatus(PipelineStage stage, PipelineTaskStatus status) => throw new NotSupportedException();
        public Task<int> CancelJobTasksAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
