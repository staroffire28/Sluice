using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

public sealed class JobProgressStoreTests : IDisposable
{
    private const string PipelineId = "pipe-1";
    private const string JobId = "job-1";

    private readonly string _dataDir;
    private readonly FixedTimeProvider _clock = new();
    private readonly JobProgressStore _store;

    public JobProgressStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _store = new JobProgressStore(
            new IngestManifestStore(Options.Create(new AppOptions { DataDir = _dataDir })),
            _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void GetPath_IsInsideTheJobRunDirectory()
    {
        Assert.Equal(
            Path.Combine(_dataDir, PipelineId, "runs", JobId, "progress.json"),
            _store.GetPath(PipelineId, JobId));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.Load(PipelineId, JobId));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        var path = _store.GetPath(PipelineId, JobId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.Null(_store.Load(PipelineId, JobId));
    }

    [Fact]
    public void Begin_StartStep_CompleteStep_RoundTrips()
    {
        var progress = _store.Begin(PipelineId, JobId, "sql");
        progress.StartStep(JobStepCatalog.Prepare);
        _clock.Advance(TimeSpan.FromSeconds(2));
        progress.CompleteStep(JobStepCatalog.Prepare);
        progress.StartStep(JobStepCatalog.Merge, total: 3);

        var loaded = _store.Load(PipelineId, JobId);

        Assert.NotNull(loaded);
        Assert.Equal(JobId, loaded!.JobId);
        Assert.Equal("sql", loaded.Stage);
        Assert.Equal(JobStepCatalog.Merge, loaded.CurrentStep);
        var prepare = loaded.Steps[JobStepCatalog.Prepare];
        Assert.Equal("done", prepare.Status);
        Assert.Equal(_clock.GetUtcNow().AddSeconds(-2), prepare.StartedAt);
        Assert.Equal(_clock.GetUtcNow(), prepare.FinishedAt);
        var merge = loaded.Steps[JobStepCatalog.Merge];
        Assert.Equal("running", merge.Status);
        Assert.Equal(3, merge.Total);
    }

    [Fact]
    public void Report_WithoutForce_IsThrottledToOneWritePerSecond()
    {
        var progress = _store.Begin(PipelineId, JobId, "ingest");
        progress.StartStep(JobStepCatalog.Snapshots, total: 10);

        // Solo cambia el contador (mismo elemento, misma fase): se acumula sin volcar.
        progress.Report(JobStepCatalog.Snapshots, 1);
        Assert.Equal(0, _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Snapshots].Done);

        _clock.Advance(TimeSpan.FromSeconds(1));
        progress.Report(JobStepCatalog.Snapshots, 2);
        Assert.Equal(2, _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Snapshots].Done);

