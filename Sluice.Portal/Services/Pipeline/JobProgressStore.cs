using System.Text.Json;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Pipeline;

// progress.json de cada job (DATA_DIR/{pipelineId}/runs/{jobId}/): progreso fino por paso para
// la UI. Es un fichero lateral y no un campo del Job porque se escribe a menudo y los executors
// guardan el Job con copias completas que lo pisarían. Un solo escritor: el worker que tiene
// reclamada la etapa en curso. Al borrar el job, RetentionService ya borra runs/{jobId}.
public sealed class JobProgressStore
{
    private readonly IngestManifestStore _manifests;

    public JobProgressStore(IngestManifestStore manifests, TimeProvider? clock = null)
    {
        _manifests = manifests;
        Clock = clock ?? TimeProvider.System;
    }

    internal TimeProvider Clock { get; }

    public string GetPath(string pipelineId, string jobId) =>
        Path.Combine(_manifests.GetRunDirectory(pipelineId, jobId), "progress.json");

    // null si no existe o no se puede leer (jobs viejos, fakes, escritura a medias de otra
    // réplica): la UI degrada a los estados de etapa del Job.
    public JobProgress? Load(string pipelineId, string jobId)
    {
        try
        {
            // FileShare.Delete: el escritor reemplaza el fichero con un rename atómico mientras la
            // UI lo puede tener abierto; sin él, el rename fallaría en Windows.
            using var stream = new FileStream(
                GetPath(pipelineId, jobId), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<JobProgress>(stream, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Best-effort: un fallo al escribir el progreso nunca debe tumbar la etapa.
    public void Save(string pipelineId, JobProgress progress)
    {
        try
        {
            var path = GetPath(pipelineId, progress.JobId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicJsonFile.Write(path, progress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Arranca el escritor de una etapa: conserva los pasos de etapas anteriores y deja los de
    // esta en pending (un reintento de la etapa empieza de cero). cancellationToken es el de la
    // etapa: al perder el lease el worker lo cancela y este escritor deja de volcar, para no
    // pisar el progress.json del worker que reclamó la tarea.
    public StageProgress Begin(string pipelineId, string jobId, string stage, CancellationToken cancellationToken = default)
    {
        var previous = Load(pipelineId, jobId);
        var steps = new Dictionary<string, StepProgress>();
        if (previous is not null)
        {
            foreach (var (key, step) in previous.Steps)
            {
                if (JobStepCatalog.Find(key)?.Stage != stage)
                {
                    steps[key] = step;
                }
            }
        }

        foreach (var def in JobStepCatalog.ForStage(stage))
        {
            steps[def.Key] = new StepProgress();
        }

        var progress = new StageProgress(this, pipelineId, stage, new JobProgress
        {
            JobId = jobId,
            Stage = stage,
            Steps = steps,
        }, cancellationToken);
        progress.Flush();
        return progress;
    }
}
