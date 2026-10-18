using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Stores;

// Una ejecución pedida mientras el pipeline estaba ocupado: el scheduler la lanza en su vuelta
// cuando el pipeline queda libre (también si el job en curso falla o se cancela).
public sealed record QueuedRun(JobType Type, DateTimeOffset RequestedAt);

// Un hueco por pipeline, en {pipeline}/state/queued_run.json junto al resto de su estado. No es un
// job: el job se crea al arrancar, así que no cuenta como activo ni lo ve el vigilante de huérfanos.
public sealed class PipelineRunQueue
{
    // Las acciones del menú de ejecución. Restaurar y reintentar deciden con el estado del momento en
    // que se confirman, así que no se encolan.
    public static readonly IReadOnlySet<JobType> QueueableTypes =
        new HashSet<JobType> { JobType.Incremental, JobType.Force, JobType.Reconcile, JobType.Diff };

    private readonly AppOptions _options;

    public PipelineRunQueue(IOptions<AppOptions> options) => _options = options.Value;

    public QueuedRun? Get(string pipelineId)
    {
        if (!EntityIds.IsValid(pipelineId))
        {
            return null;
        }

        var path = GetPath(pipelineId);
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<QueuedRun>(stream, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            // Solo editándolo a mano (se escribe atómicamente): se descarta, o el scheduler fallaría
            // en cada poll y el hueco quedaría ocupado para siempre.
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    // Ocupa el hueco solo si está libre (creación atómica): con él ocupado devuelve lo que ya había,
    // para no sustituir una petición sin que nadie lo sepa. Si el scheduler lo vacía entre medias,
    // se vuelve a intentar.
    public QueuedRun? TryEnqueue(string pipelineId, QueuedRun run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GetPath(pipelineId))!);
        while (!AtomicJsonFile.WriteNew(GetPath(pipelineId), run))
        {
            if (Get(pipelineId) is { } queued)
            {
                return queued;
            }
        }

        return null;
    }

    public bool Remove(string pipelineId)
    {
        if (!EntityIds.IsValid(pipelineId))
        {
            return false;
        }

        var path = GetPath(pipelineId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !File.Exists(path);
        }
    }

    private string GetPath(string pipelineId) =>
        EntityIds.IsValid(pipelineId)
            ? Path.Combine(_options.DataDir, pipelineId, "state", "queued_run.json")
            : throw new ArgumentException($"Id no válido: '{pipelineId}'.", nameof(pipelineId));
}