        // force escribe aunque no haya pasado el segundo.
        progress.Report(JobStepCatalog.Snapshots, 3, force: true);
        Assert.Equal(3, _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Snapshots].Done);
    }

    // El throttle es solo para contadores: si cambia la fase o el elemento en curso se vuelca ya,
    // si no la UI se quedaba minutos mostrando "ensureTable" durante un MERGE largo.
    [Fact]
    public void Report_PhaseOrItemChange_IsWrittenEvenWithinTheSameSecond()
    {
        var progress = _store.Begin(PipelineId, JobId, "sql");
        progress.StartStep(JobStepCatalog.Merge, total: 2);
        progress.Report(JobStepCatalog.Merge, 0, item: "account", phase: "ensureTable", force: true);

        progress.Report(JobStepCatalog.Merge, 0, item: "account", phase: "merge");
        Assert.Equal("merge", _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Merge].Phase);

        progress.Report(JobStepCatalog.Merge, 1, item: "contact", phase: "merge");
        var step = _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Merge];
        Assert.Equal("contact", step.Item);
        Assert.Equal(1, step.Done);
    }

    // Lease perdido: el worker A sigue unos segundos (hasta que su latido lo detecta y cancela la
    // etapa) mientras B ya reclamó la tarea. A, ya cancelado, no debe pisar el progreso de B.
    [Fact]
    public void CancelledWriter_DoesNotOverwriteTheNewOwner()
    {
        using var leaseA = new CancellationTokenSource();
        var writerA = _store.Begin(PipelineId, JobId, "sql", leaseA.Token);
        writerA.StartStep(JobStepCatalog.Prepare);
        writerA.CompleteStep(JobStepCatalog.Prepare);
        writerA.StartStep(JobStepCatalog.Merge, total: 10);

        var writerB = _store.Begin(PipelineId, JobId, "sql");
        writerB.StartStep(JobStepCatalog.Prepare);
        leaseA.Cancel();

        writerA.Report(JobStepCatalog.Merge, 9, item: "account", force: true);
        writerA.Finish("done");

        var loaded = _store.Load(PipelineId, JobId)!;
        Assert.Equal(JobStepCatalog.Prepare, loaded.CurrentStep);
        Assert.Equal("running", loaded.Steps[JobStepCatalog.Prepare].Status);
        Assert.Equal("pending", loaded.Steps[JobStepCatalog.Merge].Status);
    }

    // Filas acumuladas del paso (leídas en snapshots, fusionadas en merge): las métricas del
    // detalle las muestran en vivo antes de que la etapa escriba su Summary.
    [Fact]
    public void Report_Rows_AreKeptUntilTheNextReportWithRows()
    {
        var progress = _store.Begin(PipelineId, JobId, "ingest");
        progress.StartStep(JobStepCatalog.Snapshots, total: 3);

        progress.Report(JobStepCatalog.Snapshots, 1, rows: 5_000_000_000L, force: true);
        progress.Report(JobStepCatalog.Snapshots, 2, force: true);

        var step = _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Snapshots];
        Assert.Equal(2, step.Done);
        Assert.Equal(5_000_000_000L, step.Rows);
    }

    [Fact]
    public void CompleteStep_ClearsItemAndPhase()
    {
        var progress = _store.Begin(PipelineId, JobId, "sql");
        progress.StartStep(JobStepCatalog.Merge, total: 1);
        progress.Report(JobStepCatalog.Merge, 0, item: "account", phase: "merge", force: true);

        progress.CompleteStep(JobStepCatalog.Merge);

        var merge = _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Merge];
        Assert.Null(merge.Item);
        Assert.Null(merge.Phase);
        Assert.Null(_store.Load(PipelineId, JobId)!.CurrentStep);
    }

    [Fact]
    public void Begin_KeepsPreviousStages_AndResetsItsOwnSteps()
    {
        var ingest = _store.Begin(PipelineId, JobId, "ingest");
        ingest.StartStep(JobStepCatalog.Discover);
        ingest.CompleteStep(JobStepCatalog.Discover);
        ingest.Finish("done");

        // Primer intento de SQL que se cae a medias.
        var sql = _store.Begin(PipelineId, JobId, "sql");
        sql.StartStep(JobStepCatalog.Prepare);

        // Reintento de la etapa SQL.
        _store.Begin(PipelineId, JobId, "sql");

        var loaded = _store.Load(PipelineId, JobId)!;
        Assert.Equal("done", loaded.Steps[JobStepCatalog.Discover].Status);
        Assert.Equal("pending", loaded.Steps[JobStepCatalog.Prepare].Status);
        Assert.Null(loaded.Steps[JobStepCatalog.Prepare].StartedAt);
        Assert.Equal("pending", loaded.Steps[JobStepCatalog.Merge].Status);
        Assert.False(loaded.Steps.ContainsKey(JobStepCatalog.Compare));
    }

    [Fact]
    public void SkipRemaining_MarksPendingStepsOfTheStageAsSkipped()
    {
        var progress = _store.Begin(PipelineId, JobId, "ingest");
        progress.StartStep(JobStepCatalog.Discover);
        progress.CompleteStep(JobStepCatalog.Discover);

        progress.SkipRemaining();
        progress.Finish("done");

        var steps = _store.Load(PipelineId, JobId)!.Steps;
        Assert.Equal("done", steps[JobStepCatalog.Discover].Status);
        Assert.Equal("skipped", steps[JobStepCatalog.Snapshots].Status);
        Assert.Equal("skipped", steps[JobStepCatalog.Consolidate].Status);
        Assert.Equal("skipped", steps[JobStepCatalog.Register].Status);
    }

    [Fact]
    public void Finish_Error_MarksFailedStepAndClosesRunningSteps()
    {
        var progress = _store.Begin(PipelineId, JobId, "sql");
        progress.StartStep(JobStepCatalog.Prepare);
        progress.CompleteStep(JobStepCatalog.Prepare);
        progress.StartStep(JobStepCatalog.Merge, total: 2);
        progress.Report(JobStepCatalog.Merge, 2, failed: 1, force: true);

        progress.Finish("error", failedStep: JobStepCatalog.Merge, message: "1 entidad falló");

        var loaded = _store.Load(PipelineId, JobId)!;
        Assert.Null(loaded.CurrentStep);
        var merge = loaded.Steps[JobStepCatalog.Merge];
        Assert.Equal("error", merge.Status);
        Assert.Equal("1 entidad falló", merge.Message);
        Assert.Equal(1, merge.Failed);
        Assert.NotNull(merge.FinishedAt);
        Assert.Equal("done", loaded.Steps[JobStepCatalog.Prepare].Status);
    }

    [Fact]
    public void Finish_Done_ClosesStepsStillRunning()
    {
        var progress = _store.Begin(PipelineId, JobId, "reconcile");
        progress.StartStep(JobStepCatalog.Compare, total: 1);

        progress.Finish("done");

        var compare = _store.Load(PipelineId, JobId)!.Steps[JobStepCatalog.Compare];
        Assert.Equal("done", compare.Status);
        Assert.NotNull(compare.FinishedAt);
    }

    [Fact]
    public void Save_WhenRunDirectoryCannotBeCreated_DoesNotThrow()
    {
        // Un fichero donde debería ir el directorio del run: escribir falla con IOException.
        // El progreso es best-effort y nunca debe tumbar la etapa.
        var runsDir = Path.Combine(_dataDir, PipelineId, "runs");
        Directory.CreateDirectory(runsDir);
        File.WriteAllText(Path.Combine(runsDir, JobId), "blocking file");

        var progress = _store.Begin(PipelineId, JobId, "sql");
        progress.StartStep(JobStepCatalog.Prepare);
        progress.Finish("done");

        Assert.Null(_store.Load(PipelineId, JobId));
    }

    [Fact]
    public void NoneWriter_IsNoOp()
    {
        var progress = StageProgress.None;

        progress.StartStep(JobStepCatalog.Merge, total: 5);
        progress.Report(JobStepCatalog.Merge, 3, item: "x", force: true);
        progress.CompleteStep(JobStepCatalog.Merge);
        progress.SkipRemaining();
        progress.Finish("done");

        Assert.Empty(Directory.GetFileSystemEntries(_dataDir));
    }
}
