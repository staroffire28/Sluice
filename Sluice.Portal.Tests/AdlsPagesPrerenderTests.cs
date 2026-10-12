using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Snapshots y Parquet leen ADLS solo con el circuito conectado. En el prerender lo leían y
// persistían el resultado ([PersistentState]) para reutilizarlo; con un catálogo real (200+
// snapshots, ~43 KB) el estado superaba los 32 KB que SignalR acepta en el primer mensaje y
// el circuito se cerraba: la página quedaba pintada pero sin responder.
public sealed class AdlsPagesPrerenderTests : IClassFixture<AdlsPagesPrerenderTests.Factory>
{
    private readonly Factory _factory;

    public AdlsPagesPrerenderTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData("/snapshots")]
    [InlineData("/parquet")]
    public async Task Prerender_does_not_query_adls(string route)
    {
        var client = _factory.CreateAuthenticatedClient();
        await _factory.EnsurePipelineAsync(client);
        var before = _factory.AdlsCalls;

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, _factory.AdlsCalls);
    }

    public sealed class Factory : ApiWebApplicationFactory
    {
        private int _adlsCalls;
        private bool _pipelineCreated;

        public int AdlsCalls => Volatile.Read(ref _adlsCalls);

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.AddSingleton<ISnapshotCatalogService>(new CountingCatalog(this));
            services.AddSingleton<IParquetArtifactService>(new CountingParquet(this));
        }

        // Las dos páginas toman el primer pipeline; sin él no llegan a consultar nada.
        public async Task EnsurePipelineAsync(HttpClient client)
        {
            if (_pipelineCreated)
            {
                return;
            }

            await PostAsync(client, "/api/connections", new Connection
            {
                Id = "conn-src-prerender",
                Name = "Source",
                Role = ConnectionRole.Source,
                Type = "adls",
                Config = new Dictionary<string, string?> { ["account"] = "bogus" },
            });
            await PostAsync(client, "/api/connections", new Connection
            {
                Id = "conn-dst-prerender",
                Name = "Destination",
                Role = ConnectionRole.Destination,
                Type = "sqlserver",
                Config = new Dictionary<string, string?> { ["host"] = "bogus", ["user"] = "sa", ["password"] = "x" },
            });
            await PostAsync(client, "/api/pipelines", new Pipeline
            {
                Id = "pipe-prerender",
                Name = "Prerender",
                SourceId = "conn-src-prerender",
                DestinationId = "conn-dst-prerender",
                Enabled = false,
            });
            _pipelineCreated = true;
        }

        private static async Task PostAsync<T>(HttpClient client, string url, T body) =>
            (await client.PostAsJsonAsync(url, body)).EnsureSuccessStatusCode();

        private void CountCall() => Interlocked.Increment(ref _adlsCalls);

        private sealed class CountingCatalog(Factory factory) : ISnapshotCatalogService
        {
            public Task<SnapshotCatalogResult> GetCatalogAsync(string pipelineId, CancellationToken cancellationToken = default)
            {
                factory.CountCall();
                throw new InvalidOperationException("Sin ADLS en el test.");
            }

            public Task<SnapshotRetentionPreviewResult> GetRetentionPreviewAsync(string pipelineId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public bool IsChangelogStalled(DateTimeOffset? changelogLastModified, DateTimeOffset now) => false;
        }

        private sealed class CountingParquet(Factory factory) : IParquetArtifactService
        {
            public Task<IReadOnlyList<ParquetArtifact>> ListAsync(string pipelineId, CancellationToken cancellationToken = default)
            {
                factory.CountCall();
                return Task.FromResult<IReadOnlyList<ParquetArtifact>>([]);
            }

            public Task<Stream?> OpenAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default) =>
                Task.FromResult<Stream?>(null);

            public Task<bool> DeleteAsync(string pipelineId, string blobPath, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
        }
    }
}
