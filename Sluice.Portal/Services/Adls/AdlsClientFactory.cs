using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Services.Adls;

public interface IAdlsClientFactory
{
    Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default);

    // El almacén de una conexión aún sin guardar (copia al cambiar parquet_storage). Por defecto,
    // el de la conexión guardada con ese id, para los dobles de test.
    Task<IParquetStore> CreateParquetStoreAsync(Connection connection, CancellationToken cancellationToken = default) =>
        CreateParquetStoreAsync(connection.Id, cancellationToken);
    Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default);
}

public sealed class AdlsClientFactory : IAdlsClientFactory
{
    public const string LocalType = "local";

    // parquet_storage de un origen ADLS: dónde se guarda el Parquet. "adls" (por defecto, el
    // parquet_container de la cuenta) o "portal" (DATA_DIR/parquet, el volumen del portal).
    public const string ParquetStorageKey = "parquet_storage";
    public const string AdlsParquetStorage = "adls";
    public const string PortalParquetStorage = "portal";

    private readonly IConnectionResolver _connections;
    private readonly AppOptions _options;

    public AdlsClientFactory(IConnectionResolver connections, IOptions<AppOptions> options)
    {
        _connections = connections;
        _options = options.Value;
    }

    public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Build(Resolve(connectionId)));

    public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParquetStore(Resolve(connectionId)));

    public Task<IParquetStore> CreateParquetStoreAsync(Connection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParquetStore(_connections.Unprotect(connection)));

    private Connection Resolve(string connectionId) =>
        _connections.GetUnmasked(connectionId)
            ?? throw new InvalidOperationException($"Source connection '{connectionId}' not found.");

    private IParquetStore ParquetStore(Connection connection)
    {
        if (!string.Equals(connection.Type, LocalType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(connection.Config.GetValueOrDefault(ParquetStorageKey), PortalParquetStorage, StringComparison.OrdinalIgnoreCase))
        {
            return new LocalParquetStore(Path.Combine(_options.DataDir, "parquet"));
        }

        return (IParquetStore)Build(connection);
    }

    private IAdlsClient Build(Connection connection)
    {
        var connectionId = connection.Id;

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
