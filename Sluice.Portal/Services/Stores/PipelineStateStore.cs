using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Stores;

public sealed class PipelineStateStore
{
    private readonly AppOptions _options;
    private readonly object _lock = new();

    public PipelineStateStore(IOptions<AppOptions> options) => _options = options.Value;

    public PipelineState GetOrCreate(string pipelineId)
    {
        lock (_lock)
        {
            var path = GetPath(pipelineId);
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<PipelineState>(stream, JsonDefaults.Options)
                    ?? new PipelineState { PipelineId = pipelineId };
            }

            return new PipelineState { PipelineId = pipelineId };
        }
    }

    public void Save(PipelineState state)
    {
        lock (_lock)
        {
            var directory = GetDirectory(state.PipelineId);
            Directory.CreateDirectory(directory);
            var path = GetPath(state.PipelineId);
            AtomicJsonFile.Write(path, state);
        }
    }

    public void Update(string pipelineId, Func<PipelineState, PipelineState> change)
    {
        lock (_lock)
        {
            Save(change(GetOrCreate(pipelineId)));
        }
    }

    public void Delete(string pipelineId)
    {
        lock (_lock)
        {
            var path = GetPath(pipelineId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string GetDirectory(string pipelineId) =>
        Path.Combine(_options.DataDir, pipelineId, "state");

    private string GetPath(string pipelineId) =>
        Path.Combine(GetDirectory(pipelineId), "pipeline_state.json");
}