using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

public class TaskStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly FixedTimeProvider _time = new();

    public TaskStoreTests()
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

    private TaskStore CreateStore(int leaseSeconds = 120)
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var settings = new SettingsStore(options);
        // El lease de tareas ahora vive en SettingsStore (no en env): se persiste para el test.
        settings.Save(new AppSettings { TaskLeaseSeconds = leaseSeconds });
        return new TaskStore(options, settings, _time);
    }

    private static PipelineTask CreateTask(string id, PipelineStage stage = PipelineStage.Ingest) =>
        new()
        {
            Id = id,
            Stage = stage,
            PipelineId = "pipe-1",
            JobId = "job-1",
        };

    [Fact]
    public async Task EnqueueAsync_writes_pending_task_file()
    {
        var store = CreateStore();
        await store.EnqueueAsync(CreateTask("task-1"));

        var path = Path.Combine(_dataDir, "tasks", "ingest", "pending", "task-1.json");
        Assert.True(File.Exists(path));
        Assert.Equal(1, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
    }

    [Fact]
    public async Task TryClaimAsync_claims_pending_task_atomically()
    {
        var store = CreateStore();
        await store.EnqueueAsync(CreateTask("task-claim"));

        var claimed = await store.TryClaimAsync(PipelineStage.Ingest, "worker-a");

        Assert.NotNull(claimed);
        Assert.Equal("task-claim", claimed!.Id);
        Assert.Equal(PipelineTaskStatus.Claimed, claimed.Status);
        Assert.Equal("worker-a", claimed.ClaimedBy);
        Assert.NotNull(claimed.LeaseExpiresAt);

        Assert.False(File.Exists(Path.Combine(_dataDir, "tasks", "ingest", "pending", "task-claim.json")));
        Assert.True(File.Exists(Path.Combine(_dataDir, "tasks", "ingest", "claimed", "task-claim.json")));
        Assert.Equal(0, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
        Assert.Equal(1, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Claimed));
    }

    [Fact]
    public async Task TryClaimAsync_returns_null_when_second_worker_claims_same_task()
    {
        var store = CreateStore();
        await store.EnqueueAsync(CreateTask("task-single"));

        var first = await store.TryClaimAsync(PipelineStage.Ingest, "worker-a");
        var second = await store.TryClaimAsync(PipelineStage.Ingest, "worker-b");

        Assert.NotNull(first);
        Assert.Null(second);
    }

    // Refuerzo del caso anterior bajo concurrencia REAL (réplicas del mismo stage,
    // T6): N workers compitiendo en paralelo por M tareas. El claim por File.Move
    // atómico debe entregar cada tarea a exactamente un worker, sin duplicados ni
    // pérdidas. El bucle se guía por el conteo global de pendientes para no parar
    // antes de tiempo por un null transitorio.
    [Fact]
    public async Task Concurrent_workers_each_task_claimed_exactly_once()
    {
        var store = CreateStore();
        const int taskCount = 60;
        const int workerCount = 8;

        for (var i = 0; i < taskCount; i++)
        {
            await store.EnqueueAsync(CreateTask($"t-{i}"));
        }

        var claims = new ConcurrentBag<string>();

        async Task Worker(string workerId)
        {
            while (store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending) > 0)
            {
                var task = await store.TryClaimAsync(PipelineStage.Ingest, workerId);
                if (task is not null)
                {
                    claims.Add(task.Id);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(i => Worker($"worker-{i}")));

        Assert.Equal(taskCount, claims.Count);                 // ninguna perdida
        Assert.Equal(taskCount, claims.Distinct().Count());    // ninguna duplicada
        Assert.Equal(0, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
    }

    [Fact]
    public async Task ReclaimExpiredLeasesAsync_moves_expired_task_back_to_pending()
    {
        var store = CreateStore(leaseSeconds: 30);
        await store.EnqueueAsync(CreateTask("task-expire"));
        var claimed = await store.TryClaimAsync(PipelineStage.Ingest, "worker-a");
        Assert.NotNull(claimed);

        _time.Advance(TimeSpan.FromSeconds(31));
        await store.ReclaimExpiredLeasesAsync();

        Assert.Equal(1, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
        Assert.Equal(0, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Claimed));

        var reclaimed = await store.TryClaimAsync(PipelineStage.Ingest, "worker-b");
        Assert.NotNull(reclaimed);
        Assert.Equal("worker-b", reclaimed!.ClaimedBy);
    }

    // Leases vencidos bajo concurrencia REAL: cada TryClaimAsync recupera primero los
    // vencidos, así que N workers compiten por devolver la misma tarea a pending y por
    // volver a reclamarla. Cada tarea debe acabar reclamada exactamente una vez, sin
    // excepciones ni copias sobrantes en pending.
    [Fact]
    public async Task Concurrent_workers_reclaim_expired_leases_exactly_once()
    {
        var store = CreateStore(leaseSeconds: 30);
        const int taskCount = 60;
        const int workerCount = 8;

        for (var i = 0; i < taskCount; i++)
        {
            await store.EnqueueAsync(CreateTask($"t-{i}"));
            Assert.NotNull(await store.TryClaimAsync(PipelineStage.Ingest, "worker-crashed"));
        }

        _time.Advance(TimeSpan.FromSeconds(31));

        var claims = new ConcurrentBag<string>();
        var deadline = DateTime.UtcNow.AddSeconds(30);

        async Task Worker(string workerId)
        {
            while (claims.Count < taskCount && DateTime.UtcNow < deadline)
            {
                var task = await store.TryClaimAsync(PipelineStage.Ingest, workerId);
                if (task is not null)
                {
                    claims.Add(task.Id);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(i => Task.Run(() => Worker($"worker-{i}"))));

        Assert.Equal(taskCount, claims.Count);                 // ninguna perdida
        Assert.Equal(taskCount, claims.Distinct().Count());    // ninguna duplicada
        Assert.Equal(0, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
        Assert.Equal(taskCount, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Claimed));

        for (var i = 0; i < taskCount; i++)
        {
            var stored = await store.GetAsync($"t-{i}", PipelineStage.Ingest);
            Assert.NotEqual("worker-crashed", stored!.ClaimedBy);   // ningún claim vencido filtrado
        }
    }

    [Fact]
    public async Task HeartbeatAsync_extends_lease_for_claiming_worker()
    {
        var store = CreateStore(leaseSeconds: 30);
        await store.EnqueueAsync(CreateTask("task-hb"));
        await store.TryClaimAsync(PipelineStage.Ingest, "worker-a");

        _time.Advance(TimeSpan.FromSeconds(20));
        var updated = await store.HeartbeatAsync("task-hb", PipelineStage.Ingest, "worker-a");
        Assert.NotNull(updated);

        _time.Advance(TimeSpan.FromSeconds(20));
        await store.ReclaimExpiredLeasesAsync();

        Assert.Equal(0, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
        Assert.Equal(1, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Claimed));
    }

    [Fact]
    public async Task CompleteAsync_moves_task_to_done()
    {
        var store = CreateStore();
        await store.EnqueueAsync(CreateTask("task-done"));
        await store.TryClaimAsync(PipelineStage.Ingest, "worker-a");

        var done = await store.CompleteAsync("task-done", PipelineStage.Ingest, "worker-a");

        Assert.NotNull(done);
        Assert.Equal(PipelineTaskStatus.Done, done!.Status);
        Assert.NotNull(done.FinishedAt);
        Assert.Equal(1, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Done));
    }

    [Fact]
    public async Task CancelJobTasksAsync_removes_pending_and_claimed_for_that_job_only()
    {
        var store = CreateStore();
        await store.EnqueueAsync(new PipelineTask { Id = "t-a", Stage = PipelineStage.Ingest, PipelineId = "pipe-1", JobId = "job-1" });
        await store.EnqueueAsync(new PipelineTask { Id = "t-b", Stage = PipelineStage.Sql, PipelineId = "pipe-1", JobId = "job-1" });
        await store.EnqueueAsync(new PipelineTask { Id = "t-other", Stage = PipelineStage.Ingest, PipelineId = "pipe-1", JobId = "job-2" });

        // Claim deja t-a (ordinal primero en ingest) en estado Claimed.
        await store.TryClaimAsync(PipelineStage.Ingest, "worker-a");

        var removed = await store.CancelJobTasksAsync("job-1");

        Assert.Equal(2, removed);                                                                  // t-a (claimed) + t-b (pending)
        Assert.Equal(0, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Claimed));
        Assert.Equal(0, store.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
        Assert.Equal(1, store.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));    // job-2 intacto
    }

    [Fact]
    public async Task FailAsync_moves_task_to_error()
    {
        var store = CreateStore();
        await store.EnqueueAsync(CreateTask("task-fail"));
        await store.TryClaimAsync(PipelineStage.Ingest, "worker-a");

        var failed = await store.FailAsync("task-fail", PipelineStage.Ingest, "worker-a", "boom");

        Assert.NotNull(failed);
        Assert.Equal(PipelineTaskStatus.Error, failed!.Status);
        Assert.Equal("boom", failed.ErrorMessage);
    }
}