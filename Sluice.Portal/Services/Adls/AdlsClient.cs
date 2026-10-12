using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Services.Adls;

public sealed class AdlsClient : IAdlsClient, IParquetStore
{
    private const string ChangelogPath = "Changelog/changelog.info";
    private const string CdmFolder = "Microsoft.Athena.TrickleFeedService";

    private readonly DataLakeServiceClient _serviceClient;
    private readonly DataLakeFileSystemClient _fileSystem;
    private readonly string? _parquetContainer;

    public AdlsClient(AdlsConnectionSettings settings)
    {
        _serviceClient = new DataLakeServiceClient(
            new Uri($"https://{settings.Account}.dfs.core.windows.net"),
            AzureCredentialFactory.CreateClientSecret(settings.TenantId, settings.ClientId, settings.ClientSecret));

        _fileSystem = _serviceClient.GetFileSystemClient(settings.Container);
        _parquetContainer = settings.ParquetContainer;
    }

    // --- IParquetStore: container de destino del parquet (durable, separado del origen) ---

    private DataLakeFileSystemClient ParquetFileSystem() =>
        string.IsNullOrWhiteSpace(_parquetContainer)
            ? throw new InvalidOperationException(
                "ADLS parquet destination container is not configured. Set 'parquet_container' on the ADLS connection or AZURE_PARQUET_CONTAINER.")
            : _serviceClient.GetFileSystemClient(_parquetContainer);

