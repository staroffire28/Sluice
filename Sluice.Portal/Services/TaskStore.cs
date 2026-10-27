using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public sealed class TaskStore : ITaskStore
{
    private readonly AppOptions _options;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly string _locksDir;
    private readonly ILogger<TaskStore> _logger;

    public TaskStore(IOptions<AppOptions> options, SettingsStore settings, TimeProvider? time = null, ILogger<TaskStore>? logger = null)
    {
        _options = options.Value;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<TaskStore>.Instance;
        _locksDir = Path.Combine(_options.DataDir, "locks");
        DataDirectoryInitializer.EnsureLayout(_options);
    }

    // Claim y reclaim de una misma tarea se serializan entre procesos. En Linux File.Move va por
    // ruta: un reclaim que había comprobado un claimed/X ya movido podía mover el claimed/X de un
    // claim posterior (mismo nombre, otro fichero) y la tarea acababa reclamada dos veces.
    // null = otro proceso la tiene ocupada: se trata como carrera perdida.
    private async Task<IDisposable?> TryLockTaskAsync(PipelineStage stage, string taskId, CancellationToken cancellationToken)
    {
        try
        {
            return await FileLock.AcquireAsync(Path.Combine(_locksDir, $"task-{stage}-{taskId}.lock"), TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async Task EnqueueAsync(PipelineTask task, CancellationToken cancellationToken = default)
    {
        var path = GetPath(task.Stage, PipelineTaskStatus.Pending, task.Id);
        await WriteTaskAsync(path, task with { Status = PipelineTaskStatus.Pending }, cancellationToken);
    }

    public async Task<PipelineTask?> TryClaimAsync(PipelineStage stage, string workerId, CancellationToken cancellationToken = default)
    {
        await ReclaimExpiredLeasesAsync(cancellationToken);

        var pendingDir = GetDirectory(stage, PipelineTaskStatus.Pending);
        if (!Directory.Exists(pendingDir))
        {
            return null;
        }

        foreach (var file in Directory.GetFiles(pendingDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var taskId = Path.GetFileNameWithoutExtension(file);
            var claimedPath = GetPath(stage, PipelineTaskStatus.Claimed, taskId);

            // Hasta escribir el lease nuevo: un reclaim no puede colarse entre el move y la escritura.
            using var taskLock = await TryLockTaskAsync(stage, taskId, cancellationToken);
            if (taskLock is null || !File.Exists(file))
            {
                continue;
            }

            // En Windows, File.Move abre el origen y renombra por handle: si otra réplica lo
            // movió entre medias, el segundo "renombra" claimed/X a sí mismo y también gana.
            // Mantener abierto el pendiente con FileShare.Delete durante lectura + move lo
            // evita: el open de otro claimer pide Read (no compartido) y falla, mientras que
            // nuestro propio move (pide Delete) sí está permitido. En Linux rename ya es
            // atómico por ruta y esto no cambia nada.
            PipelineTask? task;
            try
            {
                await using (var pending = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete))
                {
                    task = await JsonSerializer.DeserializeAsync<PipelineTask>(pending, JsonDefaults.Options, cancellationToken);
                    // Un pendiente que aún lleva lease bajo el lock es un reclaim que cayó entre
                    // moverlo y limpiarlo (uno en curso tiene el lock hasta escribir): se reclama
                    // como cualquier otro, el lease nuevo sustituye al vencido.
                    if (task is null)
                    {
                        continue;
                    }

                    File.Move(file, claimedPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Otra réplica reclamó este archivo a la vez, o lock transitorio de Windows:
                // perdimos la carrera, seguir con el siguiente pendiente.
                continue;
            }
            catch (JsonException ex)
            {
                // Ya bajo el lock de la tarea.
                Quarantine(file, stage, ex);
                continue;
            }

            var now = _time.GetUtcNow();
            var claimed = task with
            {
                Status = PipelineTaskStatus.Claimed,
                ClaimedBy = workerId,
                ClaimedAt = now,
                LeaseExpiresAt = now.AddSeconds(Math.Max(SettingsStore.MinTaskLeaseSeconds, _settings.Get().TaskLeaseSeconds)),
            };

            await WriteTaskAsync(claimedPath, claimed, cancellationToken);
            return claimed;
        }

        return null;
    }

    public async Task<PipelineTask?> GetAsync(string taskId, PipelineStage stage, CancellationToken cancellationToken = default)
    {
        foreach (PipelineTaskStatus status in Enum.GetValues<PipelineTaskStatus>())
        {
            var path = GetPath(stage, status, taskId);
            if (!File.Exists(path))
            {
                continue;
            }

            return await ReadTaskAsync(path, cancellationToken);
        }

        return null;
    }

    public async Task<PipelineTask?> HeartbeatAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default)
    {
        // Bajo el lock de la tarea, como el reclaim: si este ya la devolvió a pending, reescribir
        // claimed/X la dejaría a la vez en pending/ y en claimed/, y se ejecutaría dos veces. Con el
        // lock ocupado se lanza (el bucle del latido lo reintenta); null diría "ya no es tuya".
        using var taskLock = await TryLockTaskAsync(stage, taskId, cancellationToken)
            ?? throw new IOException($"La tarea {taskId} está ocupada por otro proceso.");
        var path = GetPath(stage, PipelineTaskStatus.Claimed, taskId);
        if (!File.Exists(path))
        {
            return null;
        }

        var task = await ReadTaskAsync(path, cancellationToken);
        if (task is null || task.ClaimedBy != workerId)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var updated = task with
        {
            LeaseExpiresAt = now.AddSeconds(Math.Max(SettingsStore.MinTaskLeaseSeconds, _settings.Get().TaskLeaseSeconds)),
        };

        await WriteTaskAsync(path, updated, cancellationToken);
        return updated;
    }

    public async Task<PipelineTask?> CompleteAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default)
    {
        return await MoveClaimedAsync(taskId, stage, workerId, PipelineTaskStatus.Done, task => task with
        {
            Status = PipelineTaskStatus.Done,
            FinishedAt = _time.GetUtcNow(),
            ErrorMessage = null,
        }, cancellationToken);
    }

    public async Task<PipelineTask?> FailAsync(string taskId, PipelineStage stage, string workerId, string error, CancellationToken cancellationToken = default)
    {
        return await MoveClaimedAsync(taskId, stage, workerId, PipelineTaskStatus.Error, task => task with
        {
            Status = PipelineTaskStatus.Error,
            FinishedAt = _time.GetUtcNow(),
            ErrorMessage = error,
        }, cancellationToken);
    }

    public async Task ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();

        foreach (PipelineStage stage in Enum.GetValues<PipelineStage>())
        {
            var claimedDir = GetDirectory(stage, PipelineTaskStatus.Claimed);
            if (!Directory.Exists(claimedDir))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(claimedDir, "*.json"))
            {
                PipelineTask? task;
                try
                {
                    task = await ReadTaskAsync(file, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Carrera con otra réplica: el archivo se está moviendo/reescribiendo
                    // (claim, heartbeat o complete en curso). Esa tarea ya la maneja otro
                    // worker; saltarla en vez de romper el claim. (FileNotFoundException y
                    // DirectoryNotFoundException heredan de IOException.)
                    continue;
                }
                catch (JsonException ex)
                {
                    await QuarantineAsync(file, stage, ex, cancellationToken);
                    continue;
                }

                if (!IsExpired(task, file, now))
                {
                    continue;
                }

                var taskId = Path.GetFileNameWithoutExtension(file);
                var pendingPath = GetPath(stage, PipelineTaskStatus.Pending, taskId);

                // Bajo el lock de la tarea, el claimed/X que se comprueba es el que se mueve.
                using var taskLock = await TryLockTaskAsync(stage, taskId, cancellationToken);
                if (taskLock is null)
                {
                    continue;
                }

                // Mismo riesgo que en TryClaimAsync: en Windows dos réplicas pueden "ganar" el
                // mismo File.Move. Solo las tareas vencidas se bloquean (FileShare.Delete), para
                // no chocar con heartbeat/complete de tareas activas; bajo el handle se confirma
                // que el lease sigue vencido (pudo renovarse) y se mueve.
                PipelineTask? current;
                try
                {
                    await using (var expired = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete))
                    {
                        current = await JsonSerializer.DeserializeAsync<PipelineTask>(expired, JsonDefaults.Options, cancellationToken);
                        if (!IsExpired(current, file, now))
                        {
                            continue;
                        }

                        File.Move(file, pendingPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                // Solo llega aquí quien ganó el move. Un pendiente no debe llevar lease; si esta
                // limpieza no llega a escribirse, TryClaimAsync lo reclama igualmente.
                var reclaimed = current with
                {
                    Status = PipelineTaskStatus.Pending,
                    ClaimedBy = null,
                    ClaimedAt = null,
                    LeaseExpiresAt = null,
                };

                await WriteTaskAsync(pendingPath, reclaimed, cancellationToken);
            }
        }
    }

    // Vencida: con el lease pasado, o sin lease y escrita hace más de un lease. Esta última es un claim
    // que cayó entre mover el pendiente a claimed/ y escribir su lease: nadie la renovaba ni la
    // reclamaba. Un claim en curso tiene el lock de la tarea, así que bajo el lock ya lleva lease.
    private bool IsExpired([NotNullWhen(true)] PipelineTask? task, string file, DateTimeOffset now) =>
        task is not null && (task.LeaseExpiresAt is { } lease
            ? lease <= now
            : new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)
                .AddSeconds(Math.Max(SettingsStore.MinTaskLeaseSeconds, _settings.Get().TaskLeaseSeconds)) <= now);

    public int CountByStatus(PipelineStage stage, PipelineTaskStatus status)
    {
        var directory = GetDirectory(stage, status);
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0;
    }

    public async Task<int> CancelJobTasksAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        // Solo Pending: la Claimed la detiene y libera su worker (ver ITaskStore), y las Done/Error
        // ya son terminales.
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            var directory = GetDirectory(stage, PipelineTaskStatus.Pending);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                PipelineTask? task;
                try
                {
                    task = await ReadTaskAsync(file, cancellationToken);
                }
                catch
                {
                    continue;
                }

                if (task?.JobId != jobId)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Otra réplica la reclamó/movió a la vez: la verá el guard del router
                    // (job Cancelled) y no avanzará. No es fatal para la cancelación.
                }
            }
        }

        return removed;
    }

    public async Task<PipelineTask?> FindLiveClaimAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            var directory = GetDirectory(stage, PipelineTaskStatus.Claimed);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, "*.json"))
            {
                PipelineTask? task;
                try
                {
                    task = await ReadTaskAsync(file, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Se completó, se reclamó o se está recuperando (lease vencido) entre el listado
                    // y la lectura: ya no está en curso.
                    continue;
                }
                catch (JsonException ex)
                {
                    await QuarantineAsync(file, stage, ex, cancellationToken);
                    continue;
                }

                if (task?.PipelineId == pipelineId && task.LeaseExpiresAt > now)
                {
                    return task;
                }
            }
        }

        return null;
    }

    private async Task<PipelineTask?> MoveClaimedAsync(
        string taskId,
        PipelineStage stage,
        string workerId,
        PipelineTaskStatus targetStatus,
        Func<PipelineTask, PipelineTask> transform,
        CancellationToken cancellationToken)
    {
        // Bajo el lock de la tarea, como el latido y el reclaim: sin él, completar podía mover el
        // claimed/X que un reclaim estaba devolviendo a pending, y la tarea quedaba en dos sitios. Con
        // el lock ocupado se lanza (como en HeartbeatAsync); null diría "ya no es tuya".
        using var taskLock = await TryLockTaskAsync(stage, taskId, cancellationToken)
            ?? throw new IOException($"La tarea {taskId} está ocupada por otro proceso.");
        var claimedPath = GetPath(stage, PipelineTaskStatus.Claimed, taskId);
        if (!File.Exists(claimedPath))
        {
            return null;
        }

        var task = await ReadTaskAsync(claimedPath, cancellationToken);
        if (task is null || task.ClaimedBy != workerId)
        {
            return null;
        }

        var finalTask = transform(task);
        var targetPath = GetPath(stage, targetStatus, taskId);

        try
        {
            File.Move(claimedPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        await WriteTaskAsync(targetPath, finalTask, cancellationToken);
        return finalTask;
    }

    // Una tarea ilegible (JSON corrupto, p. ej. editada a mano) hacía lanzar al reclaim de todas las
    // etapas y a FindLiveClaimAsync, la guarda de Run, Reset y Restore de cualquier pipeline. Se aparta
    // a error/ con otro nombre (GetAsync no la toma por la tarea, y no pisa otra cuarentena) y se avisa.
    private async Task QuarantineAsync(string file, PipelineStage stage, JsonException error, CancellationToken cancellationToken)
    {
        using var taskLock = await TryLockTaskAsync(stage, Path.GetFileNameWithoutExtension(file), cancellationToken);
        if (taskLock is not null)
        {
            Quarantine(file, stage, error);
        }
    }

    // Quien llama ya tiene el lock de la tarea.
    private void Quarantine(string file, PipelineStage stage, JsonException error)
    {
        var target = Path.Combine(
            GetDirectory(stage, PipelineTaskStatus.Error),
            $"{Path.GetFileNameWithoutExtension(file)}.unreadable-{_time.GetUtcNow():yyyyMMddHHmmssfff}.json");
        try
        {
            File.Move(file, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Otra réplica la apartó a la vez.
            return;
        }

        _logger.LogError(error, "Tarea ilegible {File}: apartada en {Target}; revísala y bórrala o reencólala a mano.", file, target);
    }

    private string GetDirectory(PipelineStage stage, PipelineTaskStatus status) =>
        Path.Combine(_options.DataDir, "tasks", stage.ToString().ToLowerInvariant(), status.ToString().ToLowerInvariant());

    private string GetPath(PipelineStage stage, PipelineTaskStatus status, string taskId) =>
        Path.Combine(GetDirectory(stage, status), $"{taskId}.json");

    private static async Task WriteTaskAsync(string path, PipelineTask task, CancellationToken cancellationToken)
    {
        // Escritura atómica: serializar a .tmp y renombrar. Bajo réplicas, otro
        // worker puede estar leyendo este archivo (reclaim); un File.Create directo
        // chocaría. El move es atómico y el lector nunca ve contenido parcial.
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, task, JsonDefaults.Options, cancellationToken);
        }

        await MoveWithRetryAsync(tmp, path, cancellationToken);
    }

    // File.Move(overwrite) puede lanzar IOException/UnauthorizedAccessException transitorios en
    // Windows cuando otra réplica tiene el destino abierto (lectura concurrente durante reclaim).
    // Se reintenta con backoff corto antes de rendirse, en vez de propagar y tumbar el worker.
    private static async Task MoveWithRetryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static async Task<PipelineTask?> ReadTaskAsync(string path, CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite: no bloquear a un escritor concurrente (otra réplica
        // renombrando su .tmp encima). Combinado con la escritura atómica de arriba,
        // siempre se lee un JSON completo (el inode viejo o el nuevo, nunca a medias).
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<PipelineTask>(stream, JsonDefaults.Options, cancellationToken);
    }
}