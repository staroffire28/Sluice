using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests;

public class WorkerHostServiceAlertTests : IDisposable
{
    private readonly string _dataDir;
    private readonly RecordingHttpHandler _handler = new();

    public WorkerHostServiceAlertTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-portal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task MarkJobFailedAsync_SendsPipelineFailureWebhook()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var settings = new SettingsStore(options);
        settings.Save(new AppSettings
        {
            AlertEnabled = true,
            AlertWebhookUrl = "https://logic.example/hook",
        });

        var jobs = new JobService(new JobStore(options));
        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline
        {
            Id = "pipe-1",
            Name = "Sales",
            SourceId = "src-1",
            DestinationId = "dst-1",
        });

        jobs.Create(new Job
        {
            Id = "job-1",
            PipelineId = "pipe-1",
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "running" },
        });

        var alerts = new AlertService(
            options,
            settings,
            NullLogger<AlertService>.Instance,
            new TestHttpClientFactory(_handler));

        var host = new WorkerHostService(
            options,
            new StubTaskStore(),
            new StubStageRouter(),
            new StubIngestExecutor(),
            new StubSqlExecutor(),
            new StubReconcileExecutor(),
            new WorkerRegistry(options),
            jobs,
            settings,
            pipelines,
            alerts,
            NullLogger<WorkerHostService>.Instance,
            new ActivityLogStore(options));

        await host.MarkJobFailedAsync("job-1", PipelineStage.Ingest, "csv read failed", CancellationToken.None);

        Assert.Equal(1, _handler.RequestCount);
        Assert.Contains("pipeline.failure", _handler.LastBody);
        Assert.Contains("pipe-1", _handler.LastBody);
        Assert.Contains("job-1", _handler.LastBody);

        var job = jobs.Get("job-1");
        Assert.Equal(JobStatus.Error, job!.Status);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TestHttpClientFactory(RecordingHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubTaskStore : ITaskStore
    {
        public Task EnqueueAsync(PipelineTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PipelineTask?> TryClaimAsync(PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task<PipelineTask?> GetAsync(string taskId, PipelineStage stage, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task<PipelineTask?> HeartbeatAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task<PipelineTask?> CompleteAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task<PipelineTask?> FailAsync(string taskId, PipelineStage stage, string workerId, string error, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int CountByStatus(PipelineStage stage, PipelineTaskStatus status) => 0;
        public Task<int> CancelJobTasksAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class StubStageRouter : IStageRouter
    {
        public Task RouteCompletedTaskAsync(PipelineTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubIngestExecutor : IIngestExecutor
    {
        public Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubSqlExecutor : ISqlExecutor
    {
        public Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubReconcileExecutor : IReconcileExecutor
    {
        public Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}