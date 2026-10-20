using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
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
    Task<string?> DeleteAsync(string id, bool wipeState, CancellationToken cancellationToken = default);
    PipelineState GetState(string pipelineId);
    IReadOnlyList<object> GetTables(string pipelineId);
}

public sealed class PipelineService : IPipelineService
{
    private readonly PipelineStore _store;
    private readonly PipelineStateStore _stateStore;
    private readonly ConnectionStore _connections;
    private readonly PipelineRunQueue? _runQueue;
    private readonly IJobService _jobs;
    private readonly ITaskStore _tasks;
    private readonly AppOptions _options;

    public PipelineService(IOptions<AppOptions> options, PipelineStore store, PipelineStateStore stateStore, ConnectionStore connections, IJobService jobs, ITaskStore tasks, PipelineRunQueue? runQueue = null)
    {
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

    public async Task<string?> DeleteAsync(string id, bool wipeState, CancellationToken cancellationToken = default)
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
        _runQueue?.Remove(id);
        _runQueue?.Complete(id);
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