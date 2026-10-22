using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Logging;

// Lo que se registra cuando la acción termina sin excepción. Level: solo si no basta el del outcome.
// MessageKey y MessageArgs: el texto localizable (ActivityLogEntry.MessageKey); Message sigue siendo
// el texto guardado.
public readonly record struct AuditResult(
    string Message, string? JobId = null, AuditOutcome Outcome = AuditOutcome.Ok, string? Level = null,
    string? MessageKey = null, object?[]? MessageArgs = null);

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
        string? target = null,
        string? failureKey = null,
        object?[]? failureArgs = null)
    {
        T value;
        try
        {
            value = await run();
        }
        catch (Exception ex) when (ShouldRecord(actor, ex))
        {
            RecordFailure(audit, actor, action, pipelineId, jobId, target, failure, failureKey, failureArgs, ex);
            throw;
        }

        RecordResult(audit, actor, action, pipelineId, jobId, target, describe(value));
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
        string? target = null,
        string? failureKey = null,
        object?[]? failureArgs = null)
    {
        T value;
        try
        {
            value = run();
        }
        catch (Exception ex) when (ShouldRecord(actor, ex))
        {
            RecordFailure(audit, actor, action, pipelineId, jobId, target, failure, failureKey, failureArgs, ex);
            throw;
        }

        RecordResult(audit, actor, action, pipelineId, jobId, target, describe(value));
        return value;
    }

    // Con clave, "{failure}: {mensaje de la excepción}" se guarda como Activity.Failure con el fallo
    // como parte (EventPart); la excepción queda en su idioma.
    private static void RecordFailure(IAuditLog audit, AuditActor actor, string action, string? pipelineId, string? jobId, string? target,
        string failure, string? failureKey, object?[]? failureArgs, Exception ex)
    {
        var message = $"{failure}: {ex.Message}";
        if (failureKey is null)
        {
            audit.Record(actor, action, OutcomeOf(ex), pipelineId, jobId, target, message);
        }
        else
        {
            audit.Record(actor, action, OutcomeOf(ex), pipelineId, jobId, target, message, null,
                "Activity.Failure", new EventPart(failureKey, failureArgs ?? []), ex.Message);
        }
    }

    private static void RecordResult(IAuditLog audit, AuditActor actor, string action, string? pipelineId, string? jobId, string? target, AuditResult result)
    {
        if (result.MessageKey is null)
        {
            audit.Record(actor, action, result.Outcome, pipelineId, result.JobId ?? jobId, target, result.Message, result.Level);
        }
        else
        {
            audit.Record(actor, action, result.Outcome, pipelineId, result.JobId ?? jobId, target, result.Message, result.Level,
                result.MessageKey, result.MessageArgs ?? []);
        }
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
