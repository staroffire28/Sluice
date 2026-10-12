using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests.Fakes;

public sealed class MonolithFakeIngestExecutor : IIngestExecutor
{
    private readonly IJobService _jobs;
    private readonly IngestManifestStore _manifests;

    public MonolithFakeIngestExecutor(IJobService jobs, IngestManifestStore manifests)
    {
        _jobs = jobs;
        _manifests = manifests;
    }

    public Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(task.JobId)
            ?? throw new InvalidOperationException($"Job '{task.JobId}' not found.");

        _jobs.AppendEvent(task.JobId, EventLevel.Info, "Fake ingest completed.");
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["ingest"] = "done" },
        });
        _manifests.Save(new IngestManifest
        {
            JobId = task.JobId,
            PipelineId = task.PipelineId,
            LastProcessedSnapshot = "2026-06-08T10.00.00Z",
        });
        return Task.CompletedTask;
    }
}

public sealed class MonolithFakeSqlExecutor : ISqlExecutor
{
    private readonly IJobService _jobs;

    public MonolithFakeSqlExecutor(IJobService jobs) => _jobs = jobs;

    public Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(task.JobId)
            ?? throw new InvalidOperationException($"Job '{task.JobId}' not found.");

        _jobs.AppendEvent(task.JobId, EventLevel.Info, "Fake SQL completed.");
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["sql"] = "done" },
        });
        return Task.CompletedTask;
    }
}

public sealed class MonolithFakeReconcileExecutor : IReconcileExecutor
{
    private readonly IJobService _jobs;

    public MonolithFakeReconcileExecutor(IJobService jobs) => _jobs = jobs;

    public Task ExecuteAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(task.JobId)
            ?? throw new InvalidOperationException($"Job '{task.JobId}' not found.");

        _jobs.AppendEvent(task.JobId, EventLevel.Info, "Fake reconcile completed.");
        _jobs.Update(job.Id, job with
        {
            StageStatus = new Dictionary<string, string>(job.StageStatus) { ["reconcile"] = "done" },
        });
        return Task.CompletedTask;
    }
}