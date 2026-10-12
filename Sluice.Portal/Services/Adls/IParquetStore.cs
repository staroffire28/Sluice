namespace Sluice.Portal.Services.Adls;

// Operaciones sobre el container ADLS de destino del parquet: store durable y
// separado del origen Synapse Link. Interfaz aparte de IAdlsClient (ISP): leer el
// origen y gestionar el parquet de salida son responsabilidades distintas.
public interface IParquetStore
{
    Task UploadParquetAsync(string blobPath, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenParquetReadAsync(string blobPath, CancellationToken cancellationToken = default);
    Task<bool> ParquetExistsAsync(string blobPath, CancellationToken cancellationToken = default);
    Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default);

    // Filas del parquet (metadatos del footer). Por defecto lo lee entero; AdlsClient lo
    // sobrescribe con lectura por rangos para no descargarlo solo para contar.
    async Task<long> CountParquetRowsAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenParquetReadAsync(blobPath, cancellationToken);
        return await Pipeline.ParquetMaterializer.CountRowsAsync(stream, cancellationToken);
    }

    // Artefactos de texto que acompañan al parquet en el mismo container durable
    // (p.ej. el schema CDM en {pipeline}/{job}/{entity}.schema.json).
    Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default);
    Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default);
}

public sealed record ParquetArtifact(string Path, long SizeBytes, DateTimeOffset? LastModified);
