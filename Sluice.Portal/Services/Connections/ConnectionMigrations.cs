using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Connections;

// Cambios en las conexiones guardadas que se aplican una sola vez al arrancar (marcador en
// DATA_DIR/migrations).
public static class ConnectionMigrations
{
    private const string TrustMarker = "sql-trust-server-certificate.done";

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
}
