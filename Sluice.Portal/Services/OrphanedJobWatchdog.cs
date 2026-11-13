using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Services;

// Un job queda huérfano si un worker cae entre pasos no atómicos (complete -> route,
// create -> enqueue): sigue en Running sin ninguna tarea pendiente ni en curso y bloquea para
// siempre el Run manual y el scheduler de su pipeline. Lo ejecuta el scheduler (solo en el
// orquestador) en cada poll y lo marca como Error para que se pueda reintentar.
public sealed class OrphanedJobWatchdog
{
    // Margen sin actividad antes de declararlo huérfano: cubre con holgura la ventana normal
    // entre pasos (milisegundos), y un stage largo no cuenta porque su tarea está en claimed/.
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    // Job en curso sin actividad nueva (evento, progreso de etapa) durante este tiempo: se avisa,
    // sin cambiar su estado. El propio aviso cuenta como actividad, así que se repite cada 6 h.
    public static readonly TimeSpan StallThreshold = TimeSpan.FromHours(6);

    private readonly IJobService _jobs;
    private readonly ITaskStore _tasks;
    private readonly JobProgressStore? _progress;

    public OrphanedJobWatchdog(IJobService jobs, ITaskStore tasks, JobProgressStore? progress = null)
    {
        _jobs = jobs;
        _tasks = tasks;
        _progress = progress;
    }

    public sealed record ReclaimCappedJob(Job Job, PipelineStage Stage);

    public sealed record StalledJob(Job Job, string Stage, TimeSpan Idle);

    // Job en curso cuya tarea agotó sus reclamaciones por lease vencido (TaskStore la dejó en error/):
    // nada más lo cerraría, porque el worker que la ejecutaba cayó. Se marca como Error sin esperar a
    // Grace, con su propio evento. Devuelve los que marcó, para que el scheduler avise de cada uno.
    public async Task<IReadOnlyList<ReclaimCappedJob>> SweepReclaimCappedAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var marked = new List<ReclaimCappedJob>();
        foreach (var job in _jobs.ListRunning())
        {
            if (await FindReclaimCappedStageAsync(job.Id, cancellationToken) is not { } stage)
            {
                continue;
            }

            // Releer justo antes de escribir: si terminó o lo cancelaron entretanto, no se pisa.
            var current = _jobs.Get(job.Id);
            if (current is null || !JobProgressHelper.IsActive(current))
            {
                continue;
            }

            var stageKey = stage.ToString().ToLowerInvariant();
            var failed = current with
            {
                Status = JobStatus.Error,
                FinishedAt = now,
                StageStatus = new Dictionary<string, string>(current.StageStatus) { [stageKey] = "error" },
            };
            _jobs.Update(current.Id, failed);
            _jobs.AppendEvent(current.Id, EventLevel.Error,
                $"La etapa {stageKey} se reclamó {TaskStore.MaxLeaseReclaims} veces sin terminar: el worker cae (o se reinicia) " +
                "mientras la ejecuta. Se marca como Error: usa Reintentar para relanzarlo.",
                null, "Event.Job.LeaseReclaimCap", stageKey, TaskStore.MaxLeaseReclaims);
            marked.Add(new ReclaimCappedJob(failed, stage));
        }

        return marked;
    }

    // Job en curso sin actividad nueva desde hace StallThreshold: inicio, último evento o, si lo hay,
    // la última escritura de su progress.json (un MERGE largo que avanza no escribe eventos). No cambia
    // su estado: le añade un evento Warning, que cuenta como actividad, y devuelve los avisados para
    // que el scheduler mande la alerta.
    public IReadOnlyList<StalledJob> WarnStalled(DateTimeOffset now)
    {
        var warned = new List<StalledJob>();
        foreach (var job in _jobs.ListRunning())
        {
            var idle = now - LastActivity(job);
            if (idle < StallThreshold)
            {
                continue;
            }

            var current = _jobs.Get(job.Id);
            if (current is null || !JobProgressHelper.IsActive(current))
            {
                continue;
            }

            var stage = current.CurrentStage?.ToString().ToLowerInvariant() ?? "?";
            _jobs.AppendEvent(current.Id, EventLevel.Warning,
                $"Sin actividad desde hace {idle.TotalHours:0} h en la etapa {stage}; el job sigue en curso. " +
                "Revisa el worker de esa etapa: si no avanza, cancélalo y usa Reintentar.",
                null, "Event.Job.Stalled", idle.TotalHours, stage);
            warned.Add(new StalledJob(current, stage, idle));
        }

        return warned;
    }

    private DateTimeOffset LastActivity(Job job)
    {
        var last = job.Events.Count > 0 ? new[] { job.StartedAt, job.Events.Max(e => e.At) }.Max() : job.StartedAt;
        var progress = _progress?.Load(job.PipelineId, job.Id)?.UpdatedAt;
        return progress > last ? progress.Value : last;
    }

    private async Task<PipelineStage?> FindReclaimCappedStageAsync(string jobId, CancellationToken cancellationToken)
    {
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            try
            {
                var task = await _tasks.GetAsync($"task-{jobId}-{stage.ToString().ToLowerInvariant()}", stage, cancellationToken);
                if (task is { Status: PipelineTaskStatus.Error } && task.LeaseReclaims >= TaskStore.MaxLeaseReclaims)
                {
                    return stage;
                }
            }
            catch (IOException)
            {
                // Archivo bloqueado por una réplica que lo está moviendo: se mira en el próximo poll.
            }
        }

        return null;
    }

    // Devuelve los jobs que marcó, para que el scheduler avise de cada uno.
    public async Task<IReadOnlyList<Job>> SweepAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var marked = new List<Job>();
        foreach (var job in _jobs.ListRunning())
        {
            var lastActivity = job.Events.Count > 0
                ? new[] { job.StartedAt, job.Events.Max(e => e.At) }.Max()
                : job.StartedAt;
            if (now - lastActivity < Grace || await HasLiveTaskAsync(job.Id, cancellationToken))
            {
                continue;
            }

            // Releer justo antes de escribir: si terminó o lo cancelaron entretanto, no se pisa.
            // Un Partial sin FinishedAt también se cierra como Error: no llegó a terminar sus
            // etapas, y el fallo parcial del ingest sigue visible en StageStatus.
            var current = _jobs.Get(job.Id);
            if (current is null || !JobProgressHelper.IsActive(current))
            {
                continue;
            }

            var orphaned = current with { Status = JobStatus.Error, FinishedAt = now };
            _jobs.Update(current.Id, orphaned);
            _jobs.AppendEvent(current.Id, EventLevel.Error,
                $"Sin ninguna tarea pendiente ni en curso desde hace más de {Grace.TotalMinutes:0} min " +
                "(probable caída de un worker entre etapas). Se marca como Error: usa Reintentar para relanzarlo.",
                null, "Event.Job.Orphaned", Grace.TotalMinutes);
            marked.Add(orphaned);
        }

        return marked;
    }

    // Las tareas tienen id determinista task-{jobId}-{stage}.
    private async Task<bool> HasLiveTaskAsync(string jobId, CancellationToken cancellationToken)
    {
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            try
            {
                var task = await _tasks.GetAsync($"task-{jobId}-{stage.ToString().ToLowerInvariant()}", stage, cancellationToken);
                if (task?.Status is PipelineTaskStatus.Pending or PipelineTaskStatus.Claimed)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Archivo bloqueado por una réplica que lo está moviendo: hay actividad, no es huérfano.
                return true;
            }
        }

        return false;
    }
}
