using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public interface ITaskStore
{
    Task EnqueueAsync(PipelineTask task, CancellationToken cancellationToken = default);
    Task<PipelineTask?> TryClaimAsync(PipelineStage stage, string workerId, CancellationToken cancellationToken = default);
    Task<PipelineTask?> GetAsync(string taskId, PipelineStage stage, CancellationToken cancellationToken = default);
    Task<PipelineTask?> HeartbeatAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default);
    Task<PipelineTask?> CompleteAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default);
    Task<PipelineTask?> FailAsync(string taskId, PipelineStage stage, string workerId, string error, CancellationToken cancellationToken = default);
    Task ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default);
    int CountByStatus(PipelineStage stage, PipelineTaskStatus status);

    // Cancelación cooperativa: elimina de la cola las tareas Pending/Claimed de un job
    // (todas las stages) para que no se procesen ni se encolen las siguientes. Devuelve
    // cuántas se eliminaron. La tarea en vuelo termina su trabajo actual; al completar,
    // CompleteAsync no la encuentra y el router no avanza al siguiente stage.
    Task<int> CancelJobTasksAsync(string jobId, CancellationToken cancellationToken = default);
}