using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Stores;

public sealed class JobStore : JsonFileStore<Job>
{
    // Serializa el ciclo leer+mutar+guardar de MutateAtomic/SaveWithVersionCheck dentro del
    // proceso; el FileLock por job (LockJob) lo extiende a otros procesos que comparten
    // DATA_DIR (modo distribuido: el api cancela y los workers actualizan el mismo job).
    private readonly object _writeLock = new();
    private readonly string _locksDir;
    private readonly object _indexLock = new();
    private readonly Dictionary<string, (DateTime WriteTime, long Length, JobIndexEntry Entry)> _index = new(StringComparer.Ordinal);

    public JobStore(IOptions<AppOptions> options) : base(options, "jobs")
    {
        _locksDir = Path.Combine(options.Value.DataDir, "locks");
    }

    private IDisposable LockJob(string id) =>
        FileLock.Acquire(Path.Combine(_locksDir, $"job-{id}.lock"), TimeSpan.FromSeconds(10));

    public IReadOnlyList<Job> ListAll() =>
        ListAll(Read).OrderByDescending(j => j.StartedAt).ToList();

    // Índice de los campos con que se filtran los jobs, del más reciente al más antiguo. Listar todos
    // los job.json (cada uno con todos sus eventos y tablas) costaba segundos con unos miles de jobs,
    // y el Dashboard, el scheduler y las guardas lo hacían en cada consulta. El índice solo vuelve a
    // leer los ficheros cuya fecha o tamaño cambió, así que también ve lo que escriben otros procesos
    // (los workers) sobre el mismo DATA_DIR. Quien necesite el job completo lo pide con GetById.
    public IReadOnlyList<JobIndexEntry> ListIndex()
    {
        lock (_indexLock)
        {
            var directory = new DirectoryInfo(GetDirectory());
            if (!directory.Exists)
            {
                return [];
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in directory.EnumerateFiles("*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file.Name);
                seen.Add(id);
                if (_index.TryGetValue(id, out var cached) && cached.WriteTime == file.LastWriteTimeUtc && cached.Length == file.Length)
                {
                    continue;
                }

                Job? job;
                try
                {
                    job = Read(file.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Abierto o a medio mover por otro proceso: se queda lo último leído, sin tocar la
                    // caché, y se relee en la siguiente consulta. Quitarlo hacía que un job en curso
                    // desapareciera de ListRunning y de las guardas por un error transitorio.
                    continue;
                }
                catch (System.Text.Json.JsonException)
                {
                    // Corrupto: se omite, como en ListAll.
                    job = null;
                }

                if (job is null)
                {
                    _index.Remove(id);
                    continue;
                }

                _index[id] = (file.LastWriteTimeUtc, file.Length,
                    new JobIndexEntry(job.Id, job.PipelineId, job.Type, job.Status, job.StartedAt, job.FinishedAt));
            }

            foreach (var gone in _index.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _index.Remove(gone);
            }

            return _index.Values.Select(v => v.Entry).OrderByDescending(e => e.StartedAt).ToList();
        }
    }

    public Job? GetById(string id) => Get(id);

    // Una escritura de este proceso invalida su entrada: la fecha del fichero podría no cambiar si dos
    // escrituras caen en el mismo instante.
    public void Save(Job job)
    {
        Save(job.Id, job);
        Forget(job.Id);
    }

    public new bool Delete(string id)
    {
        var deleted = base.Delete(id);
        Forget(id);
        return deleted;
    }

    private void Forget(string id)
    {
        lock (_indexLock)
        {
            // Se invalida sin quitarla: ListIndex la relee, y si no puede leerla en ese momento se
            // queda con la última. Un fichero borrado sale por el barrido de los que ya no están.
            if (_index.TryGetValue(id, out var cached))
            {
                _index[id] = cached with { WriteTime = default };
            }
        }
    }

    // Lee, aplica la mutación y guarda bajo un único lock: ninguna otra llamada a
    // MutateAtomic/SaveWithVersionCheck puede intercalarse a mitad del ciclo. Usado por
    // AppendEvent, que solo necesita agregar un elemento (no reemplazar el documento entero).
    public Job? MutateAtomic(string id, Func<Job, Job> mutate)
    {
        lock (_writeLock)
        using (LockJob(id))
        {
            var current = GetById(id);
            if (current is null)
            {
                return null;
            }

            var updated = mutate(current) with { Id = id, Version = current.Version + 1 };
            Save(updated);
            return updated;
        }
    }

    // Para Update: el caller ya trae un Job completo (construido vía "job with {...}" a partir
    // de una lectura anterior, posiblemente obsoleta). Se guarda igual (mismo comportamiento de
    // siempre, no se aborta el pipeline) pero bajo el mismo lock que MutateAtomic, y se informa
    // si la versión que traía el caller ya no coincidía con la de disco. Dos campos no se toman
    // de la copia del caller: los eventos (solo los añade AppendEvent, así que valen los de
    // disco) y un estado Cancelado (terminal: una copia obsoleta en curso no lo revive).
    public Job SaveWithVersionCheck(Job job, out bool versionConflict, out int previousVersion, out bool cancellationKept)
    {
        lock (_writeLock)
        using (LockJob(job.Id))
        {
            var current = GetById(job.Id);
            previousVersion = current?.Version ?? 0;
            versionConflict = current is not null && current.Version != job.Version;
            cancellationKept = current?.Status == JobStatus.Cancelled && job.Status != JobStatus.Cancelled;
            if (cancellationKept)
            {
                return current!;
            }

            var updated = job with { Version = previousVersion + 1, Events = current?.Events ?? job.Events };
            Save(updated);
            return updated;
        }
    }
}

// Lo que necesitan los listados para filtrar y ordenar, sin los eventos ni las tablas del job.
public sealed record JobIndexEntry(string Id, string PipelineId, JobType Type, JobStatus Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
