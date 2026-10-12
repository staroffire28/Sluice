namespace Sluice.Portal.Services.Stores;

// Lock entre procesos basado en un archivo abierto en exclusiva (FileShare.None) que se
// borra al cerrarse. Vale entre hilos y entre réplicas que comparten el volumen de DATA_DIR,
// que es donde el lock en memoria no llega (modo distribuido: api y workers).
public static class FileLock
{
    public static IDisposable Acquire(string path, TimeSpan timeout)
    {
        var deadline = Prepare(path, timeout);
        while (true)
        {
            if (TryOpen(path, deadline, out var handle))
            {
                return handle!;
            }

            Thread.Sleep(20);
        }
    }

    public static async Task<IDisposable> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = Prepare(path, timeout);
        while (true)
        {
            if (TryOpen(path, deadline, out var handle))
            {
                return handle!;
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private static DateTimeOffset Prepare(string path, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return DateTimeOffset.UtcNow + timeout;
    }

    private static bool TryOpen(string path, DateTimeOffset deadline, out IDisposable? handle)
    {
        try
        {
            handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
            return true;
        }
        // En Windows, abrir un archivo pendiente de borrado da UnauthorizedAccessException.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"No se pudo obtener el lock {Path.GetFileName(path)}.", ex);
            }

            handle = null;
            return false;
        }
    }
}
