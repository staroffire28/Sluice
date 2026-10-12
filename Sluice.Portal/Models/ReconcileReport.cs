namespace Sluice.Portal.Models;

public sealed record ReconcileReport
{
    public required string Id { get; init; }
    public required string PipelineId { get; init; }
    public required string JobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ReconcileEntityResult> Entities { get; init; } = [];
    public double MaxDivergencePct { get; init; }
    public bool IsPartial { get; init; }
}

public sealed record ReconcileEntityResult
{
    public required string Entity { get; init; }
    public long SourceRows { get; init; }
    public long DestinationRows { get; init; }
    public double DivergencePct { get; init; }
}