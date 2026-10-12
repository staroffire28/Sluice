namespace Sluice.Portal.Models;

// Progreso fino de un job (runs/{jobId}/progress.json). Lo escribe solo el worker que ejecuta
// la etapa en curso; no se copia al Job para no competir con sus Updates de copia completa.
public sealed record JobProgress
{
    public required string JobId { get; init; }
    public string? Stage { get; init; }
    public string? CurrentStep { get; init; }

    // Clave = JobStepCatalog.*
    public Dictionary<string, StepProgress> Steps { get; init; } = new();
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record StepProgress
{
    // pending | running | done | error | skipped | blocked
    public string Status { get; init; } = "pending";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int Done { get; init; }
    public int Total { get; init; }
    public int Failed { get; init; }

    // Filas acumuladas del paso (leídas en snapshots, fusionadas en merge): métricas en vivo
    // antes de que la etapa escriba su Summary.
    public long Rows { get; init; }
    public string? Item { get; init; }
    public string? Phase { get; init; }
    public string? Message { get; init; }
}
