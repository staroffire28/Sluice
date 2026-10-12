using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

// Construye la vista del flujo de un job (JobFlowView) a partir del Job y de su progress.json.
// Pura: sin I/O ni textos localizados (la UI traduce las claves). Normaliza lo guardado para que
// la pantalla no mienta: etapas "pending" que ya nunca se alcanzarán, "running" de un job que ya
// no corre, "sin snapshots nuevos", fallos de ingest que StageRouter dejó como "done"...
public static class JobFlowBuilder
{
    private static readonly string[] Stages = ["ingest", "sql", "reconcile"];

    // Paso donde se concentra el trabajo (y los fallos por entidad) de cada etapa: recibe el
    // aviso/error de la etapa cuando el progreso no lo sitúa en ningún paso.
    private static readonly Dictionary<string, string> WorkStep = new()
    {
        ["ingest"] = JobStepCatalog.Snapshots,
        ["sql"] = JobStepCatalog.Merge,
        ["reconcile"] = JobStepCatalog.Compare,
    };

    private static readonly Dictionary<string, string> StartMarkers = new()
    {
        ["ingest"] = JobEventMarkers.IngestStarted,
        ["sql"] = JobEventMarkers.SqlStarted,
        ["reconcile"] = JobEventMarkers.ReconcileStarted,
    };

    private static readonly Dictionary<string, string[]> FinishMarkers = new()
    {
        ["ingest"] = [JobEventMarkers.IngestCompletedPrefix, JobEventMarkers.IngestStoppedEarlyPrefix, JobEventMarkers.NoNewSnapshots],
        ["sql"] = [JobEventMarkers.SqlCompletedPrefix, JobEventMarkers.SqlNoManifest],
        ["reconcile"] = [JobEventMarkers.ReconcileCompletedPrefix],
    };

    private static readonly HashSet<FlowStatus> ProblemStatuses =
    [
        FlowStatus.Warning, FlowStatus.Error, FlowStatus.Blocked, FlowStatus.Cancelled, FlowStatus.Interrupted,
    ];

    public static JobFlowView Build(Job job, JobProgress? progress, DateTimeOffset now)
    {
        var active = JobProgressHelper.IsActive(job);
        var noNewData = IsNoNewData(job);
        var times = Stages.ToDictionary(s => s, s => ResolveTimes(job, s, active));

        var stages = new List<FlowStageView>();
        for (var i = 0; i < Stages.Length; i++)
        {
            var stage = Stages[i];
            var raw = job.StageStatus.GetValueOrDefault(stage)?.ToLowerInvariant();
            var (started, finished) = times[stage];
            var status = StageStatusOf(job, stage, raw, started, active, noNewData);

            // Ventana de eventos de la etapa: hasta que empieza la siguiente (no hasta su fin:
            // el evento de cierre, el más útil, se escribe justo después de guardar el fin).
            var windowEnd = Stages.Skip(i + 1).Select(s => times[s].Started).FirstOrDefault(t => t is not null);
            var issue = started is null ? null : job.Events
                .Where(e => e.Level is EventLevel.Warning or EventLevel.Error
                    && e.At >= started && (windowEnd is null || e.At < windowEnd))
                .Select(e => e.Message)
                .LastOrDefault();

            stages.Add(new FlowStageView(
                stage,
                status,
                started,
                finished,
                Duration(started, finished, status, now),
                Percent(JobProgressHelper.GetStageFraction(stage, raw, progress)),
                status == FlowStatus.Skipped ? SkipReasonKey(job.Type) : null,
                BuildSteps(job, stage, status, finished, progress, issue, now)));
        }

        return new JobFlowView(
            job.Type,
            ScopeEntity(job),
            stages,
            active ? progress?.CurrentStep : null,
            JobProgressHelper.GetPercent(job, progress));
    }

    // Por qué un tipo de job omite etapas: Force y Resync parten del parquet consolidado;
    // Reconcile y Diff solo comparan conteos.
    private static string SkipReasonKey(JobType type) => type switch
    {
        JobType.ResyncTable => "JobFlow.Skip.Resync",
        JobType.Reconcile => "JobFlow.Skip.Reconcile",
        JobType.Diff => "JobFlow.Skip.Diff",
        JobType.CompareValues => "JobFlow.Skip.CompareValues",
        _ => "JobFlow.Skip.Force",
    };

