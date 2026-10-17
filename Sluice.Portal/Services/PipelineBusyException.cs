namespace Sluice.Portal.Services;

// El pipeline ya tiene un job activo, o uno cancelado cuyo stage aún no se ha detenido. Es una
// InvalidOperationException para quien ya las trata así; el scheduler la distingue para no alertar.
public sealed class PipelineBusyException(string message) : InvalidOperationException(message)
{
    // Un solo job activo por pipeline. Un job cancelado cuyo stage aún corre también cuenta: hasta que se
    // detiene de verdad ese stage sigue escribiendo parquet y estado, y otro job lo pisaría.
    public static async Task ThrowIfBusyAsync(
        IJobService jobs, ITaskStore tasks, string pipelineId, string runningMessage, CancellationToken cancellationToken)
    {
        if (jobs.ListRunning().Any(j => j.PipelineId == pipelineId))
        {
            throw new PipelineBusyException(runningMessage);
        }

        if (await tasks.FindLiveClaimAsync(pipelineId, cancellationToken) is { } claim)
        {
            throw new PipelineBusyException(
                $"El job {claim.JobId} se está deteniendo tras la cancelación; espera unos segundos y vuelve a intentarlo.");
        }
    }
}
