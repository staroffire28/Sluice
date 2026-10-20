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
}

public sealed record IncompleteSnapshot
{
    public required string Name { get; init; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset? LastAlertedAt { get; init; }
}

public sealed record TableState
{
    public long RowCount { get; init; }
    public DateTimeOffset? Watermark { get; init; }
    public DateTimeOffset? LastLoad { get; init; }
    public string? LastSnapshot { get; init; }
    public string Status { get; init; } = "ok";
}