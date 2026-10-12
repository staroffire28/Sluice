namespace Sluice.Portal.Models;

public sealed record PipelineTask
{
    public required string Id { get; init; }
    public required PipelineStage Stage { get; init; }
    public required string PipelineId { get; init; }
    public required string JobId { get; init; }
    public PipelineTaskStatus Status { get; init; } = PipelineTaskStatus.Pending;
    public string? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public Dictionary<string, object?> Payload { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public string? ErrorMessage { get; init; }
}