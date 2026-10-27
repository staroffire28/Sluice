using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

// Corrección automática (Ajustes → AutoRemediate). La conciliación (conteos o valores) deja en el
// Summary del job las tablas accionables; al cerrar el job, StageRouter lanza UN re-sync en modo
// recarga de esas tablas. El re-sync no se corrige a sí mismo: un intento por corrida.
public static class AutoRemediation
{
    public const string SummaryKey = "autoRemediateEntities";

    // El aviso de conciliación de un job que podía corregirse solo (ReconcileAlert en JSON): lo manda
    // StageRouter después de intentar la corrección, para que diga lo que pasó de verdad.
    public const string PendingAlertKey = "reconcileAlertPending";
    public const string TriggeredByAuto = "auto";
    public const string TriggeredByUser = "user";

    // Diff es informativo y ResyncTable ya es una corrección: ninguno dispara otra.
    public static bool IsEligible(JobType type) => type is not (JobType.ResyncTable or JobType.Diff);

    public static bool IsAutomatic(PipelineTask task) =>
        task.Payload.TryGetValue("auto", out var raw) && string.Equals(raw?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

    // El Summary se guarda en JSON: al releer el job el valor llega como JsonElement; ToString()
    // devuelve el texto en ambos casos.
    public static IReadOnlyList<string> EntitiesFrom(Job job) =>
        job.Summary.TryGetValue(SummaryKey, out var raw) && raw?.ToString() is { Length: > 0 } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
}