    public async Task UploadParquetAsync(string blobPath, Stream content, CancellationToken cancellationToken = default)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var file = ParquetFileSystem().GetFileClient(blobPath);
        await file.UploadAsync(content, overwrite: true, cancellationToken);
    }

    public async Task<Stream> OpenParquetReadAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var file = ParquetFileSystem().GetFileClient(blobPath);
        var response = await file.ReadAsync(cancellationToken: cancellationToken);

        // El stream de red de ADLS se dispone tras copiar (no se deja al GC).
        await using var content = response.Value.Content;
        return await CopyToTempFileAsync(content, cancellationToken);
    }

    // Parquet.Net necesita un stream con seek para leer el footer. A un fichero temporal y no a
    // memoria: un MemoryStream retenía el parquet entero (consolidados de GB en el worker).
    // DeleteOnClose lo borra al disponer el stream (también si el proceso muere, en Windows).
    internal static async Task<Stream> CopyToTempFileAsync(Stream source, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sluice-{Guid.NewGuid():N}.parquet");
        var buffer = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await source.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            await buffer.DisposeAsync();
            throw;
        }
    }

    // Contar filas solo necesita el footer: lectura perezosa por rangos (2-3 peticiones pequeñas)
    // en vez de descargar el fichero. Las lecturas de filas siguen con OpenParquetReadAsync:
    // por rangos harían una petición por columna y row group (miles de transacciones de ADLS).
    public async Task<long> CountParquetRowsAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var file = ParquetFileSystem().GetFileClient(blobPath);
        await using var stream = await file.OpenReadAsync(
            new DataLakeOpenReadOptions(allowModifications: false) { BufferSize = 256 * 1024 },
            cancellationToken);
        return await ParquetMaterializer.CountRowsAsync(stream, cancellationToken);
    }

    public async Task<bool> ParquetExistsAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var file = ParquetFileSystem().GetFileClient(blobPath);
        return (await file.ExistsAsync(cancellationToken)).Value;
    }

    public async Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var file = ParquetFileSystem().GetFileClient(blobPath);
        await file.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes);
        var file = ParquetFileSystem().GetFileClient(blobPath);
        await file.UploadAsync(stream, overwrite: true, cancellationToken);
    }

    public async Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var file = ParquetFileSystem().GetFileClient(blobPath);
        if (!(await file.ExistsAsync(cancellationToken)).Value)
        {
            return null;
        }

        var response = await file.ReadAsync(cancellationToken: cancellationToken);
        using var reader = new StreamReader(response.Value.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var fileSystem = ParquetFileSystem();
        var artifacts = new List<ParquetArtifact>();

        try
        {
            await foreach (var path in fileSystem.GetPathsAsync(prefix, recursive: true, userPrincipalName: false, cancellationToken))
            {
                if (path.IsDirectory == true || !path.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                artifacts.Add(new ParquetArtifact(path.Name, path.ContentLength ?? 0, path.LastModified));
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // El prefijo aún no existe (nada materializado todavía para este pipeline).
            // Devolver vacío en vez de propagar el 404 PathNotFound a la UI.
            return [];
        }

        return artifacts.OrderBy(a => a.Path, StringComparer.Ordinal).ToList();
    }

    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _fileSystem.GetPathsAsync(null, false, false, cancellationToken).AsPages(pageSizeHint: 1))
        {
            break;
        }
    }

    public async Task<string?> GetSettledSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var file = _fileSystem.GetFileClient(ChangelogPath);
        if (!await file.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var response = await file.ReadAsync(cancellationToken: cancellationToken);
        using var reader = new StreamReader(response.Value.Content, Encoding.UTF8);
        var line = (await reader.ReadLineAsync(cancellationToken))?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    public async Task<DateTimeOffset?> GetChangelogLastModifiedAsync(CancellationToken cancellationToken = default)
    {
        var file = _fileSystem.GetFileClient(ChangelogPath);
        if (!await file.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var props = await file.GetPropertiesAsync(cancellationToken: cancellationToken);
        return props.Value.LastModified;
    }

    public async Task<IReadOnlyList<string>> ListSnapshotFoldersAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<string>();
        await foreach (var path in _fileSystem.GetPathsAsync(null, false, false, cancellationToken))
        {
            if (path.IsDirectory != true)
            {
                continue;
            }

            var name = path.Name.TrimEnd('/');
            if (ExcludedEntities.IsSnapshotFolder(name))
            {
                snapshots.Add(name);
            }
        }

        return snapshots.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public async Task<SnapshotIntegrity> ValidateSnapshotAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        // Validación de robustez: la carpeta ya está sellada por el changelog, pero esto
        // defiende ante un fallo de Synapse que dejara una carpeta cerrada pero vacía/a medias.
        // Solo inspecciona metadatos (longitudes), NO descarga datos.
        //
        // Criterio a NIVEL DE SNAPSHOT: basta con que UNA entidad tenga datos (>0 bytes) para
        // considerar el volcado presente y procesable. Una entidad con solo CSV vacíos es normal
        // en Dynamics F&O/Dataverse (tabla sin movimientos en la ventana) y NO debe bloquear el
        // ingest — el resto del flujo ya omite entidades vacías. Solo se marca incompleto si
        // NINGUNA entidad del snapshot tiene datos (volcado totalmente vacío o en progreso).
        var entities = await ListEntitiesAsync(snapshot, cancellationToken);

        // Snapshot sin entidades: nada que ingestar, no es un volcado roto.
        if (entities.Count == 0)
        {
            return new SnapshotIntegrity(true, null, null);
        }

        foreach (var entity in entities)
        {
            var prefix = $"{snapshot.TrimEnd('/')}/{entity}/";

            await foreach (var path in _fileSystem.GetPathsAsync(prefix, false, false, cancellationToken))
            {
                if (path.IsDirectory == true || !path.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((path.ContentLength ?? 0) > 0)
                {
                    return new SnapshotIntegrity(true, null, null);
                }
            }
        }

        return new SnapshotIntegrity(
            false,
            null,
            $"Snapshot '{snapshot}' has no non-empty CSV in any entity (empty or in-progress export).");
    }

    public async Task<IReadOnlyList<string>> ListEntitiesAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        var entities = new List<string>();
        var prefix = $"{snapshot.TrimEnd('/')}/";

        await foreach (var path in _fileSystem.GetPathsAsync(prefix, false, false, cancellationToken))
        {
            if (path.IsDirectory != true)
            {
                continue;
            }

            var name = path.Name.TrimEnd('/').Split('/').Last();
            if (!ExcludedEntities.IsExcluded(name))
            {
                entities.Add(name);
            }
        }

        return entities.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<CdmEntitySchema> GetEntitySchemaAsync(
        string entity,
        string snapshot,
        string? settledSnapshot,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<string>
        {
            $"{snapshot}/{CdmFolder}/{entity}-model.json",
        };

        if (!string.IsNullOrWhiteSpace(settledSnapshot))
        {
            candidates.Add($"{settledSnapshot}/{CdmFolder}/{entity}-model.json");
        }

        var snapshots = await ListSnapshotFoldersAsync(cancellationToken);
        candidates.AddRange(snapshots.Select(s => $"{s}/{CdmFolder}/{entity}-model.json"));
        candidates.Add($"{CdmFolder}/{entity}-model.json");

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = _fileSystem.GetFileClient(candidate);
            if (!await file.ExistsAsync(cancellationToken))
            {
                continue;
            }

            var response = await file.ReadAsync(cancellationToken: cancellationToken);
            using var reader = new StreamReader(response.Value.Content, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken);
            return CdmSchemaParser.Parse(entity, json);
        }

        throw new InvalidOperationException($"CDM schema not found for entity '{entity}'.");
    }

    // CSVs de la entidad en orden de nombre, sin materializar:
    // un Stream/StreamReader abierto a la vez, línea por línea. El pico de memoria no depende del
    // tamaño del CSV fuente (crítico para tablas con un solo archivo de cientos de MB/millones de
    // filas, ej. el volcado histórico inicial de una tabla grande).
    public async IAsyncEnumerable<string> StreamEntityCsvLinesAsync(
        string snapshot, string entity, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prefix = $"{snapshot.TrimEnd('/')}/{entity}/";
        var files = new List<string>();

        await foreach (var path in _fileSystem.GetPathsAsync(prefix, false, false, cancellationToken))
        {
            if (path.IsDirectory == true || !path.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(path.Name);
        }

        foreach (var fileName in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var file = _fileSystem.GetFileClient(fileName);
            var response = await file.ReadAsync(cancellationToken: cancellationToken);
            using var reader = new StreamReader(response.Value.Content, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                yield return line;
            }
        }
    }

    public async Task<string?> ReadGlobalOptionsetMetadataCsvAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        var path = $"{snapshot.TrimEnd('/')}/OptionsetMetadata/GlobalOptionsetMetadata.csv";
        var file = _fileSystem.GetFileClient(path);
        if (!(await file.ExistsAsync(cancellationToken)).Value)
        {
            return null;
        }

        var response = await file.ReadAsync(cancellationToken: cancellationToken);
        using var reader = new StreamReader(response.Value.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }


    public async Task DeleteSnapshotFolderAsync(string snapshotFolder, CancellationToken cancellationToken = default)
    {
        var name = snapshotFolder.Trim().TrimEnd('/');
        if (!ExcludedEntities.IsSnapshotFolder(name))
        {
            throw new ArgumentException($"Not a snapshot folder: '{snapshotFolder}'.", nameof(snapshotFolder));
        }

        var directory = _fileSystem.GetDirectoryClient(name);
        await directory.DeleteIfExistsAsync(recursive: true, cancellationToken: cancellationToken);
    }
}