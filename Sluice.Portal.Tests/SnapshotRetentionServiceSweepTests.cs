using Microsoft.Extensions.Logging.Abstractions;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

public sealed class SnapshotRetentionServiceSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SweepAsync_InterruptsDelete_WhenJobStartsRunningMidLoop()
    {
        const int k = SnapshotRetentionService.KeepLastProcessedSnapshots;
        var snapshots = Enumerable.Range(1, k + 3)
            .Select(i => $"2026-05-{i:D2}T08.00.00Z")
            .ToList();
        snapshots.Add("2026-06-24T08.00.00Z");
        var lastProcessed = snapshots[^2];

        var jobs = new RunningJobAwareJobService();
        var adls = new StubAdlsClient(snapshots, lastProcessed, jobs);
        var pipelines = new StubPipelineService(
        [
            new Pipeline
            {
                Id = "pipe-1",
                Name = "Sales",
                SourceId = "src-1",
                DestinationId = "dst-1",
                Enabled = true,
            },
        ],
        new PipelineState
        {
            PipelineId = "pipe-1",
            LastProcessedSnapshot = lastProcessed,
        });

        var service = new SnapshotRetentionService(
            pipelines,
            jobs,
            new StubAdlsClientFactory(adls),
            new NoopActivityLog(),
            NullLogger<SnapshotRetentionService>.Instance);

        jobs.SimulateRunningAfterDeletes = 1;

        var result = await service.SweepAsync(
            Now,
            new AppSettings
            {
                SourceSnapshotRetentionDays = 7,
                SourceSnapshotRetentionDryRun = false,
                SourceSnapshotRetentionRequireParquet = false,
            });

        Assert.Equal(1, result.Deleted);
        Assert.Equal(["2026-05-01T08.00.00Z"], adls.DeletedFolders);
    }

    private sealed class RunningJobAwareJobService : IJobService
    {
        public int SimulateRunningAfterDeletes { get; set; }
        private int _deleteObservations;

        public void RecordDeleteAttempt() => _deleteObservations++;

        public IReadOnlyList<Job> ListRunning()
        {
            if (_deleteObservations >= SimulateRunningAfterDeletes && SimulateRunningAfterDeletes > 0)
            {
                return
                [
                    new Job
                    {
                        Id = "job-run",
                        PipelineId = "pipe-1",
                        Type = JobType.Incremental,
                        Status = JobStatus.Running,
                    },
                ];
            }

            return [];
        }

        public Job Create(Job job) => throw new NotSupportedException();
        public Job? Get(string id) => throw new NotSupportedException();
        public IReadOnlyList<Job> List(int page = 1, int pageSize = 20, string? pipelineId = null, JobStatus? status = null, DateRange? range = null) =>
            throw new NotSupportedException();
        public int Count(string? pipelineId = null, JobStatus? status = null, DateRange? range = null) => throw new NotSupportedException();
        public Job? Update(string id, Job job) => throw new NotSupportedException();
        public void AppendEvent(string jobId, EventLevel level, string message) => throw new NotSupportedException();
    }

    private sealed class StubPipelineService(IReadOnlyList<Pipeline> pipelines, PipelineState state) : IPipelineService
    {
        public IReadOnlyList<Pipeline> List() => pipelines;
        public Pipeline? Get(string id) => pipelines.FirstOrDefault(p => p.Id == id);
        public Pipeline Create(Pipeline pipeline) => throw new NotSupportedException();
        public Pipeline? Update(string id, Pipeline pipeline) => throw new NotSupportedException();
        public bool Delete(string id, bool wipeState, out string? error) => throw new NotSupportedException();
        public PipelineState GetState(string pipelineId) => state;
        public IReadOnlyList<object> GetTables(string pipelineId) => [];
    }

    private sealed class StubAdlsClient(
        IReadOnlyList<string> snapshots,
        string? settled,
        RunningJobAwareJobService jobs) : IAdlsClient
    {
        public List<string> DeletedFolders { get; } = [];

        public Task<string?> GetSettledSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(settled);

        public Task<IReadOnlyList<string>> ListSnapshotFoldersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(snapshots);

        public Task DeleteSnapshotFolderAsync(string snapshotFolder, CancellationToken cancellationToken = default)
        {
            jobs.RecordDeleteAttempt();
            DeletedFolders.Add(snapshotFolder);
            return Task.CompletedTask;
        }

        public Task TestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DateTimeOffset?> GetChangelogLastModifiedAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);
        public Task<SnapshotIntegrity> ValidateSnapshotAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnapshotIntegrity(true, null, null));
        public Task<IReadOnlyList<string>> ListEntitiesAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<CdmEntitySchema> GetEntitySchemaAsync(string entity, string snapshot, string? settledSnapshot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamEntityCsvLinesAsync(string snapshot, string entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string?> ReadGlobalOptionsetMetadataCsvAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class StubAdlsClientFactory(StubAdlsClient client) : IAdlsClientFactory
    {
        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAdlsClient>(client);

        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IParquetStore>(new EmptyParquetStore());

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "ok"));
    }

    private sealed class EmptyParquetStore : IParquetStore
    {
        public Task<bool> ParquetExistsAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ParquetArtifact>>([]);

        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("{}");

        public Task<Stream> OpenParquetReadAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UploadParquetAsync(string blobPath, Stream content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopActivityLog : IActivityLog
    {
        public void LogSystem(string type, string message, string? pipelineId = null, string? jobId = null, string level = "info") { }
        public void LogUser(string type, string message, string? pipelineId = null, string? jobId = null, string level = "info") { }
        public ActivityLogPage QueryPage(ActivityLogFilter filter, int page, int pageSize) =>
            new([], 0, page);

        public IReadOnlyList<ActivityLogEntry> Query(ActivityLogFilter filter) => [];
    }
}