using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Services.Adls;

public interface IAdlsClientFactory
{
    Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default);
}

public sealed class AdlsClientFactory : IAdlsClientFactory
{
    public const string LocalType = "local";

    private readonly IConnectionResolver _connections;
    private readonly AppOptions _options;

    public AdlsClientFactory(IConnectionResolver connections, IOptions<AppOptions> options)
    {
        _connections = connections;
        _options = options.Value;
    }

    public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IAdlsClient>(Build(connectionId));

    public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult((IParquetStore)Build(connectionId));

    private IAdlsClient Build(string connectionId)
    {
        var connection = _connections.GetUnmasked(connectionId)
            ?? throw new InvalidOperationException($"Source connection '{connectionId}' not found.");

        // Origen simulado de las pruebas UAT: solo si el portal arrancó con LOCAL_SOURCE_ROOT.
        if (string.Equals(connection.Type, LocalType, StringComparison.OrdinalIgnoreCase))
        {
            if (!_options.IsLocalSourceEnabled)
            {
                throw new InvalidOperationException($"Connection '{connectionId}' uses the local test source, which is disabled.");
            }

            return new LocalFolderClient(
                _options.LocalSourceRoot!,
                connection.Config.GetValueOrDefault("container") ?? "",
                connection.Config.GetValueOrDefault("parquet_container"));
        }

        if (!string.Equals(connection.Type, "adls", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Connection '{connectionId}' is not an ADLS source.");
        }

        var settings = ConnectionSettingsFactory.ResolveAdls(connection);
        return new AdlsClient(settings);
    }

    public async Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await CreateAsync(connectionId, cancellationToken);
            await client.TestAsync(cancellationToken);
            var settled = await client.GetSettledSnapshotAsync(cancellationToken);
            return (true, settled is null
                ? "ADLS reachable. Changelog empty — export in progress."
                : $"ADLS reachable. Settled snapshot: {settled}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
