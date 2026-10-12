namespace Sluice.Portal.Models;

public sealed record JobEntityResult
{
    public required string Entity { get; init; }
    public int CsvRows { get; init; }
    public required string Status { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? Watermark { get; init; }
    public int? SqlRows { get; init; }
}