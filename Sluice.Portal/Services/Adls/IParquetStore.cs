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

    // Filas del parquet (metadatos del footer; con excludeDeleteRows, el recuento sin borrados del
    // footer o, si falta, la columna IsDelete: ParquetMaterializer.CountRowsAsync).
    // Por defecto lo lee entero; AdlsClient lo sobrescribe con lectura por rangos para no
    // descargarlo solo para contar.
    async Task<long> CountParquetRowsAsync(string blobPath, bool excludeDeleteRows = false, CancellationToken cancellationToken = default)
    {
        await using var stream = await OpenParquetReadAsync(blobPath, cancellationToken);
        return await Pipeline.ParquetMaterializer.CountRowsAsync(stream, excludeDeleteRows, cancellationToken);
    }

    // Artefactos de texto que acompañan al parquet en el mismo container durable
    // (p.ej. el schema CDM en {pipeline}/{job}/{entity}.schema.json).
    Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default);
    Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default);

    // Renombra un fichero (parquet o texto). El destino no debe existir: quien llama lo garantiza,
    // porque si ADLS sobrescribe o falla en ese caso no forma parte del contrato. AdlsClient y
    // LocalParquetStore lo hacen atómico; por defecto copia y borra, para los dobles de test.
    async Task MoveAsync(string fromPath, string toPath, CancellationToken cancellationToken = default)
    {
        await using (var content = await OpenParquetReadAsync(fromPath, cancellationToken))
        {
            await UploadParquetAsync(toPath, content, cancellationToken);
        }

        await DeleteParquetAsync(fromPath, cancellationToken);
    }
}

public sealed record ParquetArtifact(string Path, long SizeBytes, DateTimeOffset? LastModified);
