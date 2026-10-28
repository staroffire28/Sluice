using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using PipelineModel = Sluice.Portal.Models.Pipeline;

namespace Sluice.Portal.Services.Stores;

public sealed class PipelineStore : JsonFileStore<PipelineModel>
{
    public PipelineStore(IOptions<AppOptions> options, ILogger<PipelineStore>? logger = null) : base(options, "pipelines", logger)
    {
    }

    public IReadOnlyList<PipelineModel> ListAll() => ListAll(Read);

    public PipelineModel? GetById(string id) => Get(id);

    public void Save(PipelineModel pipeline) => Save(pipeline.Id, pipeline);

    public void SaveNew(PipelineModel pipeline) => SaveNew(pipeline.Id, pipeline);

    public new bool Delete(string id) => base.Delete(id);
}