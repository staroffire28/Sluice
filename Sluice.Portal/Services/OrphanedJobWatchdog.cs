using Sluice.Portal.Models;

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

    private readonly IJobService _jobs;
    private readonly ITaskStore _tasks;

    public OrphanedJobWatchdog(IJobService jobs, ITaskStore tasks)
    {
        _jobs = jobs;
        _tasks = tasks;
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
                "(probable caída de un worker entre etapas). Se marca como Error: usa Reintentar para relanzarlo.");
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
