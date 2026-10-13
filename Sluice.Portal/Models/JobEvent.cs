namespace Sluice.Portal.Models;

public sealed record JobEvent
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public EventLevel Level { get; init; } = EventLevel.Info;
    public required string Message { get; init; }
    // Error conocido (KnownErrors) detectado al capturar la excepción; null si no se reconoce.
    public string? Code { get; init; }
}