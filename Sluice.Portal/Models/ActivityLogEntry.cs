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
    public string? PipelineId { get; init; }
    public string? JobId { get; init; }
    public string? Actor { get; init; }            // máquina/proceso o usuario; opcional
}
