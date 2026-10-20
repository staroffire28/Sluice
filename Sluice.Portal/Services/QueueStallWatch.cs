using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Cola parada en distribuido: una etapa con tareas pendientes y ningún worker vivo de esa etapa. El
// job sigue "en curso", el vigilante de huérfanos no lo toca (su tarea está viva en pending/) y el
// dead-man sigue llegando desde la api, así que nadie se enteraba. Lo llama el scheduler en cada
// poll; avisa una vez por episodio cuando dura más de Threshold y se rearma al volver un worker o
// vaciarse la cola. El aviso cuenta como dado solo cuando el scheduler lo entrega (MarkReported):
// si el webhook falla, la etapa se vuelve a devolver en el siguiente poll. Solo sirve en
// distribuido: ahí cada worker tiene una sola etapa (WORKER_STAGE) y el registro la refleja; en
// local el worker es el propio proceso del scheduler.
public sealed class QueueStallWatch
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(15);

    private readonly ITaskStore _tasks;
    private readonly WorkerRegistry _workers;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly Dictionary<PipelineStage, DateTimeOffset> _stalledSince = [];
    private readonly HashSet<PipelineStage> _noticed = [];
    private readonly HashSet<PipelineStage> _reported = [];

    public QueueStallWatch(ITaskStore tasks, WorkerRegistry workers, SettingsStore settings, TimeProvider? time = null)
    {
        _tasks = tasks;
        _workers = workers;
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    // Etapas paradas cuyo aviso aún no se ha entregado en este episodio. FirstNotice: es la primera
    // vez que se devuelve (para registrarla una sola vez aunque la entrega falle y se reintente).
    public IReadOnlyList<StalledStage> Check()
    {
        var now = _time.GetUtcNow();
        var alive = _workers.ListAlive(WorkerRegistry.AliveWithin(_settings.Get().TaskLeaseSeconds))
            .Select(w => w.Stage)
            .ToHashSet();
        var stalled = new List<StalledStage>();
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            var pending = _tasks.CountByStatus(stage, PipelineTaskStatus.Pending);
            if (pending == 0 || alive.Contains(stage))
            {
                _stalledSince.Remove(stage);
                _noticed.Remove(stage);
                _reported.Remove(stage);
                continue;
            }

            if (!_stalledSince.TryGetValue(stage, out var since))
            {
                _stalledSince[stage] = now;
                continue;
            }

            if (now - since >= Threshold && !_reported.Contains(stage))
            {
                stalled.Add(new StalledStage(stage, pending, since, FirstNotice: _noticed.Add(stage)));
            }
        }

        return stalled;
    }

    // El aviso de la etapa llegó: no se repite hasta que el episodio termine.
    public void MarkReported(PipelineStage stage) => _reported.Add(stage);
}

public sealed record StalledStage(PipelineStage Stage, int PendingTasks, DateTimeOffset Since, bool FirstNotice);
