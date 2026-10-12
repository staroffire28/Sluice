using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

public sealed class JobFlowBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static JobEvent Ev(DateTimeOffset at, string message, EventLevel level = EventLevel.Info) =>
        new() { At = at, Level = level, Message = message };

    private static StepProgress Done(DateTimeOffset start, DateTimeOffset end, int done = 0, int total = 0) =>
        new() { Status = "done", StartedAt = start, FinishedAt = end, Done = done, Total = total };

    private static FlowStageView Stage(JobFlowView view, string key) => view.Stages.Single(s => s.Key == key);

    private static FlowStepView Step(JobFlowView view, string key) => view.Steps.Single(s => s.Key == key);

    // Forma A (Incremental): ingest → sql → reconcile, todo registrado (StageTimings + progress).
    private static (Job Job, JobProgress Progress) CompletedIncremental()
    {
        var job = new Job
        {
            Id = "job-a",
            PipelineId = "pipe-1",
            Type = JobType.Incremental,
            Status = JobStatus.Ok,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(31),
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done" },
            StageTimings = new()
            {
                ["ingest"] = new() { StartedAt = T0, FinishedAt = T0.AddMinutes(22) },
                ["sql"] = new() { StartedAt = T0.AddMinutes(22), FinishedAt = T0.AddMinutes(30) },
                ["reconcile"] = new() { StartedAt = T0.AddMinutes(30), FinishedAt = T0.AddMinutes(31) },
            },
            Summary = new()
            {
                ["snapshotsProcessed"] = 202,
                ["snapshotsSelected"] = 202,
                ["csvRows"] = 12_001_072,
                ["entities"] = 63,
                ["entityFailures"] = 0,
                ["partialEntityFailures"] = 0,
                ["sqlMergedRows"] = 5_619_939,
                ["maxDivergencePct"] = 0.0,
            },
        };
        var progress = new JobProgress
        {
            JobId = "job-a",
            Stage = "reconcile",
            Steps = new()
            {
                [JobStepCatalog.Discover] = Done(T0, T0.AddSeconds(5)),
                [JobStepCatalog.Snapshots] = Done(T0.AddSeconds(5), T0.AddMinutes(18), 202, 202),
                [JobStepCatalog.Consolidate] = Done(T0.AddMinutes(18), T0.AddMinutes(21), 63, 63),
                [JobStepCatalog.Register] = Done(T0.AddMinutes(21), T0.AddMinutes(22)),
                [JobStepCatalog.Prepare] = Done(T0.AddMinutes(22), T0.AddMinutes(23)),
                [JobStepCatalog.Merge] = Done(T0.AddMinutes(23), T0.AddMinutes(30), 63, 63),
                [JobStepCatalog.Compare] = Done(T0.AddMinutes(30), T0.AddMinutes(30.5), 63, 63),
                [JobStepCatalog.Report] = Done(T0.AddMinutes(30.5), T0.AddMinutes(31)),
            },
        };
        return (job, progress);
    }

    [Fact]
    public void ShapeA_Completed_AllStagesAndStepsDone_WithTimesAndFacts()
    {
        var (job, progress) = CompletedIncremental();

        var view = JobFlowBuilder.Build(job, progress, T0.AddHours(1));

        Assert.Equal(["ingest", "sql", "reconcile"], view.Stages.Select(s => s.Key));
        Assert.All(view.Stages, s => Assert.Equal(FlowStatus.Done, s.Status));
        Assert.Equal(8, view.Steps.Count());
        Assert.All(view.Steps, s => Assert.Equal(FlowStatus.Done, s.Status));
        Assert.Equal(100, view.Percent);
        Assert.Null(view.CurrentStepKey);

        var ingest = Stage(view, "ingest");
        Assert.Equal(TimeSpan.FromMinutes(22), ingest.Duration);
        Assert.Equal(100, ingest.Percent);
        Assert.Equal(TimeSpan.FromMinutes(8), Stage(view, "sql").Duration);

        var snapshots = Step(view, JobStepCatalog.Snapshots);
        Assert.Equal(202, snapshots.Done);
        Assert.Equal(202, snapshots.Total);
        Assert.Equal(StepUnit.Snapshots, snapshots.Unit);
        Assert.Equal(T0.AddSeconds(5), snapshots.StartedAt);
        Assert.Equal(TimeSpan.FromMinutes(18) - TimeSpan.FromSeconds(5), snapshots.Duration);
        Assert.Contains(snapshots.Facts, f => f.LabelKey == "JobFlow.Fact.CsvRows" && f.Number == 12_001_072);
        Assert.Contains(snapshots.Facts, f => f.LabelKey == "JobFlow.Fact.Snapshots" && f.Number == 202);
        Assert.Contains(Step(view, JobStepCatalog.Merge).Facts, f => f.LabelKey == "JobFlow.Fact.SqlRows" && f.Number == 5_619_939);
        Assert.Contains(Step(view, JobStepCatalog.Compare).Facts, f => f.LabelKey == "JobFlow.Fact.Divergence" && f.Percent == 0);
        // "Consolidar y publicar en ADLS": cuántos parquet (con su schema) se publicaron.
        Assert.Contains(Step(view, JobStepCatalog.Consolidate).Facts, f => f.LabelKey == "JobFlow.Fact.PublishedParquet" && f.Number == 63);
    }

    [Fact]
    public void ShapeB_Force_IngestSkippedWithForceReason()
    {
        var job = new Job
        {
            Id = "job-b",
            PipelineId = "pipe-1",
            Type = JobType.Force,
            Status = JobStatus.Running,
            StartedAt = T0,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new() { ["ingest"] = "skipped", ["sql"] = "running", ["reconcile"] = "pending" },
            StageTimings = new() { ["sql"] = new() { StartedAt = T0.AddSeconds(1) } },
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddMinutes(2));

        var ingest = Stage(view, "ingest");
        Assert.Equal(FlowStatus.Skipped, ingest.Status);
        Assert.Equal("JobFlow.Skip.Force", ingest.SkipReasonKey);
        Assert.All(ingest.Steps, s => Assert.Equal(FlowStatus.Skipped, s.Status));
        Assert.Equal(FlowStatus.Running, Stage(view, "sql").Status);
        Assert.Equal(FlowStatus.Pending, Stage(view, "reconcile").Status);
        Assert.Null(view.ScopeEntity);
        // Sin progress.json: los pasos heredan el estado de su etapa, sin tiempos propios.
        Assert.All(Stage(view, "sql").Steps, s =>
        {
            Assert.Equal(FlowStatus.Running, s.Status);
            Assert.Null(s.StartedAt);
        });
    }

    [Fact]
    public void ShapeC_Resync_IngestSkippedWithResyncReason_AndScopeEntity()
    {
        var job = new Job
        {
            Id = "job-c",
            PipelineId = "pipe-1",
            Type = JobType.ResyncTable,
            Status = JobStatus.Running,
            StartedAt = T0,
            StageStatus = new() { ["ingest"] = "skipped", ["sql"] = "pending", ["reconcile"] = "pending" },
            Events = [Ev(T0, "Re-sync de tabla custtable (Reload).")],
        };

        var view = JobFlowBuilder.Build(job, null, T0);

        var ingest = Stage(view, "ingest");
        Assert.Equal(FlowStatus.Skipped, ingest.Status);
        Assert.Equal("JobFlow.Skip.Resync", ingest.SkipReasonKey);
        Assert.Equal("custtable", view.ScopeEntity);
        Assert.Equal(0, view.Percent);
    }

    [Fact]
    public void ReconcileJob_IngestAndSqlSkippedWithReconcileReason_PercentOnlyCountsReconcile()
    {
        var job = new Job
        {
            Id = "job-recon",
            PipelineId = "pipe-1",
            Type = JobType.Reconcile,
            Status = JobStatus.Running,
            StartedAt = T0,
            CurrentStage = PipelineStage.Reconcile,
            StageStatus = new() { ["ingest"] = "skipped", ["sql"] = "skipped", ["reconcile"] = "running" },
        };

        var running = JobFlowBuilder.Build(job, null, T0.AddSeconds(5));
        var done = JobFlowBuilder.Build(
            job with { Status = JobStatus.Ok, FinishedAt = T0.AddSeconds(30), StageStatus = new() { ["ingest"] = "skipped", ["sql"] = "skipped", ["reconcile"] = "done" } },
            null,
            T0.AddMinutes(1));

        foreach (var stage in new[] { "ingest", "sql" })
        {
            Assert.Equal(FlowStatus.Skipped, Stage(running, stage).Status);
            Assert.Equal("JobFlow.Skip.Reconcile", Stage(running, stage).SkipReasonKey);
        }

        Assert.Equal(FlowStatus.Running, Stage(running, "reconcile").Status);
        Assert.Equal(0, running.Percent);
        Assert.Equal(100, done.Percent);
    }

    [Fact]
    public void DiffJob_SkipReasonDiff_AndDeltaFactsOnCompare()
    {
        var job = new Job
        {
            Id = "job-diff",
            PipelineId = "pipe-1",
            Type = JobType.Diff,
            Status = JobStatus.Ok,
            StartedAt = T0,
            FinishedAt = T0.AddSeconds(30),
            StageStatus = new() { ["ingest"] = "skipped", ["sql"] = "skipped", ["reconcile"] = "done" },
            Summary = new() { ["diffEntities"] = 2, ["diffTotalDelta"] = 15L },
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddMinutes(1));

        Assert.Equal("JobFlow.Skip.Diff", Stage(view, "ingest").SkipReasonKey);
        Assert.Equal("JobFlow.Skip.Diff", Stage(view, "sql").SkipReasonKey);
        var compare = Step(view, JobStepCatalog.Compare);
        Assert.Contains(compare.Facts, f => f.LabelKey == "JobFlow.Fact.DiffEntities" && f.Number == 2);
        Assert.Contains(compare.Facts, f => f.LabelKey == "JobFlow.Fact.DiffDelta" && f.Number == 15);
        Assert.DoesNotContain(compare.Facts, f => f.LabelKey == "JobFlow.Fact.Divergence");
        Assert.Equal(100, view.Percent);
    }

    [Fact]
    public void IngestErrorFromSummary_LaterStagesNotReached()
    {
        // Todas las entidades fallaron: el job se cierra en Error y SQL/Reconcile quedan
        // "pending" en StageStatus, pero nunca se van a alcanzar.
        var job = new Job
        {
            Id = "job-err",
            PipelineId = "pipe-1",
            Status = JobStatus.Error,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(1),
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "pending", ["reconcile"] = "pending" },
            StageTimings = new() { ["ingest"] = new() { StartedAt = T0, FinishedAt = T0.AddMinutes(1) } },
            Summary = new() { ["snapshotsSelected"] = 1, ["entityFailures"] = 2 },
            Events =
            [
                Ev(T0, JobEventMarkers.IngestStarted),
                Ev(T0.AddSeconds(10), "bad: ingest failed for snapshot s1 — X", EventLevel.Error),
                Ev(T0.AddMinutes(1).AddSeconds(1), "Ingest completed: 1 snapshot(s), 0 entity(ies). 2 entity(ies) failed: a, b.", EventLevel.Error),
            ],
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddHours(1));

        Assert.Equal(FlowStatus.Error, Stage(view, "ingest").Status);
        Assert.Equal(FlowStatus.NotReached, Stage(view, "sql").Status);
        Assert.Equal(FlowStatus.NotReached, Stage(view, "reconcile").Status);
        Assert.All(Stage(view, "sql").Steps, s => Assert.Equal(FlowStatus.NotReached, s.Status));
        // Sin progreso, el mensaje es el último aviso/error de la etapa (aunque llegue tras el fin).
        Assert.Equal(
            "Ingest completed: 1 snapshot(s), 0 entity(ies). 2 entity(ies) failed: a, b.",
            Step(view, JobStepCatalog.Snapshots).Message);
    }

    [Fact]
    public void IngestPartialFailures_WarningOnTheSnapshotsStep()
    {
        var (job, progress) = CompletedIncremental();
        job = job with
        {
            Status = JobStatus.Partial,
            Summary = new(job.Summary) { ["partialEntityFailures"] = 1 },
            Events =
            [
                Ev(T0, JobEventMarkers.IngestStarted),
                Ev(T0.AddMinutes(21), "Ingest completed: ... 1 entity(ies) with partial snapshot failures", EventLevel.Warning),
                Ev(T0.AddMinutes(22), JobEventMarkers.SqlStarted),
                Ev(T0.AddMinutes(25), "otra: aviso de la etapa SQL", EventLevel.Warning),
            ],
        };

        var view = JobFlowBuilder.Build(job, progress, T0.AddHours(1));

        Assert.Equal(FlowStatus.Warning, Stage(view, "ingest").Status);
        var snapshots = Step(view, JobStepCatalog.Snapshots);
        Assert.Equal(FlowStatus.Warning, snapshots.Status);
        Assert.Contains("partial snapshot failures", snapshots.Message);
        Assert.Equal(FlowStatus.Done, Stage(view, "sql").Status);
    }

    [Fact]
    public void StoppedAtIncompleteSnapshot_IsWarning()
    {
        var (job, progress) = CompletedIncremental();
        job = job with { Summary = new(job.Summary) { ["stoppedAtIncompleteSnapshot"] = "missing csv" } };

        var view = JobFlowBuilder.Build(job, progress, T0.AddHours(1));

        Assert.Equal(FlowStatus.Warning, Stage(view, "ingest").Status);
    }

    [Fact]
    public void Cancelled_StartedStageIsCancelled_UnstartedIsNotReached()
    {
        var job = new Job
        {
            Id = "job-cancel",
            PipelineId = "pipe-1",
            Status = JobStatus.Cancelled,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(10),
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "cancelled", ["reconcile"] = "cancelled" },
            StageTimings = new()
            {
                ["ingest"] = new() { StartedAt = T0, FinishedAt = T0.AddMinutes(5) },
                ["sql"] = new() { StartedAt = T0.AddMinutes(5) },
            },
        };
        var progress = new JobProgress
        {
            JobId = "job-cancel",
            Stage = "sql",
            CurrentStep = JobStepCatalog.Merge,
            Steps = new()
            {
                [JobStepCatalog.Prepare] = Done(T0.AddMinutes(5), T0.AddMinutes(6)),
                [JobStepCatalog.Merge] = new() { Status = "running", StartedAt = T0.AddMinutes(6), Done = 10, Total = 20 },
            },
        };

        var view = JobFlowBuilder.Build(job, progress, T0.AddHours(1));

        var sql = Stage(view, "sql");
        Assert.Equal(FlowStatus.Cancelled, sql.Status);
        // Sin fin registrado, una etapa de un job terminado se cierra con el fin del job.
        Assert.Equal(T0.AddMinutes(10), sql.FinishedAt);
        Assert.Equal(TimeSpan.FromMinutes(5), sql.Duration);
        Assert.Equal(FlowStatus.Cancelled, Step(view, JobStepCatalog.Merge).Status);
        Assert.Equal(50, Step(view, JobStepCatalog.Merge).Percent);
        Assert.Equal(FlowStatus.NotReached, Stage(view, "reconcile").Status);
        Assert.Null(view.CurrentStepKey);
        Assert.NotEqual(100, view.Percent);
    }

    [Fact]
    public void RunningStageOfStoppedJob_IsInterrupted()
    {
        // El watchdog marca Error un job huérfano sin tocar StageStatus: "running" ya no es cierto.
        var job = new Job
        {
            Id = "job-int",
            PipelineId = "pipe-1",
            Status = JobStatus.Error,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(40),
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "running", ["reconcile"] = "pending" },
            StageTimings = new()
            {
                ["ingest"] = new() { StartedAt = T0, FinishedAt = T0.AddMinutes(5) },
                ["sql"] = new() { StartedAt = T0.AddMinutes(5) },
            },
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddHours(1));

        var sql = Stage(view, "sql");
        Assert.Equal(FlowStatus.Interrupted, sql.Status);
        Assert.Equal(job.FinishedAt, sql.FinishedAt);
        Assert.Equal(TimeSpan.FromMinutes(35), sql.Duration);
        Assert.Equal(FlowStatus.NotReached, Stage(view, "reconcile").Status);
    }

    [Fact]
    public void Blocked_IngestBlocked_FollowingNotReached_WithReason()
    {
        var job = new Job
        {
            Id = "job-blocked",
            PipelineId = "pipe-1",
            Status = JobStatus.Blocked,
            StartedAt = T0,
            FinishedAt = T0.AddSeconds(3),
            StageStatus = new() { ["ingest"] = "blocked", ["sql"] = "pending", ["reconcile"] = "pending" },
            StageTimings = new() { ["ingest"] = new() { StartedAt = T0, FinishedAt = T0.AddSeconds(3) } },
            Events =
            [
                Ev(T0, JobEventMarkers.IngestStarted),
                Ev(T0.AddSeconds(4), "Changelog empty or missing — export in progress.", EventLevel.Warning),
            ],
        };
        var progress = new JobProgress
        {
            JobId = "job-blocked",
            Stage = "ingest",
            Steps = new()
            {
                [JobStepCatalog.Discover] = new() { Status = "blocked", StartedAt = T0, FinishedAt = T0.AddSeconds(3) },
                [JobStepCatalog.Snapshots] = new(),
                [JobStepCatalog.Consolidate] = new(),
                [JobStepCatalog.Register] = new(),
            },
        };

        var view = JobFlowBuilder.Build(job, progress, T0.AddHours(1));

        Assert.Equal(FlowStatus.Blocked, Stage(view, "ingest").Status);
        var discover = Step(view, JobStepCatalog.Discover);
        Assert.Equal(FlowStatus.Blocked, discover.Status);
        Assert.Equal("Changelog empty or missing — export in progress.", discover.Message);
        Assert.Equal(FlowStatus.NotReached, Step(view, JobStepCatalog.Snapshots).Status);
        Assert.Equal(FlowStatus.NotReached, Stage(view, "sql").Status);
        Assert.Equal(0, view.Percent);
    }

    [Fact]
    public void NoNewData_FromSummary()
    {
        var job = new Job
        {
            Id = "job-nonew",
            PipelineId = "pipe-1",
            Type = JobType.Incremental,
            Status = JobStatus.Ok,
            StartedAt = T0,
            FinishedAt = T0.AddSeconds(20),
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done" },
            Summary = new() { ["snapshotsProcessed"] = 0, ["snapshotsSelected"] = 0 },
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddHours(1));

        Assert.All(view.Stages, s => Assert.Equal(FlowStatus.NoNewData, s.Status));
    }

    [Fact]
    public void NoNewData_FromEvent_ReconcileJobStillReconciles()
    {
        // Jobs anteriores a este cambio no guardaban Summary en esa salida: basta el evento.
        // Un job Reconcile sí compara todo el pipeline aunque no haya snapshots nuevos.
        var job = new Job
        {
            Id = "job-nonew-rec",
            PipelineId = "pipe-1",
            Type = JobType.Reconcile,
            Status = JobStatus.Ok,
            StartedAt = T0,
            FinishedAt = T0.AddSeconds(20),
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done" },
            Events = [Ev(T0.AddSeconds(1), JobEventMarkers.NoNewSnapshots)],
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddHours(1));

        Assert.Equal(FlowStatus.NoNewData, Stage(view, "ingest").Status);
        Assert.Equal(FlowStatus.NoNewData, Stage(view, "sql").Status);
        Assert.Equal(FlowStatus.Done, Stage(view, "reconcile").Status);
    }

    [Fact]
    public void OldJob_WithoutTimingsOrProgress_ReconstructsTimesFromEvents()
    {
        var job = new Job
        {
            Id = "job-old",
            PipelineId = "pipe-1",
            Type = JobType.Incremental,
            Status = JobStatus.Ok,
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(10),
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done" },
            Events =
            [
                Ev(T0, "Job started (Incremental)."),
                Ev(T0.AddSeconds(1), JobEventMarkers.IngestStarted),
                Ev(T0.AddMinutes(3), "Ingest completed: 2 snapshot(s), 3 entity(ies)."),
                Ev(T0.AddMinutes(4), JobEventMarkers.SqlStarted),
                // SQL sin evento de cierre: termina donde empieza la etapa siguiente.
                Ev(T0.AddMinutes(9), JobEventMarkers.ReconcileStarted),
                Ev(T0.AddMinutes(9).AddSeconds(30), "Reconcile completed. Max divergence: 0.00%."),
            ],
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddHours(1));

        var ingest = Stage(view, "ingest");
        Assert.Equal(T0.AddSeconds(1), ingest.StartedAt);
        Assert.Equal(T0.AddMinutes(3), ingest.FinishedAt);
        var sql = Stage(view, "sql");
        Assert.Equal(T0.AddMinutes(4), sql.StartedAt);
        Assert.Equal(T0.AddMinutes(9), sql.FinishedAt);
        Assert.Equal(TimeSpan.FromSeconds(30), Stage(view, "reconcile").Duration);
        // Los pasos heredan el estado de su etapa, sin tiempos propios.
        Assert.All(view.Steps, s =>
        {
            Assert.Equal(FlowStatus.Done, s.Status);
            Assert.Null(s.StartedAt);
            Assert.Null(s.Duration);
            Assert.Equal(100, s.Percent);
        });
    }

    [Fact]
    public void RunningJob_LiveDurationsCountersAndCurrentStep()
    {
        var now = T0.AddMinutes(7);
        var job = new Job
        {
            Id = "job-live",
            PipelineId = "pipe-1",
            Status = JobStatus.Running,
            StartedAt = T0,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new() { ["ingest"] = "running", ["sql"] = "pending", ["reconcile"] = "pending" },
            StageTimings = new() { ["ingest"] = new() { StartedAt = T0 } },
        };
        var progress = new JobProgress
        {
            JobId = "job-live",
            Stage = "ingest",
            CurrentStep = JobStepCatalog.Snapshots,
            Steps = new()
            {
                [JobStepCatalog.Discover] = Done(T0, T0.AddSeconds(5)),
                [JobStepCatalog.Snapshots] = new()
                {
                    Status = "running",
                    StartedAt = T0.AddSeconds(5),
                    Done = 46,
                    Total = 201,
                    Rows = 1_234_567,
                    Item = "2026-06-08T10.00.00Z · custtable",
                },
                [JobStepCatalog.Consolidate] = new(),
                [JobStepCatalog.Register] = new(),
            },
        };

        var view = JobFlowBuilder.Build(job, progress, now);

        Assert.Equal(JobStepCatalog.Snapshots, view.CurrentStepKey);
        Assert.Equal(14, view.Percent);
        var ingest = Stage(view, "ingest");
        Assert.Equal(FlowStatus.Running, ingest.Status);
        Assert.Equal(TimeSpan.FromMinutes(7), ingest.Duration);
        Assert.Null(ingest.FinishedAt);
        Assert.Equal(20, ingest.Percent);
        var snapshots = Step(view, JobStepCatalog.Snapshots);
        Assert.Equal(FlowStatus.Running, snapshots.Status);
        Assert.Equal(now - T0.AddSeconds(5), snapshots.Duration);
        Assert.Equal(23, snapshots.Percent);
        Assert.Equal("2026-06-08T10.00.00Z · custtable", snapshots.Item);
        Assert.Equal(1_234_567, snapshots.Rows);
        Assert.Equal(FlowStatus.Pending, Step(view, JobStepCatalog.Consolidate).Status);
        Assert.Equal(FlowStatus.Pending, Stage(view, "sql").Status);
        Assert.Null(Stage(view, "sql").Duration);
    }

    [Fact]
    public void PartialJobStillRunning_IsNotInterrupted()
    {
        // Un ingest con entidades fallidas deja el job en Partial pero el pipeline sigue con SQL:
        // Partial sin FinishedAt es un job en curso.
        var job = new Job
        {
            Id = "job-partial-live",
            PipelineId = "pipe-1",
            Status = JobStatus.Partial,
            StartedAt = T0,
            StageStatus = new() { ["ingest"] = "error", ["sql"] = "running", ["reconcile"] = "pending" },
            StageTimings = new()
            {
                ["ingest"] = new() { StartedAt = T0, FinishedAt = T0.AddMinutes(1) },
                ["sql"] = new() { StartedAt = T0.AddMinutes(1) },
            },
        };

        var view = JobFlowBuilder.Build(job, null, T0.AddMinutes(3));

        Assert.Equal(FlowStatus.Error, Stage(view, "ingest").Status);
        Assert.Equal(FlowStatus.Running, Stage(view, "sql").Status);
        Assert.Equal(TimeSpan.FromMinutes(2), Stage(view, "sql").Duration);
        Assert.Equal(FlowStatus.Pending, Stage(view, "reconcile").Status);
    }

    [Fact]
    public void SqlEntityFailures_FailedEntitiesFactAndErrorOnMerge()
    {
        var (job, progress) = CompletedIncremental();
        job = job with
        {
            Status = JobStatus.Partial,
            StageStatus = new() { ["ingest"] = "done", ["sql"] = "error", ["reconcile"] = "done" },
        };
        progress.Steps[JobStepCatalog.Merge] = progress.Steps[JobStepCatalog.Merge] with { Status = "error", Failed = 2 };

        var view = JobFlowBuilder.Build(job, progress, T0.AddHours(1));

        Assert.Equal(FlowStatus.Error, Stage(view, "sql").Status);
        var merge = Step(view, JobStepCatalog.Merge);
        Assert.Equal(FlowStatus.Error, merge.Status);
        Assert.Equal(2, merge.Failed);
        Assert.Contains(merge.Facts, f => f.LabelKey == "JobFlow.Fact.FailedEntities" && f.Number == 2);
        Assert.Equal(FlowStatus.Done, Step(view, JobStepCatalog.Prepare).Status);
    }
}
