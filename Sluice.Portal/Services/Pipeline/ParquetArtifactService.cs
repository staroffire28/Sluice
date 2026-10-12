using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

// Fachada de gestión del parquet en ADLS para UI y API: resuelve el pipeline a su
// conexión ADLS y acota cada operación al prefijo {pipelineId}/ (evita travesía y
// acceso cruzado entre pipelines).
public interface IParquetArtifactService
{
    Task<IReadOnlyList<ParquetArtifact>> ListAsync(string pipelineId, CancellationToken cancellationToken = default);
    Task<Stream?> OpenAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default);
}

public sealed class ParquetArtifactService : IParquetArtifactService
{
    private readonly IPipelineService _pipelines;
    private readonly IAdlsClientFactory _adlsFactory;

    public ParquetArtifactService(IPipelineService pipelines, IAdlsClientFactory adlsFactory)
    {
        _pipelines = pipelines;
        _adlsFactory = adlsFactory;
    }

    public async Task<IReadOnlyList<ParquetArtifact>> ListAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        var store = await StoreFor(pipelineId, cancellationToken);
        return await store.ListParquetAsync($"{pipelineId}/", cancellationToken);
    }

    public async Task<Stream?> OpenAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default)
    {
        if (!IsWithinPipeline(pipelineId, blobPath))
        {
            return null;
        }

        var store = await StoreFor(pipelineId, cancellationToken);
        return await store.ParquetExistsAsync(blobPath, cancellationToken)
            ? await store.OpenParquetReadAsync(blobPath, cancellationToken)
            : null;
    }

    public async Task<bool> DeleteAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default)
    {
        if (!IsWithinPipeline(pipelineId, blobPath))
        {
            return false;
        }

        var store = await StoreFor(pipelineId, cancellationToken);
        await store.DeleteParquetAsync(blobPath, cancellationToken);
        return true;
    }

    private async Task<IParquetStore> StoreFor(string pipelineId, CancellationToken cancellationToken)
    {
        var pipeline = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{pipelineId}' not found.");
        return await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
    }

    private static bool IsWithinPipeline(string pipelineId, string blobPath) =>
        !string.IsNullOrWhiteSpace(blobPath)
        && !blobPath.Contains("..", StringComparison.Ordinal)
        && blobPath.StartsWith($"{pipelineId}/", StringComparison.Ordinal);
}
