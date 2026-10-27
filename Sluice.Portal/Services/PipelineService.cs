using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;
using PipelineModel = Sluice.Portal.Models.Pipeline;

namespace Sluice.Portal.Services;

public interface IPipelineService
{
    IReadOnlyList<PipelineModel> List();
    PipelineModel? Get(string id);
    // Lo que muta recibe quién actúa y queda en el registro de actividad (ver AuditActor).
    PipelineModel Create(AuditActor actor, PipelineModel pipeline);
    // Cambiar el origen o el destino con un job en curso lanza PipelineBusyException.
    Task<PipelineModel?> UpdateAsync(AuditActor actor, string id, PipelineModel pipeline, CancellationToken cancellationToken = default);

    // Activa o desactiva el scheduler del pipeline (null si no existe).
    PipelineModel? SetScheduler(AuditActor actor, string id, bool enabled);

    // null si se borró; si no, el motivo.
    Task<string?> DeleteAsync(AuditActor actor, string id, bool wipeState, CancellationToken cancellationToken = default);
    PipelineState GetState(string pipelineId);
    IReadOnlyList<object> GetTables(string pipelineId);
}

public sealed class PipelineService : IPipelineService
{
    private readonly PipelineStore _store;
    private readonly PipelineStateStore _stateStore;
    private readonly ConnectionStore _connections;
    private readonly RunQueueService? _runQueue;
    private readonly IAuditLog _audit;
    private readonly IJobService _jobs;
    private readonly ITaskStore _tasks;
    private readonly AppOptions _options;

    public PipelineService(IOptions<AppOptions> options, PipelineStore store, PipelineStateStore stateStore, ConnectionStore connections, IJobService jobs, ITaskStore tasks, IAuditLog audit, RunQueueService? runQueue = null)
    {
        _audit = audit;
        _options = options.Value;
        _store = store;
        _stateStore = stateStore;
        _connections = connections;
        _runQueue = runQueue;
        _jobs = jobs;
        _tasks = tasks;
    }

    public IReadOnlyList<PipelineModel> List() => _store.ListAll();

    public PipelineModel? Get(string id) => _store.GetById(id);

    public PipelineModel Create(AuditActor actor, PipelineModel pipeline) =>
        _audit.Audit(
            actor,
            "pipeline.create",
            pipeline.Id,
            () => CreateCore(pipeline),
            created => new AuditResult($"Pipeline {created.Name} creado.", MessageKey: "Activity.Pipeline.Created", MessageArgs: [created.Name]),
            $"No se pudo crear el pipeline {pipeline.Name}",
            failureKey: "Activity.Failed.PipelineCreate", failureArgs: [pipeline.Name]);

    private PipelineModel CreateCore(PipelineModel pipeline)
    {
        if (!EntityIds.IsValid(pipeline.Id))
        {
            throw new InvalidOperationException($"Id de pipeline no válido: '{pipeline.Id}'.");
        }

        if (EntityIds.IsReservedPipelineId(pipeline.Id))
        {
            throw new InvalidOperationException(
                $"Id de pipeline reservado: '{pipeline.Id}' coincide con una carpeta del sistema o empieza por '_' o '.'.");
        }

        ValidateRefs(pipeline);
        _store.SaveNew(pipeline);
        _stateStore.GetOrCreate(pipeline.Id);
        return pipeline;
    }

    // La entrada dice qué campos cambiaron (AuditChanges).
    public async Task<PipelineModel?> UpdateAsync(AuditActor actor, string id, PipelineModel pipeline, CancellationToken cancellationToken = default) =>
        (await _audit.AuditAsync(
            actor,
            "pipeline.update",
            id,
            () => UpdateCoreAsync(id, pipeline, cancellationToken),
            edit => edit is not { } done
                ? new AuditResult($"El pipeline {id} no existe.", Outcome: AuditOutcome.Rejected, MessageKey: "Activity.Pipeline.NotFound", MessageArgs: [id])
                : AuditChanges.Between(AuditChanges.Fields(done.Before, "id", "createdAt"), AuditChanges.Fields(done.After, "id", "createdAt"), _ => false) is { } changes
                    ? new AuditResult($"Pipeline {done.After.Name} actualizado: {changes.Text}.", MessageKey: "Activity.Pipeline.UpdatedChanges", MessageArgs: [done.After.Name, changes.Part])
                    : new AuditResult($"Pipeline {done.After.Name} actualizado.", MessageKey: "Activity.Pipeline.Updated", MessageArgs: [done.After.Name]),
            $"No se pudo guardar el pipeline {pipeline.Name}",
            failureKey: "Activity.Failed.PipelineSave", failureArgs: [pipeline.Name]))?.After;

