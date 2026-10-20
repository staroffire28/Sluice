using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Stores;

// Estado de la última entrega al webhook. En el volumen compartido, para que las transiciones
// (falla / se recupera) se detecten igual envíe la alerta la api o un worker, y Ajustes lo muestre.
public sealed class AlertDeliveryStatusStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public AlertDeliveryStatusStore(IOptions<AppOptions> options) =>
        _path = Path.Combine(options.Value.DataDir, "alerts", "status.json");

    // Null si aún no se ha enviado nada o el fichero no se puede leer.
    public AlertDeliveryStatus? Get()
    {
        lock (_lock)
        {
            return Read();
        }
    }

    // Guarda el resultado de un envío y devuelve el estado anterior.
    public AlertDeliveryStatus? Record(string eventType, AlertDeliveryResult result, DateTimeOffset at)
    {
        lock (_lock)
        {
            var previous = Read();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicJsonFile.Write(_path, new AlertDeliveryStatus
            {
                LastAttemptAt = at,
                LastSucceeded = result.Success,
                LastEventType = eventType,
                LastDetail = result.Detail,
                LastStatusCode = result.StatusCode,
                LastSuccessAt = result.Success ? at : previous?.LastSuccessAt,
                LastFailureAt = result.Success ? previous?.LastFailureAt : at,
            });
            return previous;
        }
    }

    private AlertDeliveryStatus? Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<AlertDeliveryStatus>(stream, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
