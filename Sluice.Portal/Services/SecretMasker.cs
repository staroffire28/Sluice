using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class SecretMasker
{
    public const string MaskValue = "***";

    // Whitelist por patrón en vez de una lista cerrada de nombres exactos: un tipo de
    // conexión nuevo con un campo sensible (p.ej. "sas_token" o "account_key" para ADLS
    // con SAS) queda enmascarado sin tener que acordarse de actualizar esta lista a mano.
    // Cubre los nombres reales de hoy (password, client_secret, connection_string) y
    // cualquier variante futura razonable.
    private static readonly string[] SecretPatterns = ["secret", "password", "token", "key", "connection_string"];

    public static Connection Mask(Connection connection) =>
        connection with
        {
            Config = MaskDictionary(connection.Config),
        };

    public static Dictionary<string, string?> MaskDictionary(IReadOnlyDictionary<string, string?> source)
    {
        var masked = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            masked[key] = IsSecretKey(key) && !string.IsNullOrEmpty(value) ? MaskValue : value;
        }

        return masked;
    }

    public static bool IsSecretKey(string key) =>
        SecretPatterns.Any(pattern => key.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}