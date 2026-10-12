using System.Text.Json;

namespace Sluice.Portal.Services.Stores;

// Escritura atómica de JSON: serializa a un archivo .tmp y lo renombra (File.Move es
// un rename atómico dentro del mismo volumen). Un crash a mitad deja el archivo previo
// intacto en vez de un JSON truncado — que, con secretos cifrados, sería irrecuperable.
// Punto único para todo el estado JSON bajo DATA_DIR (stores, reportes, manifests, settings).
internal static class AtomicJsonFile
{
    // El .tmp es único por escritura: con un nombre fijo, dos escrituras simultáneas del mismo
    // archivo (réplicas, o UI y worker sobre el mismo job) chocaban al crearlo.
    public static void Write<T>(string path, T item)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, item, JsonDefaults.Options);
        }

        try
        {
            MoveWithRetry(tmp, path);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }

    // En Windows el rename sobre un destino que otro proceso tiene abierto (un lector de la UI o
    // de otra réplica) falla de forma transitoria: se reintenta con un backoff corto, como en
    // TaskStore, antes de rendirse.
    private static void MoveWithRetry(string source, string destination)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    // Como Write, pero solo si el destino no existe (File.Move sin overwrite falla de forma
    // atómica en Windows y Linux). Devuelve false si ya existía; el .tmp es único para que
    // dos creaciones simultáneas del mismo id no choquen al escribirlo.
    public static bool WriteNew<T>(string path, T item)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, item, JsonDefaults.Options);
        }

        try
        {
            File.Move(tmp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(tmp);
            return false;
        }
    }
}
