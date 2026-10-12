using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

// Vista del flujo de un job para la UI (lienzo de etapas, cabecera, porcentaje). La construye
// JobFlowBuilder a partir del Job y de su progress.json; no se persiste.
// Textos por convención: JobFlow.Step.{Key} y JobFlow.Step.{Key}.Hint para los pasos,
// Common.Stage.* para las etapas y JobFlow.Status.{FlowStatus} para los estados.

// Estado visible (solo UI). Normaliza lo guardado en Job.StageStatus y progress.json: etapas que
// nunca se alcanzan tras un Error/Blocked, "sin snapshots nuevos", trabajo interrumpido...
public enum FlowStatus
{
    Pending,
    Running,
    Done,
    NoNewData,
    Warning,
    Error,
    Blocked,
    Cancelled,
    Skipped,
    NotReached,
    Interrupted,
}

// Dato del panel de detalle: Number se formatea con N0, Percent con P2 (0,0153 = 1,53 %).
public sealed record FlowFact(string LabelKey, long? Number = null, double? Percent = null, string? Text = null);

public sealed record FlowStepView(
    string Key,
    string StageKey,
    FlowStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    TimeSpan? Duration,          // en curso: ahora - StartedAt
    StepUnit Unit,
    int Done,
    int Total,                   // 0 = sin contador
    int Failed,                  // entidades con error en este paso
    string? Item,                // snapshot/entidad en curso
    string? Phase,               // fase dentro de merge: ensureTable|merge|orphans|count
    int Percent,
    IReadOnlyList<FlowFact> Facts,
    string? Message,             // último mensaje relevante (error del paso, aviso...)
    long Rows = 0);              // filas acumuladas (leídas en snapshots, fusionadas en merge)

public sealed record FlowStageView(
    string Key,                  // ingest | sql | reconcile
    FlowStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    TimeSpan? Duration,
    int Percent,
    string? SkipReasonKey,       // etapa omitida: JobFlow.Skip.Force | Resync | Reconcile | Diff
    IReadOnlyList<FlowStepView> Steps);

public sealed record JobFlowView(
    JobType Type,
    string? ScopeEntity,         // Resync de una tabla
    IReadOnlyList<FlowStageView> Stages,
    string? CurrentStepKey,
    int Percent)
{
    public IEnumerable<FlowStepView> Steps => Stages.SelectMany(s => s.Steps);
}
