namespace Sluice.Portal.Models;

// Inicio y fin reales de una etapa (ingest/sql/reconcile), registrados por su executor.
public sealed record StageTiming
{
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}
