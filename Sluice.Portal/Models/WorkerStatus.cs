namespace Sluice.Portal.Models;

public sealed record WorkerStatus
{
    public required string Id { get; init; }
    public required PipelineStage Stage { get; init; }
    public WorkerState Status { get; init; } = WorkerState.Idle;
    public string? TaskId { get; init; }
    public string? PipelineId { get; init; }
    public string? JobId { get; init; }
    public DateTimeOffset? Since { get; init; }
}