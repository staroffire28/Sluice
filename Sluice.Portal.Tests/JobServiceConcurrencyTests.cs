using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Regresión: JobService.Update/AppendEvent hacían read-modify-write sin ningún control de
// versión — dos escritores concurrentes (p.ej. dos stages/workers reportando progreso del
// mismo job) podían pisarse entre sí sin detección (ya causó un bug real documentado:
// "entidades duplicadas en job JSON"). Ahora Job.Version + JobStore.TrySave detectan el
// conflicto y JobService.AppendEvent reintenta con estado fresco en vez de perder el evento.
public sealed class JobServiceConcurrencyTests : IDisposable
{
    private readonly string _dataDir;
    private readonly JobService _jobs;

    public JobServiceConcurrencyTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-jobservice-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        _jobs = new JobService(new JobStore(options));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task AppendEvent_ConcurrentWriters_NoEventsLost()
    {
        const string jobId = "job-concurrency-1";
        const int writers = 20;

        _jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = "pipe-1",
            Type = JobType.Incremental,
            Status = JobStatus.Running,
        });

        // Simula varios "escritores" (workers de distintas etapas + heartbeats) anotando
        // eventos en el mismo job al mismo tiempo — antes del fix, el patrón read-modify-write
        // sin versión perdía eventos silenciosamente bajo esta carrera.
        var tasks = Enumerable.Range(0, writers)
            .Select(i => Task.Run(() => _jobs.AppendEvent(jobId, EventLevel.Info, $"event-{i}")))
            .ToArray();
        await Task.WhenAll(tasks);

        var job = _jobs.Get(jobId);
        Assert.NotNull(job);
        Assert.Equal(writers, job!.Events.Count);

        var messages = job.Events.Select(e => e.Message).ToHashSet();
        for (var i = 0; i < writers; i++)
        {
            Assert.Contains($"event-{i}", messages);
        }
    }

    // "En curso" = JobProgressHelper.IsActive: un Partial sin FinishedAt (ingest parcial que
    // sigue con SQL/Reconcile) también corre. Guardas anti doble run, watchdog, scheduler y
    // retención dependen de ListRunning.
    [Fact]
    public void ListRunning_IncludesUnfinishedPartial_ExcludesFinishedJobs()
    {
        _jobs.Create(new Job { Id = "job-run", PipelineId = "pipe-1", Status = JobStatus.Running });
        _jobs.Create(new Job { Id = "job-partial-live", PipelineId = "pipe-1", Status = JobStatus.Partial });
        _jobs.Create(new Job { Id = "job-partial-done", PipelineId = "pipe-1", Status = JobStatus.Partial, FinishedAt = DateTimeOffset.UtcNow });
        _jobs.Create(new Job { Id = "job-ok", PipelineId = "pipe-1", Status = JobStatus.Ok, FinishedAt = DateTimeOffset.UtcNow });

        Assert.Equal(
            ["job-partial-live", "job-run"],
            _jobs.ListRunning().Select(j => j.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Update_IncrementsVersion_OnEachSave()
    {
        const string jobId = "job-version-1";
        var created = _jobs.Create(new Job { Id = jobId, PipelineId = "pipe-1" });
        Assert.Equal(0, created.Version);

        var first = _jobs.Update(jobId, created with { Status = JobStatus.Ok });
        Assert.Equal(1, first!.Version);

        var second = _jobs.Update(jobId, first with { Status = JobStatus.Partial });
        Assert.Equal(2, second!.Version);
    }

    [Fact]
    public void Update_StaleVersion_StillSavesWithoutThrowing()
    {
        const string jobId = "job-version-stale-1";
        var created = _jobs.Create(new Job { Id = jobId, PipelineId = "pipe-1" });

        // Dos "lecturas" del mismo estado inicial (Version=0), como si dos executors hubieran
        // leído el job antes de que ninguno escribiera.
        var readByWorkerA = created;
        var readByWorkerB = created;

        _jobs.Update(jobId, readByWorkerA with { StageStatus = new Dictionary<string, string> { ["ingest"] = "done" } });

        // WorkerB actualiza con su copia ya obsoleta (Version=0, pero en disco ya es 1) — no
        // debe tirar excepción; el comportamiento observable (mantener el pipeline andando) se
        // preserva, solo que ahora el conflicto queda logueado en vez de silencioso.
        var afterStaleUpdate = _jobs.Update(jobId, readByWorkerB with { StageStatus = new Dictionary<string, string> { ["sql"] = "running" } });

        Assert.NotNull(afterStaleUpdate);
        Assert.Equal(2, afterStaleUpdate!.Version);
    }

    // Los executors hacen AppendEvent("stage started") y luego Update con el job leído antes:
    // ese Update pisaba la lista de eventos y el evento se perdía.
    [Fact]
    public void Update_ConCopiaObsoleta_ConservaLosEventosDeAppendEvent()
    {
        const string jobId = "job-events-1";
        var stale = _jobs.Create(new Job { Id = jobId, PipelineId = "pipe-1", Status = JobStatus.Running });

        _jobs.AppendEvent(jobId, EventLevel.Info, "Ingest stage started.");
        _jobs.Update(jobId, stale with { CurrentStage = PipelineStage.Ingest });

        Assert.Contains(_jobs.Get(jobId)!.Events, e => e.Message == "Ingest stage started.");
    }

    // Un executor que termina con la copia leída al empezar (Running) revivía un job que el
    // usuario había cancelado mientras tanto, y el router encolaba el siguiente stage.
    [Fact]
    public void Update_ConCopiaObsoleta_NoReviveUnJobCancelado()
    {
        const string jobId = "job-cancel-1";
        var stale = _jobs.Create(new Job { Id = jobId, PipelineId = "pipe-1", Status = JobStatus.Running });

        _jobs.Update(jobId, stale with { Status = JobStatus.Cancelled, FinishedAt = DateTimeOffset.UtcNow });
        var result = _jobs.Update(jobId, stale with { Status = JobStatus.Partial, CurrentStage = PipelineStage.Sql });

        Assert.Equal(JobStatus.Cancelled, result!.Status);
        Assert.Equal(JobStatus.Cancelled, _jobs.Get(jobId)!.Status);
    }

    // Dos JobStore sobre el mismo DATA_DIR = dos procesos (api y worker en modo distribuido):
    // el lock en memoria de cada uno no protege del otro.
    [Fact]
    public async Task AppendEvent_DesdeDosStoresDistintos_NoPierdeEventos()
    {
        const string jobId = "job-two-processes-1";
        const int writersPerStore = 15;
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var storeA = new JobService(new JobStore(options));
        var storeB = new JobService(new JobStore(options));
        storeA.Create(new Job { Id = jobId, PipelineId = "pipe-1", Status = JobStatus.Running });

        await Task.WhenAll(Enumerable.Range(0, writersPerStore).SelectMany(i => new[]
        {
            Task.Run(() => storeA.AppendEvent(jobId, EventLevel.Info, $"a-{i}")),
            Task.Run(() => storeB.AppendEvent(jobId, EventLevel.Info, $"b-{i}")),
        }));

        Assert.Equal(writersPerStore * 2, _jobs.Get(jobId)!.Events.Count);
    }
}
