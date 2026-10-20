using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;

namespace Sluice.Portal.Services.Stores;

// Una ejecución pedida mientras el pipeline estaba ocupado: el scheduler la lanza en su vuelta
// cuando el pipeline queda libre (también si el job en curso falla o se cancela). RequestedBy: null
// en peticiones anteriores a la auditoría.
public sealed record QueuedRun(JobType Type, DateTimeOffset RequestedAt, AuditActor? RequestedBy = null);

// Un hueco por pipeline, en {pipeline}/state/queued_run.json junto al resto de su estado. No es un
// job: el job se crea al arrancar, así que no cuenta como activo ni lo ve el vigilante de huérfanos.
// Para lanzarla, el scheduler la reserva antes (rename a queued_run.starting.json): desde ese momento
// ya no se puede quitar, así que "Quitar de la cola" nunca dice que la quitó si va a arrancar. Una
// reserva que una vuelta caída no terminó se retoma en la siguiente, para no perder la petición.
public sealed class PipelineRunQueue
{
    // Las acciones del menú de ejecución. Restaurar y reintentar deciden con el estado del momento en
    // que se confirman, así que no se encolan.
    public static readonly IReadOnlySet<JobType> QueueableTypes =
        new HashSet<JobType> { JobType.Incremental, JobType.Force, JobType.Reconcile, JobType.Diff };

    private readonly AppOptions _options;

    public PipelineRunQueue(IOptions<AppOptions> options) => _options = options.Value;

    // Lo que ocupa el hueco: en cola o ya reservado para arrancar.
    public QueuedRun? Get(string pipelineId)
    {
        if (!EntityIds.IsValid(pipelineId))
        {
            return null;
        }

        return Read(GetPath(pipelineId)) ?? Read(GetStartingPath(pipelineId));
    }

    // Ocupa el hueco solo si está libre (creación atómica): con él ocupado devuelve lo que ya había,
    // para no sustituir una petición sin que nadie lo sepa. Si el scheduler lo vacía entre medias,
    // se vuelve a intentar.
    public QueuedRun? TryEnqueue(string pipelineId, QueuedRun run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GetPath(pipelineId))!);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Get(pipelineId) is { } queued)
            {
                return queued;
            }

            if (AtomicJsonFile.WriteNew(GetPath(pipelineId), run))
            {
                return null;
            }
        }

        throw new InvalidOperationException("La cola del pipeline está ocupada; inténtalo de nuevo.");
    }

    // Solo lo que sigue en cola: lo reservado para arrancar ya no se puede quitar.
    public bool Remove(string pipelineId)
    {
        if (!EntityIds.IsValid(pipelineId))
        {
            return false;
        }

        var removed = $"{GetPath(pipelineId)}.{Guid.NewGuid():N}.removed";
        try
        {
            File.Move(GetPath(pipelineId), removed, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Delete(removed);
        return true;
    }

    // Reserva lo encolado para arrancarlo (null si ya no hay nada: lo quitaron entre medias). Una
    // reserva previa que no se terminó (el proceso cayó al lanzarla) se devuelve tal cual.
    public QueuedRun? TryClaim(string pipelineId)
    {
        var starting = GetStartingPath(pipelineId);
        if (Read(starting) is { } claimed)
        {
            return claimed;
        }

        try
        {
            File.Move(GetPath(pipelineId), starting, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Read(starting);
    }

    // El arranque se rechazó (un job cancelado aún se detiene): vuelve a la cola. Si entretanto se
    // encoló otra petición, manda la nueva.
    public void Release(string pipelineId)
    {
        try
        {
            File.Move(GetStartingPath(pipelineId), GetPath(pipelineId), overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Delete(GetStartingPath(pipelineId));
        }
    }

    // Arrancó (o no puede arrancar): la reserva se descarta.
    public void Complete(string pipelineId) => Delete(GetStartingPath(pipelineId));

    private static QueuedRun? Read(string path)
    {
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
            Delete(path);
            return null;
        }
    }

    private static bool Delete(string path)
    {
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

    private string GetStartingPath(string pipelineId) =>
        Path.ChangeExtension(GetPath(pipelineId), ".starting.json");
}
