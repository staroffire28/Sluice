using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Services.Pipeline;

public interface ISqlConnectionFactory
{
    Task<SqlConnection> OpenAsync(string connectionId, CancellationToken cancellationToken = default);
    Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default);
    string GetSchema(string connectionId);
    SqlServerVersion GetSqlVersion(string connectionId);

    // Conecta con una conexión (guardada o no, con los secretos en claro) y devuelve la versión
    // mayor del servidor (SERVERPROPERTY('ProductMajorVersion')). Lanza si no conecta. Por defecto
    // no soportado, para los dobles de test que no lo ejercitan.
    Task<int> DetectMajorVersionAsync(Connection connection, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly IConnectionResolver _connections;

    public SqlConnectionFactory(IConnectionResolver connections)
    {
        _connections = connections;
    }

    public async Task<SqlConnection> OpenAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var settings = Resolve(connectionId);
        var connection = new SqlConnection(ForPipeline(settings.ConnectionString));
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            // Si no abre (servidor caído, credenciales), la conexión no llega al caller que la
            // dispondría: se libera aquí.
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    // Las sentencias del pipeline corren sin límite de tiempo (los comandos heredan el
    // CommandTimeout de la conexión): un MERGE, un borrado de huérfanas o un índice sobre una tabla
    // grande pasan de los 30 s por defecto. Un posible bloqueo no se corta: lo avisa SqlStallWatch.
    internal static string ForPipeline(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { CommandTimeout = 0 };
        return builder.ConnectionString;
    }

    // Connect Timeout corto SOLO para el test de conexión: una sonda de alcanzabilidad no
    // debe colgar el spinner de la UI hasta el default de 15s contra un host inalcanzable.
    // El pipeline real (OpenAsync) mantiene el timeout configurado: un MERGE puede correr
    // contra un servidor cargado donde 5s para establecer la conexión sería demasiado justo.
    private const int TestConnectTimeoutSeconds = 5;

    public async Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = Resolve(connectionId);
            var major = await ProbeMajorVersionAsync(settings.ConnectionString, cancellationToken);
            var detected = SqlServerVersions.FromMajorVersion(major);
            var configured = settings.Version;
            var detectedLabel = SqlServerVersions.DisplayName(detected);
            var configuredLabel = SqlServerVersions.DisplayName(configured);
            var message = $"{detectedLabel} detectado (major {major}).";
            if (!SqlServerVersions.IsServerCompatible(configured, detected))
            {
                return (false,
                    $"{message} Configurado como {configuredLabel}: el servidor es más antiguo y puede fallar con el T-SQL actual.");
            }

            if (detected != configured)
            {
                message += $" Configurado como {configuredLabel} (compatible).";
            }

            return (true, message);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task<int> DetectMajorVersionAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(connection.Type, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Connection '{connection.Name}' is not a SQL Server destination.");
        }

        // Al editar, la contraseña guardada llega cifrada (ConnectionService la toma del store).
        var settings = ConnectionSettingsFactory.ResolveSql(_connections.Unprotect(connection));
        return ProbeMajorVersionAsync(settings.ConnectionString, cancellationToken);
    }

    private static async Task<int> ProbeMajorVersionAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = TestConnectTimeoutSeconds,
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS INT)";
        var majorObj = await cmd.ExecuteScalarAsync(cancellationToken);
        return majorObj is int m ? m : Convert.ToInt32(majorObj);
    }

    public string GetSchema(string connectionId)
    {
        var connection = _connections.GetUnmasked(connectionId)
            ?? throw new InvalidOperationException($"Destination connection '{connectionId}' not found.");
        return ConnectionSettingsFactory.GetSchema(connection);
    }

    public SqlServerVersion GetSqlVersion(string connectionId)
    {
        var connection = _connections.GetUnmasked(connectionId)
            ?? throw new InvalidOperationException($"Destination connection '{connectionId}' not found.");
        return ConnectionSettingsFactory.GetSqlVersion(connection);
    }

    private SqlConnectionSettings Resolve(string connectionId)
    {
        var connection = _connections.GetUnmasked(connectionId)
            ?? throw new InvalidOperationException($"Destination connection '{connectionId}' not found.");

        if (!string.Equals(connection.Type, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Connection '{connectionId}' is not a SQL Server destination.");
        }

        return ConnectionSettingsFactory.ResolveSql(connection);
    }
}