using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;

namespace Sluice.Portal.Services.Stores;

// La cola de ejecuciones con auditoría: lo que la vacía por decisión de alguien (quitarla de la cola)
// queda registrado con su actor. No depende de los servicios de pipeline, así que también la pueden
// usar los que OrchestrationService usa. Encolar lo audita OrchestrationService, que valida antes el
// pipeline y el tipo.
public sealed class RunQueueService
{
    private readonly PipelineRunQueue _queue;
    private readonly IAuditLog _audit;

    public RunQueueService(PipelineRunQueue queue, IAuditLog audit)
    {
        _queue = queue;
        _audit = audit;
    }

    public QueuedRun? Get(string pipelineId) => _queue.Get(pipelineId);

    public QueuedRun? TryEnqueue(string pipelineId, QueuedRun run) => _queue.TryEnqueue(pipelineId, run);

    // Devuelve lo quitado, o null si no había nada en cola (o ya estaba reservado para arrancar).
    public QueuedRun? Dequeue(AuditActor actor, string pipelineId)
    {
        var (queued, removed) = _audit.Audit(
            actor,
            "pipeline.queue.removed",
            pipelineId,
            () => _queue.Get(pipelineId) is { } run ? (run, _queue.Remove(pipelineId)) : ((QueuedRun?)null, false),
            result => result switch
            {
                ({ } run, true) => new AuditResult($"{run.Type} quitado de la cola del pipeline {pipelineId}.",
                    MessageKey: "Activity.Queue.Removed", MessageArgs: [run.Type.ToString(), pipelineId]),
                ({ } run, false) => new AuditResult($"{run.Type} del pipeline {pipelineId} ya está arrancando; no se puede quitar de la cola.", Outcome: AuditOutcome.Rejected,
                    MessageKey: "Activity.Queue.AlreadyStarting", MessageArgs: [run.Type.ToString(), pipelineId]),
                _ => new AuditResult($"No había nada que quitar de la cola del pipeline {pipelineId}.", Outcome: AuditOutcome.Rejected,
                    MessageKey: "Activity.Queue.NothingToRemove", MessageArgs: [pipelineId]),
            },
            $"No se pudo quitar de la cola del pipeline {pipelineId}",
            failureKey: "Activity.Failed.QueueRemove", failureArgs: [pipelineId]);
        return removed ? queued : null;
    }

    // Vacía la cola (lo encolado y lo reservado para arrancar) porque ya no tiene sentido lanzarlo: el
    // pipeline se borró o se reseteó. Solo deja entrada si había algo que descartar.
    public QueuedRun? Discard(AuditActor actor, string pipelineId, string reason)
    {
        var queued = _queue.Get(pipelineId);
        _queue.Remove(pipelineId);
        _queue.Complete(pipelineId);
        if (queued is not null)
        {
            _audit.Record(actor, "pipeline.queue.discarded", AuditOutcome.Ok, pipelineId, null, null,
                $"{queued.Type} descartado de la cola del pipeline {pipelineId}: {reason}.", null,
                "Activity.Queue.Discarded", queued.Type.ToString(), pipelineId, reason);
        }

        return queued;
    }

    public QueuedRun? TryClaim(string pipelineId) => _queue.TryClaim(pipelineId);

    public void Release(string pipelineId) => _queue.Release(pipelineId);

    public void Complete(string pipelineId) => _queue.Complete(pipelineId);
}
