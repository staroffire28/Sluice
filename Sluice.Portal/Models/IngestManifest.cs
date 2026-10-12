namespace Sluice.Portal.Models;

public sealed record IngestManifest
{
    public required string JobId { get; init; }
    public required string PipelineId { get; init; }
    public string? LastProcessedSnapshot { get; init; }
    public List<IngestEntityManifest> Entities { get; init; } = [];
}

public sealed record IngestEntityManifest
{
    public required string Entity { get; init; }
    public required string ParquetPath { get; init; }
    public required string Snapshot { get; init; }
    public int CsvRows { get; init; }
    public int FilteredRows { get; init; }
    public DateTimeOffset? Watermark { get; init; }
    public long SourceRowCount { get; init; }
    public string? SchemaPath { get; init; }

    // El ingest dejó un parquet delta (solo las filas de esta corrida) en DATA_DIR; la ruta la
    // deriva cada lector con IngestManifestStore.GetDeltaPath (DATA_DIR puede montarse distinto).
    public bool HasDelta { get; init; }
}