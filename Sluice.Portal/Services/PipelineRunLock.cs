using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Exclusión mutua para "¿hay un job en curso? → créalo" de un pipeline: sin ella dos Run
// simultáneos (scheduler, UI, API o dos réplicas) pasaban los dos la guarda. Usa un FileLock
// en DATA_DIR/locks, así que vale entre hilos y entre procesos que comparten el volumen.
public static class PipelineRunLock
{
    // Quien espera el lock lo hace mientras el otro arranque termina (Force incluye un listado
    // de ADLS); pasado este margen se rechaza igual que si ya hubiera un job en curso.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

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
        catch (TimeoutException)
        {
            throw new PipelineBusyException("El pipeline está ocupado (arranque o reset en curso).", "Error.PipelineLocked");
        }
    }
}
