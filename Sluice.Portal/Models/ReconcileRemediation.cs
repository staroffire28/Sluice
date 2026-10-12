namespace Sluice.Portal.Models;

// Registro de una acción de remediación (re-sincronización por tabla) disparada
// por el usuario sobre una divergencia de conciliación. La divergencia "después"
// no se guarda aquí: es el ReconcileReport con Id = recon-{JobId}, joinable por JobId.
public sealed record ReconcileRemediation
{
    public required string Id { get; init; }
    public required string PipelineId { get; init; }
    public required string Entity { get; init; }
    public required string JobId { get; init; }
    public ResyncMode Mode { get; init; }
    public DateTimeOffset TriggeredAt { get; init; } = DateTimeOffset.UtcNow;
    public string? TriggeredBy { get; init; }
    public double DivergenceBefore { get; init; }
}

// Aceptación explícita de una divergencia legítima (p. ej. una tabla que difiere
// del origen por diseño). Suprime la alerta/banner mientras la divergencia se
// mantenga cerca del valor aceptado (ver ReconcileExecutor: la supresión decae si
// la divergencia se aparta más allá del umbral configurado).
public sealed record ReconAck
{
    public double AckPct { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}
