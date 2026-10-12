using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

public sealed class SnapshotCatalogServiceTests
{
    [Fact]
    public void IsChangelogStalled_ReturnsTrue_WhenOlderThanThreshold()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        var lastModified = now.AddHours(-5);

        Assert.True(service.IsChangelogStalled(lastModified, now));
    }

    [Fact]
    public void IsChangelogStalled_ReturnsFalse_WhenRecent()
    {
        var service = CreateService();
        var now = new DateTimeOffset(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        var lastModified = now.AddHours(-1);

        Assert.False(service.IsChangelogStalled(lastModified, now));
    }

    [Fact]
    public void IsChangelogStalled_ReturnsFalse_WhenMissing()
    {
        var service = CreateService();
        Assert.False(service.IsChangelogStalled(null, DateTimeOffset.UtcNow));
    }

    private static SnapshotCatalogService CreateService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "snap-catalog-" + Guid.NewGuid().ToString("N"));
        DataDirectoryInitializer.EnsureLayout(dir);
        var opts = Options.Create(new AppOptions { DataDir = dir, ChangelogStallHours = 4 });
        return new(opts, new StubPipelineService(), new StubAdlsClientFactory(), new SettingsStore(opts));
    }

    private sealed class StubPipelineService : IPipelineService
    {
        public Pipeline? Get(string id) => new Pipeline
        {
            Id = id,
            Name = "Stub",
            SourceId = "src",
            DestinationId = "dst",
            Enabled = true,
        };

        public IReadOnlyList<Pipeline> List() => [Get("pipe")!];
        public Pipeline Create(Pipeline pipeline) => pipeline;
        public Pipeline? Update(string id, Pipeline pipeline) => pipeline;
        public bool Delete(string id, bool wipeState, out string? error) { error = null; return true; }
        public PipelineState GetState(string pipelineId) => new() { PipelineId = pipelineId };
        public IReadOnlyList<object> GetTables(string pipelineId) => [];
    }

    private sealed class StubAdlsClientFactory : IAdlsClientFactory
    {
        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAdlsClient>(new StubAdlsClient());

        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Snapshot catalog tests no usan el parquet store.");

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "ok"));
    }

    private sealed class StubAdlsClient : IAdlsClient
    {
        public Task TestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetSettledSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("2026-06-08T10.00.00Z");

        public Task<DateTimeOffset?> GetChangelogLastModifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);

        public Task<IReadOnlyList<string>> ListSnapshotFoldersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "2026-06-08T09.00.00Z", "2026-06-08T10.00.00Z" });

        public Task<SnapshotIntegrity> ValidateSnapshotAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnapshotIntegrity(true, null, null));

        public Task<IReadOnlyList<string>> ListEntitiesAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<CdmEntitySchema> GetEntitySchemaAsync(string entity, string snapshot, string? settledSnapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CdmEntitySchema { EntityName = entity, Attributes = [] });

        public async IAsyncEnumerable<string> StreamEntityCsvLinesAsync(string snapshot, string entity, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<string?> ReadGlobalOptionsetMetadataCsvAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task DeleteSnapshotFolderAsync(string snapshotFolder, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}