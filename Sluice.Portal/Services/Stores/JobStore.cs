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

    public JobStore(IOptions<AppOptions> options) : base(options, "jobs")
    {
        _locksDir = Path.Combine(options.Value.DataDir, "locks");
    }

    private IDisposable LockJob(string id) =>
        FileLock.Acquire(Path.Combine(_locksDir, $"job-{id}.lock"), TimeSpan.FromSeconds(10));

    public IReadOnlyList<Job> ListAll() =>
        ListAll(Read).OrderByDescending(j => j.StartedAt).ToList();

    public Job? GetById(string id) => Get(id);

    public void Save(Job job) => Save(job.Id, job);

    public new bool Delete(string id) => base.Delete(id);

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