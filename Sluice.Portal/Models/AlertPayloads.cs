namespace Sluice.Portal.Models;

public sealed record PipelineFailureAlert
{
    public required string PipelineId { get; init; }
    public string? PipelineName { get; init; }
    public required string JobId { get; init; }
    public required string JobType { get; init; }
    public required string Stage { get; init; }
    public required string Message { get; init; }

    // "error" (el job falló) o "warning" (Partial: parte de las tablas sí cargó), para que el
    // receptor distinga un aviso de un fallo.
    public string Status { get; init; } = Error;

    public const string Error = "error";
    public const string Warning = "warning";
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

public sealed record AlertDeliveryResult(bool Success, string? Detail, int? StatusCode)
{
    // Sin webhook configurado la alerta solo va al log: no cuenta como entregada, así que un aviso de
    // episodio sigue pendiente y llega si después se configura el webhook.
    public static readonly AlertDeliveryResult NotConfigured = new(false, "No webhook configured.", null);
}

// Última entrega al webhook (DATA_DIR/alerts/status.json), compartida por la api y los workers.
public sealed record AlertDeliveryStatus
{
    public required DateTimeOffset LastAttemptAt { get; init; }
    public required bool LastSucceeded { get; init; }
    public string? LastEventType { get; init; }
    public string? LastDetail { get; init; }
    public int? LastStatusCode { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }

    // Intentos de entrega fallidos desde la última entrega correcta, incluidos los reintentos de
    // las alertas deduplicadas en cada poll. El aviso de recuperación los cita y recuerda que las
    // de un solo disparo (fallo, Partial, divergencia...) no se reenvían.
    public int FailedSinceLastSuccess { get; init; }
}