using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

public sealed class JobStageTimingsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir;
    private readonly JobService _jobs;

    public JobStageTimingsTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-stage-timings-" + Guid.NewGuid().ToString("N"));
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
    public void Set_ReturnsCopy_AndPreservesOtherStages()
    {
        var original = new Dictionary<string, StageTiming>
        {
            ["ingest"] = new() { StartedAt = T0, FinishedAt = T0.AddMinutes(1) },
        };

        var updated = original.Set("sql", started: T0.AddMinutes(2));

        Assert.NotSame(original, updated);
        Assert.False(original.ContainsKey("sql"));
        Assert.Equal(T0.AddMinutes(1), updated["ingest"].FinishedAt);
        Assert.Equal(T0.AddMinutes(2), updated["sql"].StartedAt);
        Assert.Null(updated["sql"].FinishedAt);
    }

    [Fact]
    public void Set_FinishedOnly_KeepsStartedAt()
    {
        var timings = new Dictionary<string, StageTiming>().Set("sql", started: T0);

        var finished = timings.Set("sql", finished: T0.AddSeconds(30));

        Assert.Equal(T0, finished["sql"].StartedAt);
        Assert.Equal(T0.AddSeconds(30), finished["sql"].FinishedAt);
    }

    [Fact]
    public void Set_StartedAgain_ClearsPreviousFinishedAt()
    {
        // Un stage reintentado (tarea reclamada de nuevo tras caerse un worker) vuelve a empezar:
        // el FinishedAt del intento anterior ya no describe esta ejecución.
        var timings = new Dictionary<string, StageTiming>().Set("sql", started: T0, finished: T0.AddSeconds(5));

        var restarted = timings.Set("sql", started: T0.AddMinutes(1));

        Assert.Equal(T0.AddMinutes(1), restarted["sql"].StartedAt);
        Assert.Null(restarted["sql"].FinishedAt);
    }

    // El patrón real de los executors: leen el job una vez, anotan el inicio con esa copia y,
    // en una salida temprana, vuelven a guardar LA MISMA copia (obsoleta) con el estado final.
    // Como Update guarda el documento completo, el inicio solo sobrevive si la salida temprana
    // lo vuelve a escribir junto al fin.
    [Fact]
    public void StaleCopyPattern_EarlyExitKeepsStartAndFinish_AndFreshUpdatePreservesThem()
    {
        const string jobId = "job-stale-1";
        _jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = "pipe-1",
            StageStatus = new Dictionary<string, string> { ["ingest"] = "pending" },
        });

        var stale = _jobs.Get(jobId)!;
        var stageStart = T0;
        _jobs.AppendEvent(jobId, EventLevel.Info, JobEventMarkers.IngestStarted);
        _jobs.Update(stale.Id, stale with
        {
            StageStatus = new Dictionary<string, string>(stale.StageStatus) { ["ingest"] = "running" },
            StageTimings = stale.StageTimings.Set("ingest", started: stageStart),
        });

        // Salida temprana con la copia obsoleta (no ve el Update anterior).
        var finishedAt = T0.AddSeconds(3);
        _jobs.AppendEvent(jobId, EventLevel.Info, JobEventMarkers.NoNewSnapshots);
        _jobs.Update(stale.Id, stale with
        {
            StageStatus = new Dictionary<string, string>(stale.StageStatus) { ["ingest"] = "done" },
            StageTimings = stale.StageTimings.Set("ingest", started: stageStart, finished: finishedAt),
        });

        var afterExit = _jobs.Get(jobId)!;
        Assert.Equal(stageStart, afterExit.StageTimings["ingest"].StartedAt);
        Assert.Equal(finishedAt, afterExit.StageTimings["ingest"].FinishedAt);
        Assert.Equal(2, afterExit.Events.Count);

        // Un escritor posterior que relee (StageRouter, cancelación, watchdog) no toca
        // StageTimings: el record lo arrastra intacto.
        var fresh = _jobs.Get(jobId)!;
        _jobs.Update(fresh.Id, fresh with { CurrentStage = PipelineStage.Sql });

        var final = _jobs.Get(jobId)!;
        Assert.Equal(stageStart, final.StageTimings["ingest"].StartedAt);
        Assert.Equal(finishedAt, final.StageTimings["ingest"].FinishedAt);
    }

    [Fact]
    public void JobWithoutStageTimings_DeserializesWithEmptyDictionary()
    {
        // Jobs guardados antes de existir el campo.
        const string json = """{ "id": "job-old", "pipelineId": "pipe-1", "status": "ok" }""";

        var job = System.Text.Json.JsonSerializer.Deserialize<Job>(json, JsonDefaults.Options)!;

        Assert.NotNull(job.StageTimings);
        Assert.Empty(job.StageTimings);
    }
}
