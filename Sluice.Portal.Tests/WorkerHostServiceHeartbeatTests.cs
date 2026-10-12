using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests;

// Latido del lease. Antes un solo fallo de HeartbeatAsync salía del bucle (el catch estaba
// fuera del while) y el stage seguía corriendo sin lease: otra réplica lo reclamaba y lo
// ejecutaba en paralelo. Y un latido que devolvía null (la tarea ya no es nuestra) se ignoraba.
public sealed class WorkerHostServiceHeartbeatTests : IDisposable
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(10);
    private readonly string _dataDir;

    public WorkerHostServiceHeartbeatTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-heartbeat-" + Guid.NewGuid().ToString("N"));
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
    public async Task HeartbeatLoop_survives_a_transient_failure_and_keeps_renewing()
    {
        var tasks = new ScriptedHeartbeatTaskStore(failFirst: true, returnNullFrom: int.MaxValue);
        var leaseLost = false;
        using var cts = new CancellationTokenSource();

        var loop = CreateHost(tasks).HeartbeatLoopAsync("task-1", PipelineStage.Ingest, "worker-1", () => leaseLost = true, cts.Token, FastInterval);
        await tasks.WaitForCallsAsync(3);
        cts.Cancel();
        await loop;

        Assert.False(leaseLost);
    }

    [Fact]
    public async Task HeartbeatLoop_reports_lease_lost_when_the_task_is_no_longer_ours()
    {
        var tasks = new ScriptedHeartbeatTaskStore(failFirst: false, returnNullFrom: 2);
        var leaseLost = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await CreateHost(tasks).HeartbeatLoopAsync("task-1", PipelineStage.Ingest, "worker-1", () => leaseLost = true, cts.Token, FastInterval);

        Assert.True(leaseLost);
        Assert.False(cts.IsCancellationRequested); // terminó solo, no por el timeout
    }

    private WorkerHostService CreateHost(ITaskStore tasks)
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        // Las dependencias que el latido no usa van a null: el test ejercita solo ese bucle.
        return new WorkerHostService(
            options,
            tasks,
            null!,
            null!,
            null!,
            null!,
            new WorkerRegistry(options),
            null!,
            new SettingsStore(options),
            null!,
            null!,
            NullLogger<WorkerHostService>.Instance,
            null!);
    }

    // HeartbeatAsync guionizado: opcionalmente falla la primera llamada y devuelve null (tarea
    // perdida) a partir de la llamada N (1-based); el resto renueva el lease.
    private sealed class ScriptedHeartbeatTaskStore(bool failFirst, int returnNullFrom) : ITaskStore
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task WaitForCallsAsync(int expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Calls < expected)
            {
                Assert.True(DateTime.UtcNow < deadline, $"El latido se detuvo tras {Calls} llamada(s).");
                await Task.Delay(10);
            }
        }

        public Task<PipelineTask?> HeartbeatAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (failFirst && call == 1)
            {
                throw new IOException("simulated transient lock");
            }

            return Task.FromResult<PipelineTask?>(call >= returnNullFrom
                ? null
                : new PipelineTask { Id = taskId, Stage = stage, PipelineId = "p", JobId = "j", ClaimedBy = workerId });
        }

        public Task EnqueueAsync(PipelineTask task, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PipelineTask?> TryClaimAsync(PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task<PipelineTask?> GetAsync(string taskId, PipelineStage stage, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task<PipelineTask?> CompleteAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task<PipelineTask?> FailAsync(string taskId, PipelineStage stage, string workerId, string error, CancellationToken cancellationToken = default) => Task.FromResult<PipelineTask?>(null);
        public Task ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public int CountByStatus(PipelineStage stage, PipelineTaskStatus status) => 0;
        public Task<int> CancelJobTasksAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
