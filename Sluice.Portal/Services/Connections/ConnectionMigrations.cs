using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Connections;

// Cambios en las conexiones guardadas que se aplican una sola vez al arrancar (marcador en
// DATA_DIR/migrations).
public static class ConnectionMigrations
{
    private const string TrustMarker = "sql-trust-server-certificate.done";
    private const string ConnectionStringMarker = "sql-connection-string-removed.done";

    // Marca de una conexión de destino cuya connection_string no se pudo pasar a campos: lleva las
    // claves que lo impidieron. Sus pipelines no arrancan hasta que se escriban los campos.
    public const string FieldsRequiredKey = "fields_required";

    // Claves de la cadena que se pasan a campos o que no cambian a dónde ni cómo se autentica la
    // conexión (Command Timeout se descarta: las sentencias del pipeline ya corren sin límite).
    private static readonly HashSet<string> ConvertibleKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data Source", "Initial Catalog", "User ID", "Password", "TrustServerCertificate", "Trust Server Certificate",
        "Encrypt", "Connect Timeout", "Command Timeout", "Application Name",
    };

    // El certificado del servidor SQL pasa a validarse por defecto. Las conexiones anteriores no
    // tenían la opción (el formulario no la ofrecía) y confiaban en cualquiera: se deja "confiar"
    // explícito en ellas para que sigan funcionando igual (p. ej. un SQL con certificado
    // autofirmado). Solo una vez: una conexión creada después sin la clave valida el certificado.
    public static void PinTrustServerCertificate(ConnectionStore store, string dataDir)
    {
        var marker = Path.Combine(dataDir, "migrations", TrustMarker);
        if (File.Exists(marker))
        {
            return;
        }

        foreach (var connection in store.ListAll())
        {
            if (!string.Equals(connection.Type, "sqlserver", StringComparison.OrdinalIgnoreCase)
                || connection.Config.ContainsKey("trust_server_certificate")
                || connection.Config.ContainsKey("connection_string"))
            {
                continue;
            }

            store.Save(connection with
            {
                Config = new Dictionary<string, string?>(connection.Config, StringComparer.OrdinalIgnoreCase)
                {
                    ["trust_server_certificate"] = "true",
                },
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
    }

    // La cadena de conexión SQL libre (connection_string) deja de usarse: permitía apuntar a cualquier
    // servidor con cualquier autenticación. Una vez: la que solo lleva claves de ConvertibleKeys pasa a
    // campos (la contraseña se vuelve a cifrar al guardar); la que lleva otras (Integrated Security,
    // Authentication, Failover Partner...) se quita igual y la conexión queda marcada con
    // FieldsRequiredKey. Cada una deja una entrada en el registro de actividad.
    public static void RemoveSqlConnectionStrings(ConnectionStore store, IConnectionResolver resolver, IAuditLog audit, string dataDir)
    {
        var marker = Path.Combine(dataDir, "migrations", ConnectionStringMarker);
        if (File.Exists(marker))
        {
            return;
        }

        foreach (var saved in store.ListAll())
        {
            if (!saved.Config.ContainsKey("connection_string"))
            {
                continue;
            }

            var connection = resolver.Unprotect(saved);
            var config = new Dictionary<string, string?>(connection.Config, StringComparer.OrdinalIgnoreCase);
            var blocking = Convert(config["connection_string"], config);
            config.Remove("connection_string");
            if (blocking is null)
            {
                store.Save(connection with { Config = config });
                audit.Record(AuditActor.Migration, "connection.migrate", AuditOutcome.Ok, null, null, connection.Id,
                    $"Conexión de destino '{connection.Name}': la cadena de conexión pasa a campos.", null,
                    "Activity.Connection.ConnectionStringConverted", connection.Name);
                continue;
            }

            config[FieldsRequiredKey] = blocking;
            store.Save(connection with { Config = config });
            audit.Record(AuditActor.Migration, "connection.migrate", AuditOutcome.Rejected, null, null, connection.Id,
                $"Conexión de destino '{connection.Name}': su cadena de conexión usaba opciones que ya no se admiten ({blocking}) y se ha quitado; sus pipelines no arrancan hasta que se escriban sus campos.",
                "warning", "Activity.Connection.ConnectionStringNeedsFields", connection.Name, blocking);
        }

        // Atómico: un proceso que cae a medio escribir no deja un marcador vacío que se dé por hecho.
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        var temp = marker + ".tmp";
        File.WriteAllText(temp, DateTimeOffset.UtcNow.ToString("O"));
        File.Move(temp, marker, overwrite: true);
    }

    // Pasa la cadena a host, database, user, password y trust_server_certificate. Devuelve null si se
    // pudo, o las claves que lo impiden. Los campos siempre cifran, así que, para que la conexión siga
    // funcionando igual: TrustServerCertificate explícito se respeta; si no, una cadena que no activaba
    // el cifrado (Encrypt ausente, False/No/Optional) no validaba el certificado y queda en "confiar"
    // (como en PinTrustServerCertificate), y una que lo activaba (True/Mandatory/Strict) lo valida.
    private static string? Convert(string? connectionString, Dictionary<string, string?> config)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return "connection_string";
        }

        var blocking = builder.Keys.Cast<string>()
            .Where(key => builder.ShouldSerialize(key) && !ConvertibleKeys.Contains(key))
            .ToList();
        if (blocking.Count > 0)
        {
            return string.Join(", ", blocking);
        }

        config["host"] = builder.DataSource;
        config["database"] = builder.InitialCatalog;
        config["user"] = builder.UserID;
        config["password"] = builder.Password;
        var trust = builder.ShouldSerialize("TrustServerCertificate")
            ? builder.TrustServerCertificate
            : !builder.ShouldSerialize("Encrypt") || builder.Encrypt == SqlConnectionEncryptOption.Optional;
        config["trust_server_certificate"] = trust ? "true" : "false";
        return null;
    }
}
