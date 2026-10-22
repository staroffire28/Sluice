namespace Sluice.Portal.Models;

public sealed record JobEvent
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public EventLevel Level { get; init; } = EventLevel.Info;
    public required string Message { get; init; }
    // Error conocido (KnownErrors) detectado al capturar la excepción; null si no se reconoce.
    public string? Code { get; init; }

    // Texto localizable: clave del resx y argumentos (cadenas o números), que se formatean al
    // pintar en el idioma de quien lo ve (EventText). Message sigue guardando el texto de siempre:
    // lo usan la búsqueda, la API, los marcadores de JobFlowBuilder y los eventos anteriores a
    // la clave, que se pintan con él.
    public string? MessageKey { get; init; }
    public object?[]? MessageArgs { get; init; }
}