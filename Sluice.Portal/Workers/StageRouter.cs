using System.Text.Json;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Workers;

public interface IStageRouter
{
    Task RouteCompletedTaskAsync(PipelineTask task, CancellationToken cancellationToken = default);
}

public sealed class StageRouter : IStageRouter
{
    private readonly ITaskStore _tasks;
    private readonly IJobService _jobs;
    private readonly IOrchestrationService? _orchestration;
    private readonly SettingsStore? _settings;
    private readonly AlertService? _alerts;

    // Las dependencias de la corrección automática son opcionales: sin ellas el router solo encadena
    // etapas (así lo construyen los tests que no la ejercitan).
    public StageRouter(
        ITaskStore tasks,
        IJobService jobs,
        IOrchestrationService? orchestration = null,
        SettingsStore? settings = null,
        AlertService? alerts = null)
    {
        _tasks = tasks;
        _jobs = jobs;
        _orchestration = orchestration;
        _settings = settings;
        _alerts = alerts;
    }

    public async Task RouteCompletedTaskAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(task.JobId);
        if (job is null)
        {
            return;
        }

        if (job.Status is JobStatus.Blocked or JobStatus.Error or JobStatus.Cancelled)
        {
            return;
        }

        switch (task.Stage)
        {
            case PipelineStage.Ingest:
                // Igual que con SQL: conservar el estado real del ingest ("error" en un ingest
                // parcial); forzar "done" ocultaba el fallo en la UI y en RetryJobAsync.
                var ingestStage = job.StageStatus.GetValueOrDefault("ingest", "done");
                job = job with
                {
                    CurrentStage = PipelineStage.Sql,
                    StageStatus = UpdateStage(job.StageStatus, "ingest", ingestStage, "sql", "pending"),
                };
                if (!SavedAndStillRunnable(job))
                {
                    return;
                }

                await _tasks.EnqueueAsync(new PipelineTask
                {
                    Id = $"task-{task.JobId}-sql",
                    Stage = PipelineStage.Sql,
                    PipelineId = task.PipelineId,
                    JobId = task.JobId,
                    Payload = task.Payload,
                }, cancellationToken);
                break;

            case PipelineStage.Sql:
                // Conservar el estado real de la etapa SQL (p. ej. "error" en fallos parciales);
                // no forzar "done" si SqlExecutor marcó errores de entidad.
                var sqlStage = job.StageStatus.GetValueOrDefault("sql", "done");
                job = job with
                {
                    CurrentStage = PipelineStage.Reconcile,
                    StageStatus = UpdateStage(job.StageStatus, "sql", sqlStage, "reconcile", "pending"),
                };
                if (!SavedAndStillRunnable(job))
                {
                    return;
                }

                await _tasks.EnqueueAsync(new PipelineTask
                {
                    Id = $"task-{task.JobId}-reconcile",
                    Stage = PipelineStage.Reconcile,
                    PipelineId = task.PipelineId,
                    JobId = task.JobId,
                    Payload = task.Payload,
                }, cancellationToken);
                break;

            case PipelineStage.Reconcile:
                job = _jobs.Get(task.JobId);
                if (job is null)
                {
                    return;
                }

                var finalStatus = job.Status == JobStatus.Partial ? JobStatus.Partial : JobStatus.Ok;
                job = job with
                {
                    CurrentStage = null,
                    Status = finalStatus,
                    FinishedAt = DateTimeOffset.UtcNow,
                    StageStatus = UpdateStage(job.StageStatus, "reconcile", "done"),
                };
                _jobs.Update(job.Id, job);

                // Después de cerrar el job: con él aún activo, la guarda anti doble run rechazaría el re-sync.
                var launch = await TryAutoRemediateAsync(job, cancellationToken);
                await SendPendingReconcileAlertAsync(job, launch, cancellationToken);
                break;
        }
    }

    // Corrección automática: un re-sync (recarga) de las tablas accionables que dejó la conciliación.
    // Nunca hace fallar la tarea: el job ya está cerrado y un error aquí solo se registra. Devuelve null
    // si no se intentó (desactivada o sin tablas), o si se lanzó y, si no, por qué.
    private async Task<(bool Launched, string? Reason)?> TryAutoRemediateAsync(Job job, CancellationToken cancellationToken)
    {
        if (_orchestration is null || _settings is null
            || !_settings.Get().AutoRemediate
            || !AutoRemediation.IsEligible(job.Type))
        {
            return null;
        }

        var entities = AutoRemediation.EntitiesFrom(job);
        if (entities.Count == 0)
        {
            return null;
        }

        try
        {
            // El registro de actividad lo deja el re-sync, con el actor AutoFix.
            var fix = await _orchestration.ResyncTablesAsync(AuditActor.AutoFix, job.PipelineId, entities, ResyncMode.Reload, cancellationToken);
            _jobs.AppendEvent(job.Id, EventLevel.Info, $"Corrección automática: el job {fix.Id} re-sincroniza {string.Join(", ", entities)}.",
                null, "Event.AutoFix.Launched", fix.Id, string.Join(", ", entities));
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // P. ej. "Ya hay un job en ejecución" si el scheduler arrancó otro entretanto.
            _jobs.AppendEvent(job.Id, EventLevel.Warning, $"Corrección automática no lanzada: {ex.Message}", null, "Event.AutoFix.NotLaunched", ex.Message);
            return (false, ex.Message);
        }
    }

    // El aviso de conciliación que la corrección automática podía resolver (ReconcileExecutor lo dejó
    // en el Summary) sale ahora, con lo que pasó de verdad: lanzada, no lanzada (y por qué) o desactivada.
    private async Task SendPendingReconcileAlertAsync(Job job, (bool Launched, string? Reason)? launch, CancellationToken cancellationToken)
    {
        if (_alerts is null
            || !job.Summary.TryGetValue(AutoRemediation.PendingAlertKey, out var raw)
            || raw?.ToString() is not { Length: > 0 } json
            || JsonSerializer.Deserialize<ReconcileAlert>(json, JsonDefaults.Options) is not { } alert)
        {
            return;
        }

        if (launch is { } attempt)
        {
            alert = alert with
            {
                AutoFixLaunched = attempt.Launched,
                Message = attempt.Launched ? alert.Message : $"{alert.Message} Automatic correction could not be launched: {attempt.Reason}",
            };
        }

        await _alerts.SendReconcileAlertAsync(alert, cancellationToken);
    }

    // El job se leyó al empezar a rutear: si entretanto se canceló, Update conserva el estado
    // Cancelado y aquí no se encola el siguiente stage.
    private bool SavedAndStillRunnable(Job job)
    {
        var saved = _jobs.Update(job.Id, job);
        return saved is not null && saved.Status is not (JobStatus.Cancelled or JobStatus.Blocked or JobStatus.Error);
    }

    private static Dictionary<string, string> UpdateStage(
        Dictionary<string, string> current,
        string stage,
        string value,
        string? nextStage = null,
        string? nextValue = null)
    {
        var copy = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase)
        {
            [stage] = value,
        };

        if (nextStage is not null && nextValue is not null)
        {
            copy[nextStage] = nextValue;
        }

        return copy;
    }
}