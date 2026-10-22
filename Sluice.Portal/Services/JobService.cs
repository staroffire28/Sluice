using Microsoft.Extensions.Logging.Abstractions;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public interface IJobService
{
    // range: filtro por fecha de inicio (Desde/Hasta, días completos) de la página Jobs.
    IReadOnlyList<Job> List(int page = 1, int pageSize = 20, string? pipelineId = null, JobStatus? status = null, DateRange? range = null);
    int Count(string? pipelineId = null, JobStatus? status = null, DateRange? range = null);
    IReadOnlyList<Job> ListRunning();

    // Entradas del índice del pipeline, de la más reciente a la más antigua, sin leer los job.json.
    // Por defecto se derivan de List, para los dobles de test.
    IReadOnlyList<JobIndexEntry> ListIndex(string pipelineId) =>
        List(pageSize: int.MaxValue, pipelineId: pipelineId)
            .Select(j => new JobIndexEntry(j.Id, j.PipelineId, j.Type, j.Status, j.StartedAt, j.FinishedAt))
            .ToList();

    Job? Get(string id);
    Job Create(Job job);
    Job? Update(string id, Job job);
    void AppendEvent(string jobId, EventLevel level, string message);

    // Con el código de error conocido (KnownErrors). Por defecto lo descarta, para los dobles de test.
    void AppendEvent(string jobId, EventLevel level, string message, string? code) => AppendEvent(jobId, level, message);

    // Con la clave del texto localizable (JobEvent.MessageKey). Por defecto guarda solo el mensaje,
    // para los dobles de test.
    void AppendEvent(string jobId, EventLevel level, string message, string? code, string messageKey, params object?[] messageArgs) =>
        AppendEvent(jobId, level, message, code);
}

public sealed class JobService : IJobService
{
    private readonly JobStore _store;
    private readonly ILogger<JobService> _logger;

    public JobService(JobStore store, ILogger<JobService>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<JobService>.Instance;
    }

    // Se filtra y pagina sobre el índice; solo se leen enteros los jobs de la página.
    public IReadOnlyList<Job> List(int page = 1, int pageSize = 20, string? pipelineId = null, JobStatus? status = null, DateRange? range = null) =>
        Load(Filter(pipelineId, status, range).Skip((page - 1) * pageSize).Take(pageSize));

    public int Count(string? pipelineId = null, JobStatus? status = null, DateRange? range = null) =>
        Filter(pipelineId, status, range).Count();

    // Un job borrado entre el índice y la lectura simplemente no se devuelve.
    private List<Job> Load(IEnumerable<JobIndexEntry> entries) =>
        entries.Select(e => _store.GetById(e.Id)).OfType<Job>().ToList();

    // List y Count filtran igual: la paginación (y la exportación CSV) no pueden desfasarse.
    private IEnumerable<JobIndexEntry> Filter(string? pipelineId, JobStatus? status, DateRange? range)
    {
        var query = _store.ListIndex().AsEnumerable();
        if (!string.IsNullOrWhiteSpace(pipelineId))
        {
            query = query.Where(j => j.PipelineId == pipelineId);
        }

        if (status is not null)
        {
            query = query.Where(j => j.Status == status);
        }

        if (range is { IsEmpty: false })
        {
            query = query.Where(j => range.Contains(j.StartedAt));
        }

        return query;
    }

    // Jobs en curso según JobProgressHelper.IsActive: incluye Partial sin FinishedAt (ingest
    // parcial que sigue con SQL/Reconcile). De esto dependen la guarda anti doble run, el
    // scheduler, el watchdog, la retención y los contadores de "activos".
    public IReadOnlyList<Job> ListRunning() =>
        Load(_store.ListIndex().Where(e => JobProgressHelper.IsActive(e.Status, e.FinishedAt)))
            .Where(JobProgressHelper.IsActive)
            .ToList();

    public IReadOnlyList<JobIndexEntry> ListIndex(string pipelineId) => Filter(pipelineId, null, null).ToList();

    public Job? Get(string id) => _store.GetById(id);

    public Job Create(Job job)
    {
        _store.Save(job);
        return job;
    }

    public Job? Update(string id, Job job)
    {
        if (!_store.Exists(id))
        {
            return null;
        }

        // job.Version viaja intacto desde el "job with {...}" del caller (récord: los campos
        // no listados se preservan). Si no coincide con lo que hay en disco en el momento de
        // guardar, alguien más escribió el job entre que el caller lo leyó y este Update —
        // lost-update potencial. No se aborta (mismo comportamiento de siempre, para no romper
        // el control de flujo de los executors), pero ahora queda visible en el log en vez de
        // pasar en silencio total. SaveWithVersionCheck guarda bajo el mismo lock que
        // MutateAtomic, así que tampoco corre contra un AppendEvent concurrente del mismo job.
        var updated = _store.SaveWithVersionCheck(job with { Id = id }, out var versionConflict, out var previousVersion, out var cancellationKept);
        if (cancellationKept)
        {
            _logger.LogInformation("Job {JobId}: cancelado durante la actualización; se conserva el estado Cancelado.", id);
            return updated;
        }

        if (versionConflict)
        {
            _logger.LogWarning(
                "Job {JobId}: posible lost-update en Update — versión esperada {Expected}, versión actual en disco {Actual}.",
                id, job.Version, previousVersion);
        }

        return updated;
    }

    public void AppendEvent(string jobId, EventLevel level, string message) => AppendEvent(jobId, level, message, null);

    public void AppendEvent(string jobId, EventLevel level, string message, string? code) =>
        Append(jobId, new JobEvent
        {
            At = DateTimeOffset.UtcNow,
            Level = level,
            Message = message,
            Code = code,
        });

    public void AppendEvent(string jobId, EventLevel level, string message, string? code, string messageKey, params object?[] messageArgs) =>
        Append(jobId, new JobEvent
        {
            At = DateTimeOffset.UtcNow,
            Level = level,
            Message = message,
            Code = code,
            MessageKey = messageKey,
            MessageArgs = messageArgs.Length > 0 ? messageArgs : null,
        });

    private void Append(string jobId, JobEvent newEvent)
    {
        // Leer + agregar + guardar bajo un único lock (JobStore.MutateAtomic): ninguna otra
        // llamada a AppendEvent/Update del mismo proceso puede intercalarse a mitad del ciclo,
        // así que no hace falta reintento para la concurrencia dentro del propio proceso.
        _store.MutateAtomic(jobId, job => job with { Events = [.. job.Events, newEvent] });
    }
}