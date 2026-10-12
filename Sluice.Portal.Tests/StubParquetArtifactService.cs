using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

internal sealed class StubParquetArtifactService : IParquetArtifactService
{
    public Task<IReadOnlyList<ParquetArtifact>> ListAsync(string pipelineId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ParquetArtifact>>(
        [
            new ParquetArtifact($"{pipelineId}/account.parquet", 1024, DateTimeOffset.UtcNow),
        ]);

    public Task<Stream?> OpenAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(null);

    public Task<bool> DeleteAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}