using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Analytics;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// GetTableGrowth reconstruye el crecimiento de una tabla desde Job.Entities histórico (no hay
// snapshots diarios dedicados) — SqlRows (COUNT(*) real) para el eje de crecimiento, CsvRows
// (filas ingeridas) para detectar días con picos de carga.
public sealed class AnalyticsServiceGetTableGrowthTests : IDisposable
{
    private readonly string _dataDir;

    public AnalyticsServiceGetTableGrowthTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-analytics-growth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        DataDirectoryInitializer.EnsureLayout(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void GetTableGrowth_UsesLastSqlRowsPerDay_AndRanksSpikesByCsvRows()
    {
        const string pipelineId = "pipe-1";
        const string entity = "account";

        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var jobs = new JobService(new JobStore(options));

        // Mediodía UTC de hoy: con UtcNow a secas, entre las 00:00 y las 01:00 UTC "day2Morning"
        // (-2 días -1 h) caía en el día anterior y el test fallaba según la hora de ejecución.
        var now = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(12), TimeSpan.Zero);
        var day1 = now.AddDays(-3);
        var day2Morning = now.AddDays(-2).AddHours(-1);
        var day2Evening = now.AddDays(-2);
        var day3 = now.AddDays(-1);
        var tooOld = now.AddDays(-200); // fuera de la ventana de 90 días — no debe contar

        jobs.Create(MakeJob("job-day1", pipelineId, day1, entity, sqlRows: 100, csvRows: 50));
        jobs.Create(MakeJob("job-day2-am", pipelineId, day2Morning, entity, sqlRows: 150, csvRows: 30));
        jobs.Create(MakeJob("job-day2-pm", pipelineId, day2Evening, entity, sqlRows: 180, csvRows: 70));
        jobs.Create(MakeJob("job-day3", pipelineId, day3, entity, sqlRows: 200, csvRows: 200));
        jobs.Create(MakeJob("job-too-old", pipelineId, tooOld, entity, sqlRows: 999, csvRows: 999));
        jobs.Create(MakeJob("job-other-entity", pipelineId, day3, "contact", sqlRows: 5000, csvRows: 5000));

        var service = new AnalyticsService(jobs, new ThrowingOrchestrationService(), new ThrowingPipelineService());

        var report = service.GetTableGrowth(pipelineId, entity, days: 90);

        Assert.Equal(3, report.Growth.Count);
        Assert.Equal(100, report.Growth[0].RowCount);
        Assert.Equal(180, report.Growth[1].RowCount); // día 2: se queda con el job de la tarde (último)
        Assert.Equal(200, report.Growth[2].RowCount);
        Assert.Equal(200, report.CurrentRowCount); // último punto de la serie

        Assert.Equal(350, report.TotalRowsLoadedInWindow); // 50 + (30+70) + 200, sin contar el viejo ni otra entidad

        var topSpike = Assert.Single(report.TopSpikeDays, s => s.CsvRows == 200);
        Assert.Equal(DateOnly.FromDateTime(day3.UtcDateTime), topSpike.Date);
        // Orden descendente por carga.
        Assert.True(report.TopSpikeDays[0].CsvRows >= report.TopSpikeDays[^1].CsvRows);
    }

    [Fact]
    public void GetTableGrowth_NoJobsForEntity_ReturnsEmptyReport()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var jobs = new JobService(new JobStore(options));
        var service = new AnalyticsService(jobs, new ThrowingOrchestrationService(), new ThrowingPipelineService());

        var report = service.GetTableGrowth("pipe-empty", "nonexistent", days: 30);

