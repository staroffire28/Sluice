using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class JobProgressHelper
{
    // Las 3 etapas fijas del pipeline (ver Workers/StageRouter.cs, que siempre encadena
    // Ingest → Sql → Reconcile sin importar el JobType). Ponderar por etapa vía StageStatus en
    // vez de contar Job.Entities evita el bug de mostrar "100%" apenas termina Ingest: Entities
    // es una lista plana que cada etapa sobreescribe/mergea SOLO al terminar (ver
    // JobEntityResultMerger), así que justo después de Ingest todas las entidades ya están "ok"
    // aunque SQL/Reconcile no hayan arrancado.
    private static readonly string[] Stages = ["ingest", "sql", "reconcile"];

    // Peso de cada etapa en el porcentaje, según su duración típica observada (un job real:
    // ingest ~22 min, SQL ~8,5 min, reconcile ~25 s). Con pesos iguales el % se quedaba en 33
    // durante casi todo el job.
    private static readonly Dictionary<string, int> StageWeights = new()
    {
        ["ingest"] = 70,
        ["sql"] = 25,
        ["reconcile"] = 5,
    };

    public static int GetPercent(Job job) => GetPercent(job, null);

    // "¿Cuánto del pipeline se recorrió?" (el éxito/fallo lo comunica StatusBadge). Las etapas
    // omitidas (Force/Resync no ingieren) quedan fuera del numerador y del denominador.
    public static int GetPercent(Job job, JobProgress? progress)
    {
        double done = 0;
        double total = 0;
        foreach (var stage in Stages)
        {
            var status = job.StageStatus.GetValueOrDefault(stage);
            if (string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total += StageWeights[stage];
            done += StageWeights[stage] * GetStageFraction(stage, status, progress);
        }

        return total == 0 ? 0 : (int)Math.Round(100 * done / total);
    }

    // Fracción recorrida de una etapa (0..1) según su estado y, si existe, su progress.json.
    internal static double GetStageFraction(string stage, string? status, JobProgress? progress)
    {
        var fromSteps = GetStepsFraction(stage, progress);
        return status?.ToLowerInvariant() switch
        {
            null or "" or "pending" or "claimed" or "blocked" => 0,
            "running" or "cancelled" => fromSteps ?? 0,
            // Sin progreso, una etapa en error cuenta como recorrida (semántica previa).
            "error" => fromSteps ?? 1,
            // done y cualquier otro valor ya resuelto.
            _ => 1,
        };
    }

    // Fracción según los pasos de progress.json ponderados con JobStepCatalog; null si no hay
    // pasos de esa etapa (jobs viejos, executors falsos, etapa aún no empezada).
    private static double? GetStepsFraction(string stage, JobProgress? progress)
    {
        if (progress is null)
        {
            return null;
        }

        double done = 0;
        double total = 0;
        var any = false;
        foreach (var def in JobStepCatalog.ForStage(stage))
        {
            total += def.Weight;
            if (progress.Steps.TryGetValue(def.Key, out var step))
            {
                any = true;
                done += def.Weight * GetStepFraction(step);
            }
        }

        return any && total > 0 ? done / total : null;
    }

    // Un paso con contador cuenta Done/Total aunque haya acabado en error o se interrumpiera:
    // lo que mide el % es cuánto se recorrió, no si salió bien.
    internal static double GetStepFraction(StepProgress step) => step.Status switch
    {
        "done" or "skipped" => 1,
        _ when step.Total > 0 => Math.Clamp((double)step.Done / step.Total, 0, 1),
        _ => 0,
    };

    // ¿El job sigue en curso? Partial no es terminal: un ingest con entidades fallidas deja el
    // job en Partial y el pipeline sigue con SQL/Reconcile; lo que lo cierra es FinishedAt.
    // Criterio único para el builder del flujo, el refresco de la página y Cancelar/Reintentar.
    public static bool IsActive(Job job) => IsActive(job.Status, job.FinishedAt);

    public static bool IsActive(JobStatus status, DateTimeOffset? finishedAt) =>
        status == JobStatus.Running || (status == JobStatus.Partial && finishedAt is null);

    // Etiqueta de la etapa actualmente en curso (para acompañar el %), o null si el job no está
    // corriendo o no hay una etapa "running"/"claimed" identificable en este momento.
    public static string? GetActiveStageKey(Job job)
    {
        if (!IsActive(job))
        {
            return null;
        }

        return Stages.FirstOrDefault(stage =>
            job.StageStatus.TryGetValue(stage, out var status)
            && (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "claimed", StringComparison.OrdinalIgnoreCase)));
    }
}
