using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Pipeline;

// Escritor del progreso de UNA ejecución de etapa (lo crea JobProgressStore.Begin). Los
// executors lo usan sin comprobar nada: StageProgress.None (sin store) no hace nada.
// Vuelca a disco en las fronteras de paso y cuando cambia el elemento o la fase en curso; los
// cambios que solo mueven el contador, como mucho una vez por segundo salvo force (la UI sondea
// cada 2-3 s). Sin volcar el cambio de fase, un MERGE de minutos se veía en "ensureTable".
public sealed class StageProgress
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    public static StageProgress None { get; } = new(null, "", "", new JobProgress { JobId = "" });

    private readonly JobProgressStore? _store;
    private readonly string _pipelineId;
    private readonly string _stage;
    private readonly CancellationToken _cancellationToken;
    private JobProgress _progress;
    private DateTimeOffset _lastFlush;

    internal StageProgress(
        JobProgressStore? store, string pipelineId, string stage, JobProgress progress, CancellationToken cancellationToken = default)
    {
        _store = store;
        _pipelineId = pipelineId;
        _stage = stage;
        _progress = progress;
        _cancellationToken = cancellationToken;
    }

    public void StartStep(string key, int total = 0)
    {
        if (_store is null)
        {
            return;
        }

        SetStep(key, new StepProgress { Status = "running", StartedAt = Now, Total = total });
        _progress = _progress with { CurrentStep = key };
        Flush();
    }

    public void Report(
        string key,
        int done,
        int? total = null,
        string? item = null,
        string? phase = null,
        int? failed = null,
        long? rows = null,
        bool force = false)
    {
        if (_store is null)
        {
            return;
        }

        var step = _progress.Steps.GetValueOrDefault(key) ?? new StepProgress { Status = "running", StartedAt = Now };
        // Todo cambio de elemento o fase se vuelca, así que lo que hay en memoria es lo último escrito.
        var changed = step.Item != item || step.Phase != phase;
        SetStep(key, step with
        {
            Done = done,
            Total = total ?? step.Total,
            Failed = failed ?? step.Failed,
            Rows = rows ?? step.Rows,
            Item = item,
            Phase = phase,
        });

        if (force || changed || Now - _lastFlush >= FlushInterval)
        {
            Flush();
        }
    }

    public void CompleteStep(string key, string status = "done")
    {
        if (_store is null)
        {
            return;
        }

        var step = _progress.Steps.GetValueOrDefault(key) ?? new StepProgress { StartedAt = Now };
        SetStep(key, step with { Status = status, FinishedAt = Now, Item = null, Phase = null });
        if (_progress.CurrentStep == key)
        {
            _progress = _progress with { CurrentStep = null };
        }

        Flush();
    }

    // Los pasos de esta etapa que no llegaron a empezar (salida temprana) quedan omitidos.
    public void SkipRemaining()
    {
        if (_store is null)
        {
            return;
        }

        foreach (var def in JobStepCatalog.ForStage(_stage))
        {
            if (_progress.Steps.GetValueOrDefault(def.Key) is { Status: "pending" } step)
            {
                SetStep(def.Key, step with { Status = "skipped" });
            }
        }
    }

    // Cierra la etapa: el paso que falló queda en error con su mensaje, y cualquier paso que
    // siguiera "running" se cierra (si no, mientras corre la etapa siguiente parecería vivo).
    public void Finish(string stageStatus, string? failedStep = null, string? message = null)
    {
        if (_store is null)
        {
            return;
        }

        if (failedStep is not null && stageStatus == "error"
            && _progress.Steps.GetValueOrDefault(failedStep) is { } failed)
        {
            SetStep(failedStep, failed with { Status = "error", FinishedAt = failed.FinishedAt ?? Now, Message = message });
        }

        foreach (var def in JobStepCatalog.ForStage(_stage))
        {
            if (_progress.Steps.GetValueOrDefault(def.Key) is { Status: "running" } step)
            {
                SetStep(def.Key, step with { Status = stageStatus, FinishedAt = Now, Item = null, Phase = null });
            }
        }

        _progress = _progress with { CurrentStep = null };
        Flush();
    }

    internal void Flush()
    {
        // Etapa cancelada (lease perdido, parada del worker): puede que otro worker ya sea el
        // dueño de progress.json. El estado que quede lo rehace su Begin.
        if (_store is null || _cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _lastFlush = Now;
        _progress = _progress with { UpdatedAt = _lastFlush };
        _store.Save(_pipelineId, _progress);
    }

    private DateTimeOffset Now => _store!.Clock.GetUtcNow();

    // El diccionario es solo de este escritor (lo creó Begin) y se serializa en el mismo hilo.
    private void SetStep(string key, StepProgress step) => _progress.Steps[key] = step;
}
