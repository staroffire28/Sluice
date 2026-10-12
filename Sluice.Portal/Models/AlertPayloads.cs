namespace Sluice.Portal.Models;

public sealed record PipelineFailureAlert
{
    public required string PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public required string JobId { get; init; }
    public required string JobType { get; init; }
    public required string Stage { get; init; }
    public required string Message { get; init; }
}

// Alerta de conciliación: divergencia de conteos (reconcile.divergence) o de valores
// (reconcile.values). Entities = tablas accionables (sobre el umbral y no aceptadas).
public sealed record ReconcileAlert
{
    public const string Divergence = "reconcile.divergence";
    public const string Values = "reconcile.values";

    public required string EventType { get; init; }
    public required string PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public required string JobId { get; init; }
    public required string Message { get; init; }
    public double? MaxDivergencePct { get; init; }
    public IReadOnlyList<string> Entities { get; init; } = [];

    // El job puede disparar la corrección automática (no es un re-sync ni un Diff y hay tablas).
    public bool AutoFixEligible { get; init; }

    // El job ES la corrección automática y no resolvió la divergencia.
    public bool AutoFixAttempted { get; init; }
}

public sealed record AlertDeliveryResult(bool Success, string? Detail, int? StatusCode);