    private static FlowStatus StageStatusOf(
        Job job, string stage, string? raw, DateTimeOffset? started, bool active, bool noNewData) => raw switch
    {
        "skipped" => FlowStatus.Skipped,
        "running" or "claimed" => active ? FlowStatus.Running : FlowStatus.Interrupted,
        null or "" or "pending" => active ? FlowStatus.Pending : FlowStatus.NotReached,
        "cancelled" => started is null ? FlowStatus.NotReached : FlowStatus.Cancelled,
        "blocked" => FlowStatus.Blocked,
        "error" => FlowStatus.Error,
        _ when noNewData && (stage != "reconcile" || job.Type is not (JobType.Reconcile or JobType.CompareValues)) => FlowStatus.NoNewData,
        _ when stage == "ingest" => IngestOutcome(job),
        _ => FlowStatus.Done,
    };

    // StageRouter guarda "done" al acabar el ingest aunque haya entidades fallidas: el Summary
    // del ingest es la fuente fiable.
    private static FlowStatus IngestOutcome(Job job)
    {
        if (JobSummary.GetInt(job, "entityFailures") > 0)
        {
            return FlowStatus.Error;
        }

        return JobSummary.GetInt(job, "partialEntityFailures") > 0
            || JobSummary.GetString(job, "stoppedAtIncompleteSnapshot") is not null
            ? FlowStatus.Warning
            : FlowStatus.Done;
    }

    private static bool IsNoNewData(Job job) =>
        string.Equals(job.StageStatus.GetValueOrDefault("ingest"), "done", StringComparison.OrdinalIgnoreCase)
        && (JobSummary.GetInt(job, "snapshotsSelected") == 0
            || job.Events.Any(e => e.Message == JobEventMarkers.NoNewSnapshots));

    // Tiempos registrados (StageTimings) o, en jobs anteriores, reconstruidos desde los eventos.
    // Una etapa empezada sin fin en un job ya cerrado (cancelación, caída) acaba con el job.
    private static (DateTimeOffset? Started, DateTimeOffset? Finished) ResolveTimes(Job job, string stage, bool active)
    {
        DateTimeOffset? started;
        DateTimeOffset? finished;
        if (job.StageTimings.TryGetValue(stage, out var recorded))
        {
            started = recorded.StartedAt;
            finished = recorded.FinishedAt;
        }
        else
        {
            // Si la etapa se reintentó, vale el último inicio.
            started = job.Events.LastOrDefault(e => e.Message == StartMarkers[stage])?.At;
            finished = started is null ? null : job.Events
                .FirstOrDefault(e => e.At >= started && FinishMarkers[stage].Any(m => e.Message.StartsWith(m, StringComparison.Ordinal)))?.At
                ?? Stages.SkipWhile(s => s != stage).Skip(1)
                    .Select(s => job.Events.FirstOrDefault(e => e.At >= started && e.Message == StartMarkers[s])?.At)
                    .FirstOrDefault(t => t is not null);
        }

        if (started is not null && finished is null && !active)
        {
            finished = job.FinishedAt;
        }

        return (started, finished);
    }

    private static IReadOnlyList<FlowStepView> BuildSteps(
        Job job,
        string stage,
        FlowStatus stageStatus,
        DateTimeOffset? stageFinished,
        JobProgress? progress,
        string? issue,
        DateTimeOffset now)
    {
        var defs = JobStepCatalog.ForStage(stage).ToList();
        var recorded = progress is not null && defs.Any(d => progress.Steps.ContainsKey(d.Key));

        var statuses = defs.ToDictionary(
            d => d.Key,
            d => recorded && progress!.Steps.TryGetValue(d.Key, out var step)
                ? StepStatusOf(step.Status, stageStatus)
                : stageStatus);

        // Un aviso/error de etapa que ningún paso refleja (p. ej. ingest parcial con todos los
        // pasos "done") se muestra en el paso de trabajo de la etapa.
        if (recorded && stageStatus is FlowStatus.Warning or FlowStatus.Error
            && !statuses.Values.Any(s => s is FlowStatus.Warning or FlowStatus.Error)
            && progress!.Steps.ContainsKey(WorkStep[stage]))
        {
            statuses[WorkStep[stage]] = stageStatus;
        }

        return defs.Select(def =>
        {
            var status = statuses[def.Key];
            var step = recorded ? progress!.Steps.GetValueOrDefault(def.Key) : null;
            var finished = step?.FinishedAt
                ?? (step?.StartedAt is not null && status != FlowStatus.Running ? stageFinished : null);

            // Sin progreso los pasos heredan el estado de la etapa: el mensaje va solo al paso
            // de trabajo para no repetirlo en todos.
            var message = step?.Message
                ?? (ProblemStatuses.Contains(status) && (recorded || def.Key == WorkStep[stage]) ? issue : null);

            return new FlowStepView(
                def.Key,
                stage,
                status,
                step?.StartedAt,
                finished,
                Duration(step?.StartedAt, finished, status, now),
                def.Unit,
                step?.Done ?? 0,
                step?.Total ?? 0,
                step?.Failed ?? 0,
                step?.Item,
                step?.Phase,
                step is not null
                    ? Percent(JobProgressHelper.GetStepFraction(step))
                    : status is FlowStatus.Done or FlowStatus.NoNewData or FlowStatus.Warning or FlowStatus.Skipped ? 100 : 0,
                Facts(job, def.Key, step),
                message,
                step?.Rows ?? 0);
        }).ToList();
    }

