using System.Runtime.CompilerServices;
using System.Text;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Adls;

// Origen Synapse Link simulado sobre carpetas locales, para las pruebas UAT: misma semántica que
// AdlsClient (changelog, carpetas de snapshot, CSV sin cabecera, model.json) leyendo de
// {raíz}/{container}, y el parquet en {raíz}/{parquet_container}. Solo existe si el portal arranca
// con LOCAL_SOURCE_ROOT (ver AdlsClientFactory), y ninguna ruta puede salir de esa raíz.
public sealed class LocalFolderClient : IAdlsClient, IParquetStore
{
    private const string ChangelogPath = "Changelog/changelog.info";
    private const string CdmFolder = "Microsoft.Athena.TrickleFeedService";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _source;
    private readonly LocalParquetStore? _parquetStore;

    public LocalFolderClient(string root, string container, string? parquetContainer)
    {
        if (string.IsNullOrWhiteSpace(container))
        {
            throw new InvalidOperationException("Local source folder (container) is not configured.");
        }

        var fullRoot = Path.GetFullPath(root);
        _source = Confine(fullRoot, container, allowBase: false);
        _parquetStore = string.IsNullOrWhiteSpace(parquetContainer)
            ? null
            : new LocalParquetStore(Confine(fullRoot, parquetContainer, allowBase: false));
    }

    // Ruta absoluta de 'relative' dentro de 'baseDir'; falla si '..' o una ruta absoluta la sacan fuera.
    internal static string Confine(string baseDir, string relative, bool allowBase = true)
    {
        var full = Path.GetFullPath(Path.Combine(baseDir, relative.TrimStart('/', '\\')));
        var trimmedBase = baseDir.TrimEnd(Path.DirectorySeparatorChar);
        var inside = full.StartsWith(trimmedBase + Path.DirectorySeparatorChar, PathComparison)
            || (allowBase && string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), trimmedBase, PathComparison));
        return inside ? full : throw new InvalidOperationException($"Path '{relative}' is outside the local source root.");
    }

    private string SourcePath(string relative) => Confine(_source, relative);

    private LocalParquetStore Parquet => _parquetStore ?? throw new InvalidOperationException(
        "Local parquet folder is not configured. Set 'parquet_container' on the local connection.");

    // --- IParquetStore ---

    public Task UploadParquetAsync(string blobPath, Stream content, CancellationToken cancellationToken = default) =>
        Parquet.UploadParquetAsync(blobPath, content, cancellationToken);

    public Task<Stream> OpenParquetReadAsync(string blobPath, CancellationToken cancellationToken = default) =>
        Parquet.OpenParquetReadAsync(blobPath, cancellationToken);

    public Task<bool> ParquetExistsAsync(string blobPath, CancellationToken cancellationToken = default) =>
        Parquet.ParquetExistsAsync(blobPath, cancellationToken);

    public Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default) =>
        Parquet.DeleteParquetAsync(blobPath, cancellationToken);

    public Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default) =>
        Parquet.ListParquetAsync(prefix, cancellationToken);

    public Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default) =>
        Parquet.UploadTextAsync(blobPath, content, cancellationToken);

    public Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default) =>
        Parquet.ReadTextAsync(blobPath, cancellationToken);

    public Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default) =>
        Parquet.MoveAsync(fromPath, toPath, cancellationToken);

    // --- IAdlsClient ---

    public Task TestAsync(CancellationToken cancellationToken = default) =>
        Directory.Exists(_source)
            ? Task.CompletedTask
            : throw new DirectoryNotFoundException($"Local source folder '{Path.GetFileName(_source)}' does not exist.");

    public async Task<string?> GetSettledSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var path = SourcePath(ChangelogPath);
        if (!File.Exists(path))
        {
            return null;
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        var line = (await reader.ReadLineAsync(cancellationToken))?.Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    public Task<DateTimeOffset?> GetChangelogLastModifiedAsync(CancellationToken cancellationToken = default)
    {
        var path = SourcePath(ChangelogPath);
        return Task.FromResult<DateTimeOffset?>(File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null);
    }

    public Task<IReadOnlyList<string>> ListSnapshotFoldersAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> snapshots = Subfolders(_source)
            .Where(ExcludedEntities.IsSnapshotFolder)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(snapshots);
    }

    public async Task<SnapshotIntegrity> ValidateSnapshotAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        // Mismo criterio que AdlsClient: cada partition del model.json de la raíz existe y no está vacía.
        var entities = await ListEntitiesAsync(snapshot, cancellationToken);
        var folder = SourcePath(snapshot);
        var model = Path.Combine(folder, SnapshotManifest.ModelFile);
        var modelJson = File.Exists(model) ? await File.ReadAllTextAsync(model, cancellationToken) : null;
        return SnapshotManifest.Evaluate(snapshot, modelJson, entities.Count > 0, relative =>
        {
            var file = new FileInfo(Confine(folder, relative));
            return file.Exists ? file.Length : null;
        });
    }

    public Task<IReadOnlyList<string>> ListEntitiesAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> entities = Subfolders(SourcePath(snapshot))
            .Where(name => !ExcludedEntities.IsExcluded(name))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(entities);
    }

    public Task<IReadOnlyList<string>> ListSchemaEntitiesAsync(string? snapshot, CancellationToken cancellationToken = default)
    {
        var folder = SourcePath(string.IsNullOrWhiteSpace(snapshot) ? CdmFolder : $"{snapshot}/{CdmFolder}");
        IReadOnlyList<string> entities = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*-model.json")
                .Select(f => Path.GetFileName(f)[..^"-model.json".Length])
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        return Task.FromResult(entities);
    }

    public async Task<CdmEntitySchema> GetEntitySchemaAsync(
        string entity,
        string snapshot,
        string? settledSnapshot,
        CancellationToken cancellationToken = default)
    {
        return await CdmSchemaLocator.FindAsync(entity, snapshot, settledSnapshot, async candidate =>
        {
            var path = SourcePath(candidate);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken) : null;
        }, () => ListSnapshotFoldersAsync(cancellationToken));
    }

    public async IAsyncEnumerable<string> StreamEntityCsvLinesAsync(
        string snapshot, string entity, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in CsvFiles(snapshot, entity))
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                yield return line;
            }
        }
    }

    public async Task<string?> ReadGlobalOptionsetMetadataCsvAsync(string snapshot, CancellationToken cancellationToken = default)
    {
        var path = SourcePath($"{snapshot.TrimEnd('/')}/OptionsetMetadata/GlobalOptionsetMetadata.csv");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken) : null;
    }

    public Task DeleteSnapshotFolderAsync(string snapshotFolder, CancellationToken cancellationToken = default)
    {
        var name = snapshotFolder.Trim().TrimEnd('/');
        if (!ExcludedEntities.IsSnapshotFolder(name))
        {
            throw new ArgumentException($"Not a snapshot folder: '{snapshotFolder}'.", nameof(snapshotFolder));
        }

        var dir = SourcePath(name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return Task.CompletedTask;
    }

    // CSV de la entidad en orden ordinal de nombre, como AdlsClient.
    private IEnumerable<string> CsvFiles(string snapshot, string entity)
    {
        var dir = SourcePath($"{snapshot.TrimEnd('/')}/{entity}");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.csv").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            : [];
    }

    private static IEnumerable<string> Subfolders(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Select(d => Path.GetFileName(d)) : [];

    private static string Relative(string baseDir, string fullPath) =>
        Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
}
