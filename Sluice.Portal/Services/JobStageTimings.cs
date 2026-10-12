using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class JobStageTimings
{
    // Devuelve una copia con la etapa actualizada (Job es inmutable y los executors guardan
    // copias completas). Lo no indicado se conserva; un nuevo inicio borra el fin anterior
    // porque describe un intento que ya no es el actual (stage reintentado).
    public static Dictionary<string, StageTiming> Set(
        this IReadOnlyDictionary<string, StageTiming> timings,
        string stage,
        DateTimeOffset? started = null,
        DateTimeOffset? finished = null)
    {
        var copy = new Dictionary<string, StageTiming>(timings);
        var timing = copy.GetValueOrDefault(stage) ?? new StageTiming();

        if (started is not null)
        {
            timing = timing with { StartedAt = started, FinishedAt = null };
        }

        if (finished is not null)
        {
            timing = timing with { FinishedAt = finished };
        }

        copy[stage] = timing;
        return copy;
    }
}
