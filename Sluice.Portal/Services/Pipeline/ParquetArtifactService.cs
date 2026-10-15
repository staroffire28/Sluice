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

    // Versiones del consolidado (ParquetVersions). Por defecto sin versiones, para los dobles de test.
    Task<IReadOnlyList<ParquetVersion>> ListVersionsAsync(string pipelineId, string entity, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ParquetVersion>>([]);

    Task<IReadOnlyList<string>> ListVersionedEntitiesAsync(string pipelineId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    Task<int> DeleteVersionsAsync(string pipelineId, CancellationToken cancellationToken = default) => Task.FromResult(0);

    // Vuelve a poner la versión como consolidado vigente; el vigente pasa a ser una versión más
    // (archiveCurrentAs), así que restaurar también se puede deshacer. Devuelve la restaurada.
    Task<ParquetVersion> RestoreVersionAsync(
        string pipelineId, string entity, string versionId, ParquetVersion archiveCurrentAs, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
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

    public async Task<IReadOnlyList<ParquetVersion>> ListVersionsAsync(string pipelineId, string entity, CancellationToken cancellationToken = default)
    {
        EnsureEntityName(entity);
        var store = await StoreFor(pipelineId, cancellationToken);
        return await ParquetVersions.ListAsync(store, pipelineId, entity, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListVersionedEntitiesAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        var store = await StoreFor(pipelineId, cancellationToken);
        return await ParquetVersions.ListEntitiesAsync(store, pipelineId, cancellationToken);
    }

    public async Task<int> DeleteVersionsAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        var store = await StoreFor(pipelineId, cancellationToken);
        return await ParquetVersions.DeleteAllAsync(store, pipelineId, cancellationToken);
    }

    public async Task<ParquetVersion> RestoreVersionAsync(
        string pipelineId, string entity, string versionId, ParquetVersion archiveCurrentAs, CancellationToken cancellationToken = default)
    {
        EnsureEntityName(entity);
        var store = await StoreFor(pipelineId, cancellationToken);
        var version = (await ParquetVersions.ListAsync(store, pipelineId, entity, cancellationToken))
            .FirstOrDefault(v => v.Id == versionId)
            ?? throw new InvalidOperationException($"La versión '{versionId}' de {entity} no existe.");

        if (await store.ParquetExistsAsync(IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity), cancellationToken))
        {
            await ParquetVersions.ArchiveCurrentAsync(store, pipelineId, entity, archiveCurrentAs, DateTimeOffset.UtcNow, cancellationToken);
        }

        await ParquetVersions.TakeAsync(store, pipelineId, entity, versionId, cancellationToken);
        if (version.SchemaJson is not null)
        {
            await store.UploadTextAsync(IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity), version.SchemaJson, cancellationToken);
        }

        return version;
    }

    private static void EnsureEntityName(string entity)
    {
        if (string.IsNullOrWhiteSpace(entity) || entity.Contains('/') || entity.Contains('\\') || entity.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Nombre de tabla no válido: '{entity}'.");
        }
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
