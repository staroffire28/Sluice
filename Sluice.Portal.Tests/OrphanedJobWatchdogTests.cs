using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Un worker que cae entre pasos no atómicos (complete -> route, create -> enqueue) dejaba el
// job en Running sin ninguna tarea viva: bloqueaba para siempre el Run manual y el scheduler
// del pipeline. El vigilante lo marca como Error para que se pueda reintentar.
public sealed class OrphanedJobWatchdogTests : IDisposable
{
    private readonly string _dataDir;
    private readonly JobService _jobs;
    private readonly TaskStore _tasks;
    private readonly OrphanedJobWatchdog _watchdog;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public OrphanedJobWatchdogTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-watchdog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        _jobs = new JobService(new JobStore(options));
        _tasks = new TaskStore(options, new SettingsStore(options));
        _watchdog = new OrphanedJobWatchdog(_jobs, _tasks);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Running_job_without_live_tasks_and_no_recent_activity_is_marked_error()
    {
        CreateJob("job-orphan", JobStatus.Running, startedAgo: TimeSpan.FromMinutes(30));

        var marked = await _watchdog.SweepAsync(_now);

        Assert.Equal(1, marked);
        var job = _jobs.Get("job-orphan")!;
        Assert.Equal(JobStatus.Error, job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Contains(job.Events, e => e.Level == EventLevel.Error);
    }

    [Fact]
    public async Task Running_job_with_a_pending_task_is_left_alone()
    {
        CreateJob("job-queued", JobStatus.Running, startedAgo: TimeSpan.FromMinutes(30));
        await _tasks.EnqueueAsync(new PipelineTask
        {
            Id = "task-job-queued-sql",
            Stage = PipelineStage.Sql,
            PipelineId = "pipe-1",
            JobId = "job-queued",
        });

        Assert.Equal(0, await _watchdog.SweepAsync(_now));
        Assert.Equal(JobStatus.Running, _jobs.Get("job-queued")!.Status);
    }

    [Fact]
    public async Task Running_job_with_recent_activity_is_left_alone()
    {
        // Ventana normal entre pasos (create -> enqueue, complete -> route): sin tareas un instante.
        CreateJob("job-fresh", JobStatus.Running, startedAgo: TimeSpan.FromMinutes(2));
        CreateJob("job-busy", JobStatus.Running, startedAgo: TimeSpan.FromHours(3), lastEventAgo: TimeSpan.FromMinutes(1));

        Assert.Equal(0, await _watchdog.SweepAsync(_now));
        Assert.Equal(JobStatus.Running, _jobs.Get("job-fresh")!.Status);
        Assert.Equal(JobStatus.Running, _jobs.Get("job-busy")!.Status);
    }

    // Un ingest parcial deja el job en Partial sin FinishedAt y el pipeline sigue: si el worker
    // cae entre etapas también queda huérfano. Se cierra como Error (no llegó a terminar) para
    // desbloquear el pipeline y poder reintentarlo.
    [Fact]
    public async Task Unfinished_partial_job_without_live_tasks_is_marked_error()
    {
        CreateJob("job-partial-orphan", JobStatus.Partial, startedAgo: TimeSpan.FromMinutes(30));

        Assert.Equal(1, await _watchdog.SweepAsync(_now));
        var job = _jobs.Get("job-partial-orphan")!;
        Assert.Equal(JobStatus.Error, job.Status);
        Assert.NotNull(job.FinishedAt);
    }

    // El Partial en curso con su tarea reclamada (SQL largo tras un ingest parcial) no es huérfano.
    [Fact]
    public async Task Unfinished_partial_job_with_a_claimed_task_is_left_alone()
    {
        CreateJob("job-partial-busy", JobStatus.Partial, startedAgo: TimeSpan.FromMinutes(30));
        await _tasks.EnqueueAsync(new PipelineTask
        {
            Id = "task-job-partial-busy-sql",
            Stage = PipelineStage.Sql,
            PipelineId = "pipe-1",
            JobId = "job-partial-busy",
        });
        Assert.NotNull(await _tasks.TryClaimAsync(PipelineStage.Sql, "worker-1"));

        Assert.Equal(0, await _watchdog.SweepAsync(_now));
        var job = _jobs.Get("job-partial-busy")!;
        Assert.Equal(JobStatus.Partial, job.Status);
        Assert.Null(job.FinishedAt);
    }

    [Fact]
    public async Task Finished_jobs_are_never_touched()
    {
        CreateJob("job-done", JobStatus.Ok, startedAgo: TimeSpan.FromHours(5));

        Assert.Equal(0, await _watchdog.SweepAsync(_now));
        Assert.Equal(JobStatus.Ok, _jobs.Get("job-done")!.Status);
    }

    private void CreateJob(string id, JobStatus status, TimeSpan startedAgo, TimeSpan? lastEventAgo = null) =>
        _jobs.Create(new Job
        {
            Id = id,
            PipelineId = "pipe-1",
            Type = JobType.Incremental,
            Status = status,
            CurrentStage = PipelineStage.Sql,
            StartedAt = _now - startedAgo,
            Events = lastEventAgo is { } ago
                ? [new JobEvent { At = _now - ago, Level = EventLevel.Info, Message = "progreso" }]
                : [],
        });
}