    private static FlowStatus StepStatusOf(string status, FlowStatus stageStatus) => status switch
    {
        "done" => FlowStatus.Done,
        "skipped" => FlowStatus.Skipped,
        "error" => FlowStatus.Error,
        "blocked" => FlowStatus.Blocked,
        "running" => stageStatus switch
        {
            FlowStatus.Running => FlowStatus.Running,
            FlowStatus.Error or FlowStatus.Cancelled or FlowStatus.Blocked => stageStatus,
            _ => FlowStatus.Interrupted,
        },
        _ => stageStatus switch
        {
            FlowStatus.Running or FlowStatus.Pending => FlowStatus.Pending,
            FlowStatus.Skipped => FlowStatus.Skipped,
            _ => FlowStatus.NotReached,
        },
    };

    private static IReadOnlyList<FlowFact> Facts(Job job, string stepKey, StepProgress? step)
    {
        var facts = new List<FlowFact>();
        switch (stepKey)
        {
            case JobStepCatalog.Snapshots:
                if (JobSummary.GetLong(job, "csvRows") is { } csvRows)
                {
                    facts.Add(new FlowFact("JobFlow.Fact.CsvRows", Number: csvRows));
                }

                if (JobSummary.GetLong(job, "snapshotsProcessed") is { } snapshots)
                {
                    facts.Add(new FlowFact("JobFlow.Fact.Snapshots", Number: snapshots));
                }

                break;
            case JobStepCatalog.Consolidate:
                // Un parquet consolidado (más su schema) publicado en ADLS por entidad.
                if (JobSummary.GetLong(job, "entities") is { } published)
                {
                    facts.Add(new FlowFact("JobFlow.Fact.PublishedParquet", Number: published));
                }

                break;
            case JobStepCatalog.Merge:
                if (JobSummary.GetLong(job, "sqlMergedRows") is { } sqlRows)
                {
                    facts.Add(new FlowFact("JobFlow.Fact.SqlRows", Number: sqlRows));
                }

                if (step is { Failed: > 0 })
                {
                    facts.Add(new FlowFact("JobFlow.Fact.FailedEntities", Number: step.Failed));
                }

                break;
            case JobStepCatalog.Compare:
                if (JobSummary.GetDouble(job, "maxDivergencePct") is { } divergencePct)
                {
                    facts.Add(new FlowFact("JobFlow.Fact.Divergence", Percent: divergencePct / 100));
                }

                // Diff: entidades con delta distinto de 0 y suma de |origen - destino|.
                if (JobSummary.GetLong(job, "diffEntities") is { } diffEntities)
                {
                    facts.Add(new FlowFact("JobFlow.Fact.DiffEntities", Number: diffEntities));
                }

                if (JobSummary.GetLong(job, "diffTotalDelta") is { } diffDelta)
                {
                    facts.Add(new FlowFact("JobFlow.Fact.DiffDelta", Number: diffDelta));
                }

                break;
        }

        return facts;
    }

    // Re-sync de una tabla: la entidad solo queda en el evento de creación
    // ("Re-sync de tabla {entity} ({mode}).") y, tras SQL, como única entidad del job.
    private static string? ScopeEntity(Job job)
    {
        if (job.Type != JobType.ResyncTable)
        {
            return null;
        }

        var message = job.Events.FirstOrDefault(e => e.Message.StartsWith(JobEventMarkers.ResyncTablePrefix, StringComparison.Ordinal))?.Message;
        if (message is not null)
        {
            var rest = message[JobEventMarkers.ResyncTablePrefix.Length..];
            var modeAt = rest.LastIndexOf(" (", StringComparison.Ordinal);
            return modeAt > 0 ? rest[..modeAt] : rest.TrimEnd('.');
        }

        return job.Entities.Count == 1 ? job.Entities[0].Entity : null;
    }

    private static TimeSpan? Duration(DateTimeOffset? started, DateTimeOffset? finished, FlowStatus status, DateTimeOffset now) =>
        started is null ? null
        : finished is not null ? finished - started
        : status == FlowStatus.Running ? now - started
        : null;

    private static int Percent(double fraction) => (int)Math.Round(100 * fraction);
}
