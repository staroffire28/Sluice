namespace Sluice.Portal.Models;

public sealed record PipelineState
{
    public required string PipelineId { get; init; }
    public string? LastProcessedSnapshot { get; init; }
    public DateTimeOffset? GlobalWatermark { get; init; }
    public Dictionary<string, TableState> Tables { get; init; } = new();
}

public sealed record TableState
{
    public long RowCount { get; init; }
    public DateTimeOffset? Watermark { get; init; }
    public DateTimeOffset? LastLoad { get; init; }
    public string? LastSnapshot { get; init; }
    public string Status { get; init; } = "ok";
}