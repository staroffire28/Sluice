using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Services;

// El destino venía de una connection_string que la migración no pudo pasar a campos (ver
// ConnectionMigrations): hasta que alguien escriba sus campos en Destinos, sus pipelines no arrancan.
// Es una InvalidOperationException para quien ya las trata así (la API la devuelve como 400).
public sealed class ConnectionFieldsRequiredException(string connectionName, string keys)
    : InvalidOperationException(
        $"La conexión de destino '{connectionName}' usaba una cadena de conexión con opciones que ya no se admiten ({keys}): vuelve a escribir sus campos en Destinos.")
{
    public string ConnectionName { get; } = connectionName;

    public static void ThrowIfMarked(Connection connection)
    {
        if (connection.Config.GetValueOrDefault(ConnectionMigrations.FieldsRequiredKey) is { Length: > 0 } keys)
        {
            throw new ConnectionFieldsRequiredException(connection.Name, keys);
        }
    }
}
