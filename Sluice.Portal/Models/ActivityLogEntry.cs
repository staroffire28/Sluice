using System.Text.Json.Serialization;

namespace Sluice.Portal.Models;

// Una entrada del registro de actividad (audit log) del portal. Inmutable;
// se serializa una por línea en archivos JSONL bajo {DataDir}/logs.
public sealed record ActivityLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Source { get; init; }   // "user" | "system"
    public required string Level { get; init; }    // "info" | "warning" | "error"
    public required string Action { get; init; }   // clave corta, p.ej. "pipeline.run", "connection.delete"
    public required string Message { get; init; }

    // Texto localizable, como en JobEvent: clave del resx y argumentos, que se pintan en el idioma
    // de quien lo ve (EventText). Message sigue guardando el texto de siempre (CSV, API y entradas
    // anteriores a la clave).
    public string? MessageKey { get; init; }
    public object?[]? MessageArgs { get; init; }
    public string? PipelineId { get; init; }
    public string? JobId { get; init; }

    // Solo en entradas antiguas: guardaba la máquina que la escribió (hoy Host).
    public string? Actor { get; init; }

    // Quién actuó (AuditActor): ui | api | scheduler | queue | autofix | retention | worker | demo.
    public string? Channel { get; init; }

    // IP y hash corto de la sesión de quien actuó (nunca el token).
    public string? Client { get; init; }

    // Máquina (réplica) que escribió la entrada.
    public string? Host { get; init; }

    public AuditOutcome? Outcome { get; init; }

    // Sobre qué se actuó, si no basta con pipeline y job (una tabla, una conexión, una ruta).
    public string? Target { get; init; }

    [JsonIgnore]
    public string? HostName => Host ?? Actor;

    // Para la columna Actor y el CSV: canal y cliente; sin canal (entradas antiguas o registradas
    // sin actor), la máquina que la escribió.
    [JsonIgnore]
    public string? ActorLabel => Channel is null
        ? HostName
        : Client is null ? Channel : $"{Channel} · {Client}";
}

// Rejected: la acción se rechazó por una regla (pipeline ocupado, validación); Error: falló.
public enum AuditOutcome
{
    Ok,
    Rejected,
    Error,
}
