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
    // Qué hacer en SQL con las filas de borrado de F&O (IsDelete = True). Los pipelines guardados
    // antes de existir el ajuste no traen la clave y quedan en Delete.
    public DeleteMode DeleteMode { get; init; } = DeleteMode.Delete;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum DeleteMode
{
    // Se borran en SQL y no cuentan como filas en reconcile ni en Comparar valores.
    Delete = 0,

    // Se cargan como cualquier otra fila: IsDelete = 1 y el resto de columnas a NULL.
    Ignore = 1,
}