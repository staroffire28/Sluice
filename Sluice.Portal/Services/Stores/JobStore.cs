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
    private readonly object _listLock = new();
    // Ids que Save/Delete invalidaron mientras un ListIndex lee los ficheros: cubre los jobs que aún
    // no están en _index, que Forget no tiene dónde marcar. Null fuera de esa ventana, para que no
    // crezca en un proceso que guarda jobs sin listarlos nunca (un worker de SQL).
    private HashSet<string>? _forgotten;

    public JobStore(IOptions<AppOptions> options, ILogger<JobStore>? logger = null) : base(options, "jobs", logger)
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
    //
    // La lectura de los ficheros va fuera de _indexLock: en frío (tras arrancar, con decenas de miles
    // de jobs) son segundos, y Save/Delete toman ese lock para invalidar su entrada. _listLock solo
    // serializa los listados entre sí, para que dos en frío no lean todo dos veces.
    public IReadOnlyList<JobIndexEntry> ListIndex()
    {
        lock (_listLock)
        {
            var directory = new DirectoryInfo(GetDirectory());
            if (!directory.Exists)
            {
                return [];
            }

            // La fecha y el tamaño se toman aquí, fuera de _indexLock: en Linux el FileInfo de la
            // enumeración no los trae y su primer acceso es un stat por fichero.
            var files = directory.EnumerateFiles("*.json")
                .Select(file => (Id: Path.GetFileNameWithoutExtension(file.Name), Path: file.FullName, WriteTime: file.LastWriteTimeUtc, Length: file.Length))
                .ToList();
            var seen = files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            List<(string Id, string Path, DateTime WriteTime, long Length)> stale;
            lock (_indexLock)
            {
                _forgotten = new HashSet<string>(StringComparer.Ordinal);
                stale = files.Where(f => !_index.TryGetValue(f.Id, out var cached)
                    || cached.WriteTime != f.WriteTime || cached.Length != f.Length).ToList();
            }

            // Se lee el Job entero (no solo los campos del índice): su deserialización es la que valida
            // el fichero, y un job ilegible como Job no debe entrar en los listados.
            var read = new List<(string Id, DateTime WriteTime, long Length, JobIndexEntry? Entry)>(stale.Count);
            foreach (var (id, path, writeTime, length) in stale)
            {
                Job? job;
                try
                {
                    job = Read(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Abierto o a medio mover por otro proceso: se queda lo último leído, sin tocar la
                    // caché, y se relee en la siguiente consulta. Quitarlo hacía que un job en curso
                    // desapareciera de ListRunning y de las guardas por un error transitorio.
                    continue;
                }
                catch (System.Text.Json.JsonException ex)
                {
                    // Corrupto: se omite, como en ListAll.
                    WarnUnreadable(path, ex);
                    job = null;
                }

                // Solo se guarda la entrada: retener los Job enteros hasta el volcado serían GB en frío.
                read.Add((id, writeTime, length, job is null ? null
                    : new JobIndexEntry(job.Id, job.PipelineId, job.Type, job.Status, job.StartedAt, job.FinishedAt)));
            }

            List<JobIndexEntry> entries;
            lock (_indexLock)
            {
                var forgotten = _forgotten!;
                _forgotten = null;
                foreach (var (id, writeTime, length, entry) in read)
                {
                    if (entry is null)
                    {
                        _index.Remove(id);
                        continue;
                    }

                    // Si este proceso lo guardó mientras se leía, lo leído puede ser anterior a esa
                    // escritura con la misma fecha y tamaño: se devuelve, pero queda invalidado.
                    _index[id] = (forgotten.Contains(id) ? default : writeTime, length, entry);
                }

                foreach (var gone in _index.Keys.Where(id => !seen.Contains(id)).ToList())
                {
                    _index.Remove(gone);
                }

                entries = _index.Values.Select(v => v.Entry).ToList();
            }

            return entries.OrderByDescending(e => e.StartedAt).ToList();
        }
    }

    public Job? GetById(string id) => Get(id);

    // La fecha y las filas por tabla de un job, sin materializar sus eventos ni el resto de campos
    // (la analítica de una tabla). Como GetById, bajo el lock del almacén (no se cruza con un Save del
    // mismo job en este proceso), y un id no válido o un fichero que no existe dan null.
    public JobTableRows? GetTableRows(string id) =>
        ReadLocked(id, path =>
        {
            using var stream = File.OpenRead(path);
            return System.Text.Json.JsonSerializer.Deserialize<JobTableRows>(stream, JsonDefaults.Options);
        });

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
            _forgotten?.Add(id);
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

// Lo que la analítica de una tabla lee de un job: su fecha y las filas de cada tabla, sin los eventos.
public sealed record JobTableRows
{
    public DateTimeOffset StartedAt { get; init; }
    public List<JobTableRowsEntry> Entities { get; init; } = [];
}

public sealed record JobTableRowsEntry
{
    public string Entity { get; init; } = "";
    public int CsvRows { get; init; }
    public int? SqlRows { get; init; }
}
