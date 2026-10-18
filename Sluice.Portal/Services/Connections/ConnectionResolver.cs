using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Connections;

public interface IConnectionResolver
{
    Connection? GetUnmasked(string id);

    // Descifra los secretos de una conexión que no viene del store tal cual (p. ej. la del diálogo
    // de Destinos, con la contraseña guardada mezclada). Los valores en claro pasan sin tocar.
    Connection Unprotect(Connection connection) => connection;
}

public sealed class ConnectionResolver : IConnectionResolver
{
    private readonly ConnectionStore _store;
    private readonly IConnectionSecretProtector _protector;

    public ConnectionResolver(ConnectionStore store, IConnectionSecretProtector protector)
    {
        _store = store;
        _protector = protector;
    }

    // Único punto donde el secreto vuelve a claro: justo antes de que los factories
    // (ADLS/SQL) construyan la conexión. Valores legacy en claro pasan sin tocar.
    public Connection? GetUnmasked(string id)
    {
        var connection = _store.GetById(id);
        return connection is null ? null : Unprotect(connection);
    }

    public Connection Unprotect(Connection connection) =>
        connection with { Config = UnprotectConfig(connection.Config) };

    private Dictionary<string, string?> UnprotectConfig(IReadOnlyDictionary<string, string?> config)
    {
        var result = new Dictionary<string, string?>(config.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config)
        {
            result[key] = _protector.Unprotect(value);
        }

        return result;
    }
}

public sealed record AdlsConnectionSettings(
    string Account,
    string Container,
    string TenantId,
    string ClientId,
    string ClientSecret,
    string? ParquetContainer);

public sealed record SqlConnectionSettings(
    string ConnectionString,
    SqlServerVersion Version);

public static class ConnectionSettingsFactory
{
    // La cuenta y los containers se interpolan en la URL de ADLS: un "atacante.com/" o un "x#" en la
    // cuenta mandaba el token de Entra (obtenido con el secreto guardado) a otro host. Solo se aceptan
    // nombres válidos de Azure Storage.
    private static readonly System.Text.RegularExpressions.Regex AccountName = new("^[a-z0-9]{3,24}$");
    private static readonly System.Text.RegularExpressions.Regex ContainerName = new("^(?!.*--)[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$");

    public static AdlsConnectionSettings ResolveAdls(Connection connection)
    {
        var config = connection.Config;
        var account = Get(config, "account", "storage_account")
            ?? throw new InvalidOperationException("ADLS storage account is not configured.");
        var container = Get(config, "container")
            ?? throw new InvalidOperationException("ADLS container is not configured.");
        var tenantId = Get(config, "tenant_id")
            ?? throw new InvalidOperationException("Azure tenant ID is not configured.");
        var clientId = Get(config, "client_id")
            ?? throw new InvalidOperationException("Azure client ID is not configured.");
        var clientSecret = Get(config, "client_secret")
            ?? throw new InvalidOperationException("Azure client secret is not configured.");
        // Container de destino del parquet (durable, separado del origen Synapse Link).
        // Opcional aquí; se exige solo al usarse (ver IParquetStore en AdlsClient).
        var parquetContainer = Get(config, "parquet_container", "destination_container");
        if (!AccountName.IsMatch(account))
        {
            throw new InvalidOperationException($"'{account}' no es un nombre de cuenta de Azure Storage válido (3-24 minúsculas o dígitos).");
        }

        foreach (var name in new[] { container, parquetContainer }.OfType<string>())
        {
            if (!ContainerName.IsMatch(name))
            {
                throw new InvalidOperationException($"'{name}' no es un nombre de container válido (3-63 minúsculas, dígitos o guiones).");
            }
        }

        return new AdlsConnectionSettings(account, container, tenantId, clientId, clientSecret, parquetContainer);
    }

    public static SqlConnectionSettings ResolveSql(Connection connection)
    {
        var config = connection.Config;
        var version = SqlServerVersions.Parse(Get(config, SqlServerVersions.ConfigKey));

        if (config.TryGetValue("connection_string", out var cs) && !string.IsNullOrWhiteSpace(cs))
        {
            return new SqlConnectionSettings(cs, version);
        }

        var host = Get(config, "host", "server")
            ?? throw new InvalidOperationException("SQL Server host is not configured.");
        var database = Get(config, "database")
            ?? throw new InvalidOperationException("SQL Server database is not configured.");
        var user = Get(config, "user", "username")
            ?? throw new InvalidOperationException("SQL Server user is not configured.");
        var password = Get(config, "password")
            ?? throw new InvalidOperationException("SQL Server password is not configured.");
        var trust = Get(config, "trust_server_certificate");

        // El builder escapa cada valor: un ';' o '=' en la contraseña o el host ya no puede
        // añadir palabras clave a la cadena (antes se interpolaba). Mismos valores por defecto.
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = host,
            InitialCatalog = database,
            UserID = user,
            Password = password,
            Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory,
            // Se valida el certificado del servidor salvo que la conexión pida confiar en él (un SQL
            // con certificado autofirmado): sin validarlo, un MITM capturaría el login.
            TrustServerCertificate = bool.TryParse(trust, out var trustValue) && trustValue,
        };

        return new SqlConnectionSettings(builder.ConnectionString, version);
    }

    public static SqlServerVersion GetSqlVersion(Connection connection) =>
        SqlServerVersions.Parse(Get(connection.Config, SqlServerVersions.ConfigKey));

    public static string GetSchema(Connection connection) =>
        Get(connection.Config, "schema") ?? "dbo";

    private static string? Get(IReadOnlyDictionary<string, string?> config, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}