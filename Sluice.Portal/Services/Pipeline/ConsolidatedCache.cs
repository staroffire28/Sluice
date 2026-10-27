using System.Text.Json;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

// Copia local del consolidado de cada tabla en DATA_DIR/cache/consolidated/{pipeline}/{entidad}.parquet,
// con el ETag y el tamaño del remoto en un .json al lado. Si el portal corre fuera de la región de
// F&O, cada run que tocaba la tabla bajaba el consolidado entero (la salida de datos se paga). Ahora
// solo se baja si el remoto cambió: un HEAD (ETag y tamaño) decide si la copia sirve. Tras publicar,
// el consolidado nuevo pasa a ser la copia, así que el run siguiente no baja nada.
public sealed class ConsolidatedCache(string dataDir)
{
    public string GetPath(string pipelineId, string entity) =>
        Path.Combine(dataDir, "cache", "consolidated", pipelineId, $"{entity}.parquet");

    // Ruta de una copia local válida del consolidado remoto con estas propiedades, bajándolo si la
    // copia no está, es de otra versión (ETag) o no tiene el tamaño esperado (p. ej. cortada a medias).
    public async Task<string> GetOrDownloadAsync(
        IParquetStore store, string pipelineId, string entity, string blobPath, ParquetFileProperties remote,
        CancellationToken cancellationToken)
    {
        var path = GetPath(pipelineId, entity);
        if (Matches(path, remote))
        {
            return path;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        try
        {
            await using (var source = await store.OpenParquetReadAsync(blobPath, cancellationToken))
            await using (var file = File.Create(temp))
            {
                await source.CopyToAsync(file, cancellationToken);
            }

            Commit(temp, path, remote);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }

        return path;
    }

    // El consolidado recién publicado (un fichero local de staging) pasa a ser la copia, con el ETag
    // que tiene ya en el remoto. Se mueve: staging se borra después igualmente.
    public void Store(string pipelineId, string entity, string localFile, ParquetFileProperties remote)
    {
        var path = GetPath(pipelineId, entity);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.Move(localFile, temp, overwrite: true);
        Commit(temp, path, remote);
    }

    // Una copia que deja de servir: la tabla se restaura a otro punto o se archiva. Una copia vieja nunca
    // se usaría (otro ETag), pero ocuparía disco.
    public void Remove(string pipelineId, string entity)
    {
        var path = GetPath(pipelineId, entity);
        // Sin caché (p. ej. el parquet en el disco del portal) no hay carpeta, y File.Delete lanza.
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            return;
        }

        File.Delete(MetadataPath(path));
        File.Delete(path);
    }

    // Todas las copias del pipeline: al resetearlo o borrarlo.
    public void RemovePipeline(string pipelineId)
    {
        var directory = Path.GetDirectoryName(GetPath(pipelineId, "_"))!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Primero se quita el .json viejo y al final se escribe el nuevo: si el proceso cae entre medias,
    // queda una copia sin metadatos, que no se usa. Si falla antes, el .json viejo no casa con el ETag
    // nuevo del remoto. En los dos casos el run siguiente lo baja.
    private static void Commit(string temp, string path, ParquetFileProperties remote)
    {
        File.Delete(MetadataPath(path));
        File.Move(temp, path, overwrite: true);
        File.WriteAllText(MetadataPath(path), JsonSerializer.Serialize(remote));
    }

    private static bool Matches(string path, ParquetFileProperties remote)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<ParquetFileProperties>(File.ReadAllText(MetadataPath(path)));
            return stored == remote && new FileInfo(path).Length == remote.Length;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Sin copia o ilegible: se baja de nuevo.
            return false;
        }
    }

    private static string MetadataPath(string path) => path + ".json";
}
