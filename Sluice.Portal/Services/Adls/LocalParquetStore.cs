using System.Text;

namespace Sluice.Portal.Services.Adls;

// Parquet en una carpeta local en vez de en ADLS: el disco del portal (DATA_DIR/parquet, para un
// origen con parquet_storage = portal) o la carpeta del origen local de la demo y las pruebas.
// Mismas rutas relativas que en ADLS ({pipeline}/{entity}.parquet, ...) y ninguna sale de la raíz.
public sealed class LocalParquetStore : IParquetStore
{
    private readonly string _root;

    public LocalParquetStore(string root) => _root = Path.GetFullPath(root);

    private string PathOf(string relative) => LocalFolderClient.Confine(_root, relative);

    public async Task UploadParquetAsync(string blobPath, Stream content, CancellationToken cancellationToken = default)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var path = PathOf(blobPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream> OpenParquetReadAsync(string blobPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new FileStream(PathOf(blobPath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public Task<bool> ParquetExistsAsync(string blobPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(PathOf(blobPath)));

    public Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        File.Delete(PathOf(blobPath));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var dir = PathOf(prefix);
        if (!Directory.Exists(dir))
        {
            return Task.FromResult<IReadOnlyList<ParquetArtifact>>([]);
        }

        IReadOnlyList<ParquetArtifact> artifacts = Directory.EnumerateFiles(dir, "*.parquet", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => new ParquetArtifact(Path.GetRelativePath(_root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc))
            .OrderBy(a => a.Path, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(artifacts);
    }

    public async Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default)
    {
        var path = PathOf(blobPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken);
    }

    public async Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        var path = PathOf(blobPath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken) : null;
    }
}
