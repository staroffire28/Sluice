using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;
using PipelineModel = Sluice.Portal.Models.Pipeline;

namespace Sluice.Portal.Services;

public interface IPipelineService
{
    IReadOnlyList<PipelineModel> List();
    PipelineModel? Get(string id);
    PipelineModel Create(PipelineModel pipeline);
    PipelineModel? Update(string id, PipelineModel pipeline);
    bool Delete(string id, bool wipeState, out string? error);
    PipelineState GetState(string pipelineId);
    IReadOnlyList<object> GetTables(string pipelineId);
}

public sealed class PipelineService : IPipelineService
{
    private readonly PipelineStore _store;
    private readonly PipelineStateStore _stateStore;
    private readonly ConnectionStore _connections;
    private readonly PipelineRunQueue? _runQueue;

    public PipelineService(PipelineStore store, PipelineStateStore stateStore, ConnectionStore connections, PipelineRunQueue? runQueue = null)
    {
        _store = store;
        _stateStore = stateStore;
        _connections = connections;
        _runQueue = runQueue;
    }

    public IReadOnlyList<PipelineModel> List() => _store.ListAll();

    public PipelineModel? Get(string id) => _store.GetById(id);

    public PipelineModel Create(PipelineModel pipeline)
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

    public PipelineModel? Update(string id, PipelineModel pipeline)
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

    public bool Delete(string id, bool wipeState, out string? error)
    {
        error = null;
        if (!_store.Delete(id))
        {
            error = "Pipeline not found.";
            return false;
        }

        if (wipeState)
        {
            _stateStore.Delete(id);
        }

        // Sin pipeline no hay nada que ejecutar, y otro creado luego con el mismo id no debe heredarlo.
        _runQueue?.Remove(id);
        _runQueue?.Complete(id);
        return true;
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