namespace Sluice.Portal.Models;

public sealed record PipelineState
{
    public required string PipelineId { get; init; }
    public string? LastProcessedSnapshot { get; init; }
    public DateTimeOffset? GlobalWatermark { get; init; }
    public Dictionary<string, TableState> Tables { get; init; } = new();

    // Tablas cuyo parquet volvió a un punto de restauración y SQL aún no ha recargado. Sobrevive a
    // un restore que falla a medias (el reintento las recarga) y la vacía la recarga de SQL.
    public List<string> PendingReload { get; init; } = [];

    // Snapshot cerrado pero incompleto que frena el pipeline (el ingest se para en él). Lo vigila el
    // scheduler: alerta tras una gracia y cada 24 h mientras siga igual. Se rearma al avanzar.
    public IncompleteSnapshot? IncompleteSnapshot { get; init; }

    // Restore a medias: la versión que recupera cada tabla y su estado de entonces, guardados antes de
    // mover el fichero. Mover la versión borra su sidecar, así que si el proceso cae antes de guardar el
    // TableState el reintento termina la tabla desde aquí: el plan recalculado ya no vería esa versión
    // y archivaría el consolidado correcto.
    public Dictionary<string, PendingRestoreTable> PendingRestore { get; init; } = new();

    // Restore en curso: se guarda antes de mover la primera tabla y se borra cuando ya no queda nada por
    // mover ni archivar. Los reintentos etiquetan lo que archivan con el snapshot de antes del restore,
    // porque el estado guardado ya está rebobinado al punto pedido.
    public RestoreInProgress? RestoreInProgress { get; init; }
}

public sealed record RestoreInProgress
{
    public string? FromSnapshot { get; init; }
}

public sealed record IncompleteSnapshot
{
    public required string Name { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset? LastAlertedAt { get; init; }
}

public sealed record PendingRestoreTable
{
    public required string VersionId { get; init; }
    public TableState? Table { get; init; }

    // Estado de la tabla antes del restore: etiqueta el consolidado que se archiva al moverla.
    public TableState? FromTable { get; init; }
}

public sealed record TableState
{
    public long RowCount { get; init; }
    public DateTimeOffset? Watermark { get; init; }
    public DateTimeOffset? LastLoad { get; init; }
    public string? LastSnapshot { get; init; }
    public string Status { get; init; } = "ok";
}