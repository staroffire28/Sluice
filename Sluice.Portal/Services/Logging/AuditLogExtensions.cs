using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Logging;

// Lo que se registra cuando la acción termina sin excepción.
public readonly record struct AuditResult(string Message, string? JobId = null, AuditOutcome Outcome = AuditOutcome.Ok);

public static class AuditLogExtensions
{
    // Ejecuta la acción y registra su resultado una sola vez. Si lanza, se registra Rejected (una
    // regla: pipeline ocupado, validación) o Error, y la excepción sigue su camino. No se registra:
    // - una cancelación: quien pidió la acción cortó;
    // - un pipeline ocupado para el scheduler o la cola: lo reintentan en la siguiente vuelta, y
    //   registrarlo dejaría un aviso por poll mientras dure el job en curso.
    public static async Task<T> AuditAsync<T>(
        this IAuditLog audit,
        AuditActor actor,
        string action,
        string? pipelineId,
        Func<Task<T>> run,
        Func<T, AuditResult> describe,
        string failure,
        string? jobId = null,
        string? target = null)
    {
        T value;
        try
        {
            value = await run();
        }
        catch (Exception ex) when (ShouldRecord(actor, ex))
        {
            audit.Record(actor, action, OutcomeOf(ex), pipelineId, jobId, target, $"{failure}: {ex.Message}");
            throw;
        }

        var result = describe(value);
        audit.Record(actor, action, result.Outcome, pipelineId, result.JobId ?? jobId, target, result.Message);
        return value;
    }

    public static T Audit<T>(
        this IAuditLog audit,
        AuditActor actor,
        string action,
        string? pipelineId,
        Func<T> run,
        Func<T, AuditResult> describe,
        string failure,
        string? jobId = null,
        string? target = null)
    {
        T value;
        try
        {
            value = run();
        }
        catch (Exception ex) when (ShouldRecord(actor, ex))
        {
            audit.Record(actor, action, OutcomeOf(ex), pipelineId, jobId, target, $"{failure}: {ex.Message}");
            throw;
        }

        var result = describe(value);
        audit.Record(actor, action, result.Outcome, pipelineId, result.JobId ?? jobId, target, result.Message);
        return value;
    }

    // InvalidOperationException incluye PipelineBusyException y el timeout del lock de ejecución.
    private static AuditOutcome OutcomeOf(Exception ex) =>
        ex is InvalidOperationException or ArgumentException ? AuditOutcome.Rejected : AuditOutcome.Error;

    // El scheduler reintenta en cada poll: un pipeline ocupado o retenido por un restore a medias no
    // deja una entrada por poll (la retención ya se avisa una vez, ver PipelineSchedulerService).
    private static bool ShouldRecord(AuditActor actor, Exception ex) =>
        ex is not OperationCanceledException
        && !(ex is PipelineBusyException && (actor == AuditActor.Scheduler || actor == AuditActor.Queue))
        && !(ex is RestorePendingException && actor == AuditActor.Scheduler);
}
