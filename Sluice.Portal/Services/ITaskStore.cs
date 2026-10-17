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

    // Cancelación cooperativa: elimina de la cola las tareas Pending de un job (todas las stages)
    // para que no se procesen. Devuelve cuántas se eliminaron. La Claimed sigue siendo de su worker:
    // su latido ve el job cancelado, detiene el stage y la libera (el router no avanza un job
    // cancelado). Mientras tanto FindLiveClaimAsync la encuentra y el pipeline no admite otro job.
    Task<int> CancelJobTasksAsync(string jobId, CancellationToken cancellationToken = default);

    // Tarea reclamada del pipeline con lease vigente: un worker aún ejecuta ese stage, también el de
    // un job ya cancelado que todavía no se ha detenido. null si no hay ninguna.
    Task<PipelineTask?> FindLiveClaimAsync(string pipelineId, CancellationToken cancellationToken = default);
}