using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Exclusión mutua para "¿hay un job en curso? → créalo" de un pipeline: sin ella dos Run
// simultáneos (scheduler, UI, API o dos réplicas) pasaban los dos la guarda. Usa un FileLock
// en DATA_DIR/locks, así que vale entre hilos y entre procesos que comparten el volumen.
public static class PipelineRunLock
{
    public static async Task<IDisposable> AcquireAsync(
        string dataDir,
        string pipelineId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await FileLock.AcquireAsync(
                Path.Combine(dataDir, "locks", $"run-{pipelineId}.lock"), timeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("Ya se está arrancando un job para este pipeline.", ex);
        }
    }
}
