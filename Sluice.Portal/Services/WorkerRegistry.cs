using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Estado de los workers persistido en DATA_DIR/workers/{workerId}.json (mismo patrón de
// escritura atómica que el resto de stores, ver AtomicJsonFile) en vez de solo en memoria por
// proceso. En modo distribuido cada worker-* es un contenedor distinto: con un diccionario en
// memoria, el Dashboard servido por el contenedor 'api' nunca veía los heartbeats reales de los
// demás contenedores (cada uno tenía su propia instancia). El volumen DATA_DIR ya es compartido
// entre 'api' y los 'worker-*' (mismo patrón que usa TaskStore para la cola), así que basta con
// leer/escribir ahí en vez de mantener el estado solo en memoria.
public sealed class WorkerRegistry
{
    // Un worker vivo refresca la fecha de su archivo (Touch) aunque esté ocioso; si pasa este
    // margen sin hacerlo, se da por muerto (contenedor recreado con otro hostname, apagado...).
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly string _workersDir;

    public WorkerRegistry(IOptions<AppOptions> options)
    {
        _workersDir = Path.Combine(options.Value.DataDir, "workers");
    }

    public void RegisterHeartbeat(string workerId, PipelineStage stage, string? taskId, string? pipelineId, string? jobId) =>
        Save(new WorkerStatus
        {
            Id = workerId,
            Stage = stage,
            Status = taskId is null ? WorkerState.Idle : WorkerState.Busy,
            TaskId = taskId,
            PipelineId = pipelineId,
            JobId = jobId,
            Since = DateTimeOffset.UtcNow,
        });

    public void SetIdle(string workerId, PipelineStage stage) =>
        Save(new WorkerStatus
        {
            Id = workerId,
            Stage = stage,
            Status = WorkerState.Idle,
            Since = DateTimeOffset.UtcNow,
        });

    // Señal de vida sin cambiar el estado guardado (ocioso/ocupado, desde cuándo).
    public void Touch(string workerId)
    {
        var path = Path.Combine(_workersDir, $"{SafeFileName(workerId)}.json");
        try
        {
            if (File.Exists(path))
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
        }
        catch (IOException)
        {
            // Coincide con un rename atómico del propio worker: el siguiente Touch lo refresca.
        }
    }

    // Margen para dar por caído a un worker en el Panel, mucho antes de la purga (StaleAfter):
    // ocioso hace Touch cada 30 s y, con tarea, en cada latido (lease/3, ver
    // WorkerHostService.HeartbeatLoopAsync). Tres señales perdidas = caído: 90 s, o el lease
    // entero si se configuró uno largo (su latido es más lento).
    public static TimeSpan AliveWithin(int taskLeaseSeconds) =>
        TimeSpan.FromSeconds(Math.Max(90, taskLeaseSeconds));

    public IReadOnlyList<WorkerStatus> ListAll() => List(aliveWithin: null);

    // Solo los workers con señal de vida dentro de aliveWithin. Los demás no se borran: eso lo
    // sigue haciendo la purga de ListAll a los StaleAfter.
    public IReadOnlyList<WorkerStatus> ListAlive(TimeSpan aliveWithin) => List(aliveWithin);

    private IReadOnlyList<WorkerStatus> List(TimeSpan? aliveWithin)
    {
        if (!Directory.Exists(_workersDir))
        {
            return [];
        }

        var now = DateTime.UtcNow;
        var staleBefore = now - StaleAfter;
        var result = new List<WorkerStatus>();
        foreach (var file in Directory.GetFiles(_workersDir, "*.json"))
        {
            var lastSignal = File.GetLastWriteTimeUtc(file);

            // Sin señal de vida reciente: worker muerto. Se borra para no mostrarlo para siempre.
            if (lastSignal < staleBefore)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Otro proceso lo está tocando: se reintenta en el siguiente listado.
                }

                continue;
            }

            if (aliveWithin is { } within && lastSignal < now - within)
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(file);
                var status = System.Text.Json.JsonSerializer.Deserialize<WorkerStatus>(stream, JsonDefaults.Options);
                if (status is not null)
                {
                    result.Add(status);
                }
            }
            catch
            {
                // Archivo corrupto o a mitad del rename atómico: se ignora este worker en
                // este poll puntual, sin tumbar el listado del resto.
            }
        }

        return result.OrderBy(w => w.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Save(WorkerStatus status)
    {
        Directory.CreateDirectory(_workersDir);
        AtomicJsonFile.Write(Path.Combine(_workersDir, $"{SafeFileName(status.Id)}.json"), status);
    }

    // El workerId es el hostname del contenedor (Environment.MachineName, ver
    // WorkerHostService); se sanea de todas formas por si alguna vez llega uno con
    // caracteres no válidos para nombre de archivo.
    private static string SafeFileName(string workerId) =>
        string.Concat(workerId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
