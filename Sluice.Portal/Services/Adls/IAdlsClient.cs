using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Adls;

// Resultado de la validación de robustez de un snapshot ya sellado por el changelog.
public sealed record SnapshotIntegrity(bool IsComplete, string? IncompleteEntity, string? Reason);

public interface IAdlsClient
{
    Task TestAsync(CancellationToken cancellationToken = default);
    Task<string?> GetSettledSnapshotAsync(CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> GetChangelogLastModifiedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListSnapshotFoldersAsync(CancellationToken cancellationToken = default);
    // Confirma que una carpeta sellada se volcó completa (cada partition declarada en el model.json
    // de su raíz existe y no está vacía; ver SnapshotManifest). Defiende ante un fallo de volcado de Synapse.
    Task<SnapshotIntegrity> ValidateSnapshotAsync(string snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListEntitiesAsync(string snapshot, CancellationToken cancellationToken = default);
    Task<CdmEntitySchema> GetEntitySchemaAsync(string entity, string snapshot, string? settledSnapshot, CancellationToken cancellationToken = default);

    // Tablas con esquema ({entity}-model.json) en la carpeta CDM de un snapshot, o de la raíz del
    // container con snapshot null. Base del catálogo de tablas sin lanzar un job (TableCatalogService
    // lo une a las carpetas del snapshot y a las tablas ya cargadas). Por defecto vacía, para los
    // dobles de test.
    Task<IReadOnlyList<string>> ListSchemaEntitiesAsync(string? snapshot, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    // CSVs de la entidad en orden de nombre, en streaming
    // (una línea a la vez, un archivo abierto a la vez) para no materializar CSVs grandes
    // completos en memoria — ver IngestExecutor, que la usa para escribir el parquet transitorio.
    IAsyncEnumerable<string> StreamEntityCsvLinesAsync(string snapshot, string entity, CancellationToken cancellationToken = default);

    // Lee {snapshot}/OptionsetMetadata/GlobalOptionsetMetadata.csv del container origen (traducción
    // de enums). Devuelve null si el snapshot no exporta ese archivo. Lectura específica del Global,
    // no de toda la carpeta OptionsetMetadata (que también contiene State/Status/TargetMetadata).
    Task<string?> ReadGlobalOptionsetMetadataCsvAsync(string snapshot, CancellationToken cancellationToken = default);

    // Borra recursivamente una carpeta de snapshot en el container origen (Synapse Link).
    Task DeleteSnapshotFolderAsync(string snapshotFolder, CancellationToken cancellationToken = default);
}