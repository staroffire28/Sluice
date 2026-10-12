namespace Sluice.Portal.Models;

public sealed record DiffReport
{
    public required string Id { get; init; }
    public required string PipelineId { get; init; }
    public required string JobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<DiffEntityResult> Entities { get; init; } = [];
}

public sealed record DiffEntityResult
{
    public required string Entity { get; init; }
    public long SourceRows { get; init; }
    public long DestinationRows { get; init; }
    public long Delta { get; init; }
}