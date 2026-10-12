using System.Collections.Concurrent;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;
using PipelineModel = Sluice.Portal.Models.Pipeline;

namespace Sluice.Portal.Tests;

public class ParquetArtifactServiceTests
{
    [Fact]
    public async Task ListAsync_returns_only_artifacts_under_pipeline_prefix()
    {
        var store = new FakeParquetStore();
        store.Seed("pipe-1/job-1/account.parquet", "a");
        store.Seed("pipe-1/job-1/contact.parquet", "b");
        store.Seed("pipe-2/job-9/account.parquet", "c");
        var service = CreateService(store);

        var result = await service.ListAsync("pipe-1");

        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.StartsWith("pipe-1/", a.Path));
    }

    [Fact]
    public async Task DeleteAsync_removes_valid_artifact()
    {
        var store = new FakeParquetStore();
        store.Seed("pipe-1/job-1/account.parquet", "a");
        var service = CreateService(store);

        var ok = await service.DeleteAsync("pipe-1", "pipe-1/job-1/account.parquet");

        Assert.True(ok);
        Assert.False(await store.ParquetExistsAsync("pipe-1/job-1/account.parquet"));
    }

    [Theory]
    [InlineData("pipe-2/job-1/account.parquet")] // otro pipeline
    [InlineData("pipe-1/../pipe-2/x.parquet")]    // travesía
    [InlineData("")]                               // vacío
    public async Task DeleteAsync_rejects_path_outside_pipeline(string badPath)
    {
        var store = new FakeParquetStore();
        store.Seed("pipe-2/job-1/account.parquet", "a");
        var service = CreateService(store);

        var ok = await service.DeleteAsync("pipe-1", badPath);

        Assert.False(ok);
        // El artefacto de otro pipeline sigue intacto: la validación corta antes de tocar el store.
        Assert.True(await store.ParquetExistsAsync("pipe-2/job-1/account.parquet"));
    }

    [Fact]
    public async Task OpenAsync_returns_null_for_path_outside_pipeline()
    {
        var store = new FakeParquetStore();
        store.Seed("pipe-2/job-1/account.parquet", "a");
        var service = CreateService(store);

        var stream = await service.OpenAsync("pipe-1", "pipe-2/job-1/account.parquet");

        Assert.Null(stream);
    }

    [Fact]
    public async Task OpenAsync_returns_stream_for_valid_path()
    {
        var store = new FakeParquetStore();
        store.Seed("pipe-1/job-1/account.parquet", "hello");
        var service = CreateService(store);

        await using var stream = await service.OpenAsync("pipe-1", "pipe-1/job-1/account.parquet");

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    private static ParquetArtifactService CreateService(IParquetStore store) =>
        new(new FakePipelineService(), new FakeAdlsClientFactory(store));

    private sealed class FakeParquetStore : IParquetStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public void Seed(string path, string content) => _blobs[path] = System.Text.Encoding.UTF8.GetBytes(content);

        public async Task UploadParquetAsync(string blobPath, Stream content, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            _blobs[blobPath] = ms.ToArray();
        }

        public Task<Stream> OpenParquetReadAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_blobs[blobPath]));

        public Task<bool> ParquetExistsAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.ContainsKey(blobPath));

        public Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default)
        {
            _blobs.TryRemove(blobPath, out _);
            return Task.CompletedTask;
        }

        public Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default)
        {
            _blobs[blobPath] = System.Text.Encoding.UTF8.GetBytes(content);
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.TryGetValue(blobPath, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null);

        public Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default)
        {
            var list = _blobs
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new ParquetArtifact(kv.Key, kv.Value.LongLength, null))
                .ToList();
            return Task.FromResult<IReadOnlyList<ParquetArtifact>>(list);
        }
    }

    private sealed class FakeAdlsClientFactory : IAdlsClientFactory
    {
        private readonly IParquetStore _store;

        public FakeAdlsClientFactory(IParquetStore store) => _store = store;

        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store);

        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePipelineService : IPipelineService
    {
        public PipelineModel? Get(string id) =>
            new() { Id = id, Name = id, SourceId = "src-1", DestinationId = "dst-1" };

        public IReadOnlyList<PipelineModel> List() => [];
        public PipelineModel Create(PipelineModel pipeline) => throw new NotSupportedException();
        public PipelineModel? Update(string id, PipelineModel pipeline) => throw new NotSupportedException();
        public bool Delete(string id, bool wipeState, out string? error) => throw new NotSupportedException();
        public PipelineState GetState(string pipelineId) => throw new NotSupportedException();
        public IReadOnlyList<object> GetTables(string pipelineId) => throw new NotSupportedException();
    }
}