    public PipelineModel? SetScheduler(AuditActor actor, string id, bool enabled) =>
        _audit.Audit(
            actor,
            "pipeline.scheduler",
            id,
            () => _store.GetById(id) is { } pipeline ? UpdateCore(id, pipeline with { Enabled = enabled }) : null,
            updated => updated is null
                ? new AuditResult($"El pipeline {id} no existe.", Outcome: AuditOutcome.Rejected, MessageKey: "Activity.Pipeline.NotFound", MessageArgs: [id])
                : new AuditResult($"Scheduler {(enabled ? "activado" : "desactivado")} en el pipeline {updated.Name}.",
                    MessageKey: "Activity.Pipeline.SchedulerChanged",
                    MessageArgs: [new EventPart(enabled ? "Activity.Pipeline.SchedulerOn" : "Activity.Pipeline.SchedulerOff"), updated.Name]),
            $"No se pudo cambiar el scheduler del pipeline {id}",
            failureKey: "Activity.Failed.PipelineScheduler", failureArgs: [id]);

    // Con otro origen o destino, un job en curso acabaría entre el sitio viejo y el nuevo (p. ej. el
    // SQL mergea solo el delta en el destino nuevo): se rechaza bajo el lock de ejecución, como el borrado.
    private async Task<(PipelineModel Before, PipelineModel After)?> UpdateCoreAsync(string id, PipelineModel pipeline, CancellationToken cancellationToken)
    {
        if (_store.GetById(id) is not { } existing)
        {
            return null;
        }

        if (existing.SourceId == pipeline.SourceId && existing.DestinationId == pipeline.DestinationId)
        {
            return UpdateCore(id, pipeline) is { } updated ? (existing, updated) : null;
        }

        using var runLock = await PipelineRunLock.AcquireAsync(_options.DataDir, id, PipelineRunLock.DefaultTimeout, cancellationToken);
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, id,
            "Hay un job en curso para este pipeline: espera a que termine o cancélalo antes de cambiar su origen o su destino.", cancellationToken);
        return UpdateCore(id, pipeline) is { } moved ? (existing, moved) : null;
    }

    private PipelineModel? UpdateCore(string id, PipelineModel pipeline)
    {
        if (!_store.Exists(id))
        {
            return null;
        }

        ValidateRefs(pipeline with { Id = id });
        var updated = pipeline with { Id = id };
        _store.Save(updated);
        return updated;
    }

    public Task<string?> DeleteAsync(AuditActor actor, string id, bool wipeState, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "pipeline.delete",
            id,
            () => DeleteCoreAsync(actor, id, wipeState, cancellationToken),
            error => error is null
                ? new AuditResult($"Pipeline {id} eliminado{(wipeState ? " (con borrado de estado)" : "")}.",
                    MessageKey: "Activity.Pipeline.Deleted", MessageArgs: [id, wipeState ? new EventPart("Activity.Pipeline.WithStateWipe") : ""])
                : new AuditResult($"No se borró el pipeline {id}: {error}", Outcome: AuditOutcome.Rejected,
                    MessageKey: "Activity.Pipeline.NotDeleted", MessageArgs: [id, error]),
            $"No se pudo borrar el pipeline {id}",
            failureKey: "Activity.Failed.PipelineDelete", failureArgs: [id]);

    private async Task<string?> DeleteCoreAsync(AuditActor actor, string id, bool wipeState, CancellationToken cancellationToken)
    {
        if (!_store.Exists(id))
        {
            return "Pipeline not found.";
        }

        // Borrarlo bajo un job en curso dejaría a ese job sin pipeline a mitad de etapa y, con
        // wipeState, su cierre volvería a escribir el estado recién borrado. El lock de ejecución
        // evita además borrarlo a mitad de un arranque o de un reset.
        using var runLock = await PipelineRunLock.AcquireAsync(_options.DataDir, id, PipelineRunLock.DefaultTimeout, cancellationToken);
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, id,
            "Hay un job en curso para este pipeline: espera a que termine o cancélalo antes de borrarlo.", cancellationToken);

        if (!_store.Delete(id))
        {
            return "Pipeline not found.";
        }

        if (wipeState)
        {
            _stateStore.Delete(id);
        }

        // Sin pipeline no hay nada que ejecutar, y otro creado luego con el mismo id no debe heredarlo.
        _runQueue?.Discard(actor, id, "el pipeline se borró");
        return null;
    }

    public PipelineState GetState(string pipelineId) => _stateStore.GetOrCreate(pipelineId);

    public IReadOnlyList<object> GetTables(string pipelineId)
    {
        var state = _stateStore.GetOrCreate(pipelineId);
        return state.Tables.Select(kv => (object)new
        {
            entity = kv.Key,
            rowCount = kv.Value.RowCount,
            watermark = kv.Value.Watermark,
            lastLoad = kv.Value.LastLoad,
            lastSnapshot = kv.Value.LastSnapshot,
            status = kv.Value.Status,
        }).ToList();
    }

    private void ValidateRefs(PipelineModel pipeline)
    {
        if (_connections.GetById(pipeline.SourceId) is null)
        {
            throw new InvalidOperationException("Source connection not found.");
        }

        if (_connections.GetById(pipeline.DestinationId) is null)
        {
            throw new InvalidOperationException("Destination connection not found.");
        }
    }
}