        Assert.Empty(report.Growth);
        Assert.Empty(report.DailyLoad);
        Assert.Empty(report.TopSpikeDays);
        Assert.Equal(0, report.CurrentRowCount);
        Assert.Equal(0, report.TotalRowsLoadedInWindow);
    }

    private static Job MakeJob(string id, string pipelineId, DateTimeOffset startedAt, string entity, int sqlRows, int csvRows) => new()
    {
        Id = id,
        PipelineId = pipelineId,
        Status = JobStatus.Ok,
        StartedAt = startedAt,
        FinishedAt = startedAt.AddMinutes(5),
        Entities =
        [
            new JobEntityResult
            {
                Entity = entity,
                CsvRows = csvRows,
                SqlRows = sqlRows,
                Status = "ok",
            },
        ],
    };

    // Un Partial sin FinishedAt sigue en curso (ingest parcial que continúa con SQL): no es un
    // éxito del día, igual que ya lo excluye la tasa de éxito (IsTerminal).
    [Fact]
    public void GetSnapshot_JobsPerDay_UnfinishedPartialIsNotASuccess()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var jobs = new JobService(new JobStore(options));
        var today = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(1), TimeSpan.Zero);
        jobs.Create(new Job { Id = "job-ok", PipelineId = "pipe-1", Status = JobStatus.Ok, StartedAt = today, FinishedAt = today.AddMinutes(5) });
        jobs.Create(new Job { Id = "job-partial-done", PipelineId = "pipe-1", Status = JobStatus.Partial, StartedAt = today, FinishedAt = today.AddMinutes(5) });
        jobs.Create(new Job { Id = "job-partial-live", PipelineId = "pipe-1", Status = JobStatus.Partial, StartedAt = today });

        var snapshot = new AnalyticsService(jobs, new ThrowingOrchestrationService(), new ThrowingPipelineService()).GetSnapshot();

        var day = snapshot.JobsPerDay.Single(d => d.Date == DateOnly.FromDateTime(today.UtcDateTime));
        Assert.Equal(3, day.Total);
        Assert.Equal(2, day.Success);
    }

    // Filtro por pipeline de /analytics: los indicadores de jobs solo cuentan los de ese pipeline.
    [Fact]
    public void GetSnapshot_WithPipeline_OnlyCountsThatPipelinesJobs()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var jobs = new JobService(new JobStore(options));
        var today = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(1), TimeSpan.Zero);
        jobs.Create(new Job { Id = "job-a1", PipelineId = "pipe-a", Status = JobStatus.Ok, StartedAt = today, FinishedAt = today.AddMinutes(5) });
        jobs.Create(new Job { Id = "job-a2", PipelineId = "pipe-a", Status = JobStatus.Error, StartedAt = today, FinishedAt = today.AddMinutes(5) });
        jobs.Create(new Job { Id = "job-b1", PipelineId = "pipe-b", Status = JobStatus.Ok, StartedAt = today, FinishedAt = today.AddMinutes(5) });
        var service = new AnalyticsService(jobs, new ThrowingOrchestrationService(), new ThrowingPipelineService());

        var all = service.GetSnapshot();
        var onlyA = service.GetSnapshot("pipe-a");
        var onlyB = service.GetSnapshot("pipe-b");

        Assert.Equal(3, all.TotalJobs);
        Assert.Equal(2, onlyA.TotalJobs);
        Assert.Equal(50, onlyA.SuccessRatePct);
        Assert.Equal(1, onlyB.TotalJobs);
        Assert.Equal(100, onlyB.SuccessRatePct);
        Assert.Equal(all.TotalJobs, service.GetSnapshot("").TotalJobs);
    }

    private sealed class ThrowingOrchestrationService : IOrchestrationService
    {
        public object GetMode() => throw new NotSupportedException();
        public object GetQueueCounts() => throw new NotSupportedException();
        public QueueSnapshot GetQueueSnapshot() => throw new NotSupportedException();
        public IReadOnlyList<WorkerStatus> GetWorkers() => throw new NotSupportedException();
        public IReadOnlyList<Job> GetActiveJobs() => throw new NotSupportedException();
        public Task<Job> RunPipelineAsync(string pipelineId, JobType type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Job> RetryJobAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Job> ResyncTableAsync(string pipelineId, string entity, ResyncMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Job> ReloadFromParquetAsync(string pipelineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingPipelineService : IPipelineService
    {
        public IReadOnlyList<Pipeline> List() => throw new NotSupportedException();
        public Pipeline? Get(string id) => throw new NotSupportedException();
        public Pipeline Create(Pipeline pipeline) => throw new NotSupportedException();
        public Pipeline? Update(string id, Pipeline pipeline) => throw new NotSupportedException();
        public bool Delete(string id, bool wipeState, out string? error) => throw new NotSupportedException();
        public PipelineState GetState(string pipelineId) => throw new NotSupportedException();
        public IReadOnlyList<object> GetTables(string pipelineId) => throw new NotSupportedException();
    }
}
