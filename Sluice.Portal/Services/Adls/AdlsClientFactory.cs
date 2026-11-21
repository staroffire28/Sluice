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

    // Prueba una conexión aún sin guardar (botón del formulario de orígenes). Por defecto, la
    // guardada con ese id, para los dobles de test.
    Task<(bool Ok, string Message)> TestAsync(Connection connection, CancellationToken cancellationToken = default) =>
        TestAsync(connection.Id, cancellationToken);

    // Qué almacén de snapshots abre la conexión: dos conexiones con la misma clave ven las mismas
    // carpetas. null si no se puede resolver. Por defecto, la propia conexión, para los dobles de test.
    string? SnapshotStoreKey(string connectionId) => connectionId;
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

    // Un origen local siempre guarda el Parquet en su carpeta, sea cual sea parquet_storage.
    public static bool KeepsParquetOnPortal(Connection connection) =>
        !string.Equals(connection.Type, LocalType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(connection.Config.GetValueOrDefault(ParquetStorageKey), PortalParquetStorage, StringComparison.OrdinalIgnoreCase);

    private IParquetStore ParquetStore(Connection connection)
    {
        if (KeepsParquetOnPortal(connection))
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

    // Sin descifrar: la cuenta y el container no son secretos. Cualquier fallo al leer la conexión
    // (borrada, store ilegible) da null, y la retención lo trata como "podría ser cualquier almacén".
    public string? SnapshotStoreKey(string connectionId)
    {
        try
        {
            return SnapshotStoreKey(
                _connections.GetStored(connectionId),
                _options.IsLocalSourceEnabled ? _options.LocalSourceRoot : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Misma resolución que Build. ADLS: la cuenta y el container de ResolveAdls (AdlsClient lista la
    // raíz del container; no hay prefijo). ResolveAdls solo admite minúsculas en ambos, así que la
    // comparación ordinal ya no distingue mayúsculas, y una URL en la cuenta no se acepta. Origen
    // local: la carpeta {LOCAL_SOURCE_ROOT}/{container}, comparada como el sistema de ficheros.
    // null si Build no podría abrir la conexión (tipo desconocido, config incompleta o inválida).
    public static string? SnapshotStoreKey(Connection? connection, string? localSourceRoot)
    {
        if (connection is null)
        {
            return null;
        }

        try
        {
            if (string.Equals(connection.Type, LocalType, StringComparison.OrdinalIgnoreCase))
            {
                if (localSourceRoot is null)
                {
                    return null;
                }

                var folder = LocalFolderClient.Confine(
                    Path.GetFullPath(localSourceRoot),
                    connection.Config.GetValueOrDefault("container") ?? "",
                    allowBase: false)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return "local|" + (OperatingSystem.IsWindows() ? folder.ToUpperInvariant() : folder);
            }

            if (!string.Equals(connection.Type, "adls", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var settings = ConnectionSettingsFactory.ResolveAdls(connection);
            return $"adls|{settings.Account}|{settings.Container}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
        TestClientAsync(() => Build(Resolve(connectionId)), cancellationToken);

    public Task<(bool Ok, string Message)> TestAsync(Connection connection, CancellationToken cancellationToken = default) =>
        TestClientAsync(() => Build(_connections.Unprotect(connection)), cancellationToken);

    private static async Task<(bool Ok, string Message)> TestClientAsync(Func<IAdlsClient> create, CancellationToken cancellationToken)
    {
        try
        {
            var client = create();
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
