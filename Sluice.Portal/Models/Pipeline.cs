namespace Sluice.Portal.Models;

public sealed record Pipeline
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string SourceId { get; init; }
    public required string DestinationId { get; init; }
    // "Scheduler activo": si true, el scheduler auto-ejecuta este pipeline cuando hay
    // snapshots nuevos. NO afecta el Run manual, que siempre se permite. Nace en false:
    // primero se corre a mano para validar y luego se activa el scheduler.
    public bool Enabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}