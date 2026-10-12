using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

public class IngestManifestStorePathTests
{
    [Fact]
    public void GetParquetBlobPath_uses_forward_slash_adls_layout()
    {
        Assert.Equal(
            "pipe-1/job-9/account.parquet",
            IngestManifestStore.GetParquetBlobPath("pipe-1", "job-9", "account"));
    }

    [Fact]
    public void GetSchemaBlobPath_sits_next_to_parquet_in_adls()
    {
        // El schema debe ser durable junto al parquet en ADLS (no solo en disco local),
        // para que el stage SQL pueda leerlo sin depender del volumen compartido.
        Assert.Equal(
            "pipe-1/job-9/account.schema.json",
            IngestManifestStore.GetSchemaBlobPath("pipe-1", "job-9", "account"));
    }
}
