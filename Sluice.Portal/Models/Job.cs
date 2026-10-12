namespace Sluice.Portal.Models;

public sealed record Job
{
    public required string Id { get; init; }
    public required string PipelineId { get; init; }
    public JobType Type { get; init; } = JobType.Incremental;
    public JobStatus Status { get; init; } = JobStatus.Running;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; init; }
    public PipelineStage? CurrentStage { get; init; }
    public Dictionary<string, string> StageStatus { get; init; } = new();

    // Claves ingest/sql/reconcile. Cada executor escribe inicio y fin en sus Updates de
    // transición (ver JobStageTimings). Jobs anteriores no lo traen: la UI los reconstruye
    // desde los eventos.
    public Dictionary<string, StageTiming> StageTimings { get; init; } = new();
    public Dictionary<string, string> WorkerAssignments { get; init; } = new();
    public List<JobEntityResult> Entities { get; init; } = [];
    public List<JobEvent> Events { get; init; } = [];
    public Dictionary<string, object?> Summary { get; init; } = new();

    // Concurrencia optimista: se incrementa en cada guardado (JobStore.TrySave/JobService).
    // Job.json puede ser escrito por varios workers de stage + la API de cancelación; sin
    // esto, un read-modify-write concurrente podía pisar silenciosamente al otro escritor.
    public int Version { get; init; }
}