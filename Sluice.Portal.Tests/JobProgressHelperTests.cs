using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

// Regresión: antes del fix, GetPercent contaba Job.Entities (lista plana que cada etapa
// sobreescribe/mergea SOLO al terminar) — así que apenas terminaba Ingest, el % ya marcaba 100%
// aunque SQL/Reconcile no hubieran arrancado. Ahora pondera por Job.StageStatus, que sí es
// correcto por etapa, con el peso real de cada etapa (ingest 70, sql 25, reconcile 5) y, si hay
// progress.json, con el avance dentro de la etapa.
public sealed class JobProgressHelperTests
{
    private static Job MakeJob(Dictionary<string, string> stageStatus, JobStatus status = JobStatus.Running) => new()
    {
        Id = "job-1",
        PipelineId = "pipe-1",
        Status = status,
        StageStatus = stageStatus,
    };

    private static JobProgress MakeProgress(params (string Key, StepProgress Step)[] steps) => new()
    {
        JobId = "job-1",
        Steps = steps.ToDictionary(s => s.Key, s => s.Step),
    };

    [Fact]
    public void GetPercent_NoStagesResolved_Returns0()
    {
        var job = MakeJob(new());
        Assert.Equal(0, JobProgressHelper.GetPercent(job));
    }

    [Fact]
    public void GetPercent_IngestRunning_Returns0_NotEntityBased100()
    {
        // Caso original reportado: Ingest corriendo, sin entidades todavía.
        var job = MakeJob(new() { ["ingest"] = "running" });
        Assert.Equal(0, JobProgressHelper.GetPercent(job));
    }

    [Fact]
    public void GetPercent_IngestDone_SqlRunning_Returns70_NotEntityBased100()
    {
        // Este es EXACTAMENTE el bug reportado: Ingest terminó (todas sus entidades "ok"),
        // SQL está corriendo — antes del fix esto daba 100% (contando Job.Entities de Ingest).
        var job = MakeJob(new() { ["ingest"] = "done", ["sql"] = "running" });
        Assert.Equal(70, JobProgressHelper.GetPercent(job));
    }

    [Fact]
    public void GetPercent_IngestAndSqlDone_ReconcileRunning_Returns95()
    {
        var job = MakeJob(new() { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "running" });
        Assert.Equal(95, JobProgressHelper.GetPercent(job));
    }

