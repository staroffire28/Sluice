using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Logging;

namespace Sluice.Portal.Services.Pipeline;

// Fachada de gestión del parquet en ADLS para UI y API: resuelve el pipeline a su
// conexión ADLS y acota cada operación al prefijo {pipelineId}/ (evita travesía y
// acceso cruzado entre pipelines).
public interface IParquetArtifactService
{
    Task<IReadOnlyList<ParquetArtifact>> ListAsync(string pipelineId, CancellationToken cancellationToken = default);
    Task<Stream?> OpenAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default);
    // Borrado pedido por alguien (UI o API): queda en el registro de actividad.
    Task<bool> DeleteAsync(AuditActor actor, string pipelineId, string blobPath, CancellationToken cancellationToken = default);

    // Sin entrada propia: solo para operaciones que se auditan enteras (el reset borra decenas de
    // ficheros y deja una sola entrada con el recuento).
    Task<bool> DeleteUnauditedAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default);

    // Versiones del consolidado (ParquetVersions), por tabla. Por defecto sin versiones, para los
    // dobles de test.
    Task<IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>>> ListAllVersionsAsync(string pipelineId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>>>(new Dictionary<string, IReadOnlyList<ParquetVersion>>());

    Task<IReadOnlyList<string>> ListVersionedEntitiesAsync(string pipelineId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    Task<int> DeleteVersionsAsync(string pipelineId, CancellationToken cancellationToken = default) => Task.FromResult(0);

    // Vuelve a poner la versión como consolidado vigente; el vigente pasa a ser una versión más
    // (archiveCurrentAs), así que restaurar también se puede deshacer. Devuelve la restaurada.
    Task<ParquetVersion> RestoreVersionAsync(
        string pipelineId, string entity, string versionId, ParquetVersion archiveCurrentAs, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // Pasa el consolidado vigente a versión sin poner otro en su sitio (una tabla que aún no existía
    // en el punto al que se vuelve). Sin consolidado no hace nada.
    Task ArchiveCurrentAsync(string pipelineId, string entity, ParquetVersion info, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class ParquetArtifactService : IParquetArtifactService
{
    private readonly IPipelineService _pipelines;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly IAuditLog _audit;

    public ParquetArtifactService(IPipelineService pipelines, IAdlsClientFactory adlsFactory, IAuditLog audit)
    {
        _audit = audit;
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

    public Task<bool> DeleteAsync(AuditActor actor, string pipelineId, string blobPath, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "parquet.delete",
            pipelineId,
            () => DeleteUnauditedAsync(pipelineId, blobPath, cancellationToken),
            deleted => deleted
                ? new AuditResult($"Parquet {blobPath} borrado del pipeline {pipelineId}.")
                : new AuditResult($"La ruta {blobPath} no pertenece al pipeline {pipelineId}: no se borró.", Outcome: AuditOutcome.Rejected),
            $"No se pudo borrar el parquet {blobPath}",
            target: blobPath);

    public async Task<bool> DeleteUnauditedAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default)
    {
        if (!IsWithinPipeline(pipelineId, blobPath))
        {
            return false;
        }

        var store = await StoreFor(pipelineId, cancellationToken);
        await store.DeleteParquetAsync(blobPath, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ParquetVersion>>> ListAllVersionsAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        var store = await StoreFor(pipelineId, cancellationToken);
        return await ParquetVersions.ListAllAsync(store, pipelineId, cancellationToken);
    }

    public async Task ArchiveCurrentAsync(string pipelineId, string entity, ParquetVersion info, CancellationToken cancellationToken = default)
    {
        EnsureEntityName(entity);
        var store = await StoreFor(pipelineId, cancellationToken);
        if (await store.ParquetExistsAsync(IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity), cancellationToken))
        {
            await ParquetVersions.ArchiveCurrentAsync(store, pipelineId, entity, info, DateTimeOffset.UtcNow, cancellationToken);
        }
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
        var version = await ParquetVersions.GetAsync(store, pipelineId, entity, versionId, cancellationToken)
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