    [Fact]
    public void GetPercent_AllStagesDone_Returns100()
    {
        var job = MakeJob(
            new() { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done" },
            JobStatus.Ok);
        Assert.Equal(100, JobProgressHelper.GetPercent(job));
    }

    [Fact]
    public void GetPercent_IngestFailedFully_JobEndsError_Returns70_NotEntityBased100()
    {
        // Caso de regresión explícito del plan: Ingest terminó en error (todas las entidades
        // fallaron), el job se cierra ahí sin llegar a SQL/Reconcile. El % debe reflejar cuánto
        // del pipeline se recorrió (la etapa de ingest), no 100% solo porque el job ya terminó.
        var job = MakeJob(new() { ["ingest"] = "error" }, JobStatus.Error);
        Assert.Equal(70, JobProgressHelper.GetPercent(job));
    }

    [Fact]
    public void GetPercent_PendingStageDoesNotCount()
    {
        // "pending" es el valor transitorio que StageRouter deja en la siguiente etapa apenas
        // termina la anterior, antes de que el worker la reclame — no debe contar como resuelta.
        var job = MakeJob(new() { ["ingest"] = "done", ["sql"] = "pending" });
        Assert.Equal(70, JobProgressHelper.GetPercent(job));
    }

    [Fact]
    public void GetPercent_Force_SkippedIngestIsOutOfTheDenominator()
    {
        // Force/Resync no ingieren: sin excluir la etapa omitida el job nunca pasaría de 30 %.
        var running = MakeJob(new() { ["ingest"] = "skipped", ["sql"] = "running", ["reconcile"] = "pending" });
        var sqlDone = MakeJob(new() { ["ingest"] = "skipped", ["sql"] = "done", ["reconcile"] = "pending" });

        Assert.Equal(0, JobProgressHelper.GetPercent(running));
        Assert.Equal(83, JobProgressHelper.GetPercent(sqlDone));
    }

    [Fact]
    public void GetPercent_WithProgress_UsesTheSnapshotCounter()
    {
        // "snapshot 46 de 201": discover (peso 2) hecho + 46/201 de snapshots (peso 78), dentro
        // de una etapa que pesa 70 → 70 · (2 + 78·46/201) / 100 ≈ 13,9.
        var job = MakeJob(new() { ["ingest"] = "running", ["sql"] = "pending", ["reconcile"] = "pending" });
        var progress = MakeProgress(
            (JobStepCatalog.Discover, new StepProgress { Status = "done" }),
            (JobStepCatalog.Snapshots, new StepProgress { Status = "running", Done = 46, Total = 201 }),
            (JobStepCatalog.Consolidate, new StepProgress()),
            (JobStepCatalog.Register, new StepProgress()));

        Assert.Equal(14, JobProgressHelper.GetPercent(job, progress));
    }

    [Fact]
    public void GetPercent_Cancelled_IsNot100()
    {
        // Antes un stage "cancelled" contaba como recorrido y un job cancelado en SQL marcaba 100 %.
        var job = MakeJob(
            new() { ["ingest"] = "done", ["sql"] = "cancelled", ["reconcile"] = "cancelled" },
            JobStatus.Cancelled);
        Assert.Equal(70, JobProgressHelper.GetPercent(job));

        // Con progreso: prepare (5) hecho y la mitad de merge (95) → 70 + 25 · 52,5 % ≈ 83.
        var progress = MakeProgress(
            (JobStepCatalog.Prepare, new StepProgress { Status = "done" }),
            (JobStepCatalog.Merge, new StepProgress { Status = "running", Done = 10, Total = 20 }));
        Assert.Equal(83, JobProgressHelper.GetPercent(job, progress));
    }

    [Fact]
    public void GetPercent_PartialJobWithFailedSnapshotsStep_CountsWhatWasTraversed()
    {
        // Ingest con una entidad fallida: el paso snapshots queda "error" pero recorrió 201/201.
        // Un paso con contador cuenta Done/Total sea cual sea su estado; si no, el job terminado
        // marcaría ~45 %.
        var job = MakeJob(
            new() { ["ingest"] = "error", ["sql"] = "done", ["reconcile"] = "done" },
            JobStatus.Partial);
        var progress = MakeProgress(
            (JobStepCatalog.Discover, new StepProgress { Status = "done" }),
            (JobStepCatalog.Snapshots, new StepProgress { Status = "error", Done = 201, Total = 201, Failed = 1 }),
            (JobStepCatalog.Consolidate, new StepProgress { Status = "done", Done = 62, Total = 62 }),
            (JobStepCatalog.Register, new StepProgress { Status = "done" }));

        Assert.Equal(100, JobProgressHelper.GetPercent(job, progress));
    }

    [Fact]
    public void GetPercent_BlockedStage_Counts0()
    {
        var job = MakeJob(new() { ["ingest"] = "blocked", ["sql"] = "pending", ["reconcile"] = "pending" }, JobStatus.Blocked);
        Assert.Equal(0, JobProgressHelper.GetPercent(job));
    }

    // Partial no es terminal: tras un ingest parcial el job queda en Partial y el pipeline
    // sigue con SQL/Reconcile hasta que StageRouter pone FinishedAt.
    [Theory]
    [InlineData(JobStatus.Running, false, true)]
    [InlineData(JobStatus.Partial, false, true)]
    [InlineData(JobStatus.Partial, true, false)]
    [InlineData(JobStatus.Ok, true, false)]
    [InlineData(JobStatus.Error, false, false)]
    [InlineData(JobStatus.Blocked, false, false)]
    [InlineData(JobStatus.Cancelled, false, false)]
    public void IsActive_RunningOrUnfinishedPartial(JobStatus status, bool finished, bool expected)
    {
        var job = MakeJob(new(), status) with { FinishedAt = finished ? DateTimeOffset.UtcNow : null };
        Assert.Equal(expected, JobProgressHelper.IsActive(job));
    }

    [Fact]
    public void GetActiveStageKey_ReturnsRunningStage_WhenJobIsRunning()
    {
        var job = MakeJob(new() { ["ingest"] = "done", ["sql"] = "running" });
        Assert.Equal("sql", JobProgressHelper.GetActiveStageKey(job));
    }

    [Fact]
    public void GetActiveStageKey_UnfinishedPartialJob_ReturnsRunningStage()
    {
        var job = MakeJob(new() { ["ingest"] = "error", ["sql"] = "running" }, JobStatus.Partial);
        Assert.Equal("sql", JobProgressHelper.GetActiveStageKey(job));
    }

    [Fact]
    public void GetActiveStageKey_ReturnsNull_WhenJobNotRunning()
    {
        var job = MakeJob(
            new() { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done" },
            JobStatus.Ok);
        Assert.Null(JobProgressHelper.GetActiveStageKey(job));
    }
}
