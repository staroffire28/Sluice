using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;
using PipelineModel = Sluice.Portal.Models.Pipeline;

namespace Sluice.Portal.Tests;

// CancelAsync y ResyncTableAsync sobre stores reales en un DATA_DIR temporal
// (el lock de run vive en DATA_DIR/locks).
public sealed class OrchestrationServiceCancelResyncTests : IDisposable
{
    private const string PipelineId = "pipe-1";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-orchestration-" + Guid.NewGuid().ToString("N"));
    private readonly JobService _jobs;
    private readonly TaskStore _tasks;
    private readonly IngestManifestStore _manifests;
    private readonly ReportStore _reports;
    private readonly OrchestrationService _service;

    public OrchestrationServiceCancelResyncTests()
    {
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        _jobs = new JobService(new JobStore(options));
        _tasks = new TaskStore(options, new SettingsStore(options));
        _manifests = new IngestManifestStore(options);
        _reports = new ReportStore(options);
        _service = new OrchestrationService(
            options,
            new SinglePipelineService(PipelineId),
            _jobs,
            _tasks,
            new WorkerRegistry(options),
            _manifests,
            _reports,
            new StubParquetArtifactService());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    // --- CancelAsync ---

    [Fact]
    public async Task CancelAsync_job_en_curso_lo_marca_Cancelled_y_purga_sus_tareas()
    {
        _jobs.Create(new Job
        {
            Id = "job-c1",
            PipelineId = PipelineId,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "running", ["reconcile"] = "pending" },
        });
        // Una tarea Claimed (sql) y una Pending (reconcile) del job, y una pending de otro job.
        await _tasks.EnqueueAsync(NewTask("task-job-c1-sql", PipelineStage.Sql, "job-c1"));
        Assert.NotNull(await _tasks.TryClaimAsync(PipelineStage.Sql, "w1"));
        await _tasks.EnqueueAsync(NewTask("task-job-c1-reconcile", PipelineStage.Reconcile, "job-c1"));
        await _tasks.EnqueueAsync(NewTask("task-other-sql", PipelineStage.Sql, "job-other"));

        await _service.CancelAsync("job-c1");

        var job = _jobs.Get("job-c1")!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("done", job.StageStatus["ingest"]);
        Assert.Equal("cancelled", job.StageStatus["sql"]);
        Assert.Equal("cancelled", job.StageStatus["reconcile"]);
        var last = job.Events[^1];
        Assert.Equal(EventLevel.Warning, last.Level);
        Assert.Equal("Job cancelado por el usuario.", last.Message);

        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Claimed));
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Reconcile, PipelineTaskStatus.Pending));
        // La tarea del otro job no se toca.
        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
        Assert.NotNull(await _tasks.GetAsync("task-other-sql", PipelineStage.Sql));
    }

    // Un ingest parcial deja el job en Partial sin FinishedAt y el pipeline sigue: sigue en
    // curso y se tiene que poder cancelar (el detalle muestra el botón).
    [Fact]
    public async Task CancelAsync_job_Partial_en_curso_lo_marca_Cancelled()
    {
        _jobs.Create(new Job
        {
            Id = "job-c-partial",
            PipelineId = PipelineId,
            Status = JobStatus.Partial,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "error", ["sql"] = "running", ["reconcile"] = "pending" },
        });
        await _tasks.EnqueueAsync(NewTask("task-job-c-partial-reconcile", PipelineStage.Reconcile, "job-c-partial"));

        await _service.CancelAsync("job-c-partial");

        var job = _jobs.Get("job-c-partial")!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("error", job.StageStatus["ingest"]);
        Assert.Equal("cancelled", job.StageStatus["sql"]);
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Reconcile, PipelineTaskStatus.Pending));
    }

    // Mientras se purgan las tareas (await), el executor en vuelo puede guardar el job (fin de
    // etapa, Summary, entidades). El Update de la cancelación no debe pisarlo con la copia leída
    // antes del await.
    [Fact]
    public async Task CancelAsync_no_pisa_lo_que_un_executor_guarda_mientras_se_purgan_las_tareas()
    {
        var finished = new DateTimeOffset(2026, 6, 1, 10, 5, 0, TimeSpan.Zero);
        _jobs.Create(new Job
        {
            Id = "job-c-race",
            PipelineId = PipelineId,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "running", ["reconcile"] = "pending" },
        });
        var tasks = new InterleavingTaskStore(_tasks, () =>
        {
            var fresh = _jobs.Get("job-c-race")!;
            _jobs.Update(fresh.Id, fresh with
            {
                StageStatus = new Dictionary<string, string>(fresh.StageStatus) { ["sql"] = "done" },
                StageTimings = fresh.StageTimings.Set("sql", started: finished.AddMinutes(-5), finished: finished),
                Summary = new Dictionary<string, object?>(fresh.Summary) { ["sqlMergedRows"] = 42 },
            });
        });
        var service = new OrchestrationService(
            Options.Create(new AppOptions { DataDir = _dataDir }),
            new SinglePipelineService(PipelineId),
            _jobs,
            tasks,
            new WorkerRegistry(Options.Create(new AppOptions { DataDir = _dataDir })),
            _manifests,
            _reports,
            new StubParquetArtifactService());

        await service.CancelAsync("job-c-race");

        var job = _jobs.Get("job-c-race")!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal(finished, job.StageTimings["sql"].FinishedAt);
        Assert.Equal(42, JobSummary.GetInt(job, "sqlMergedRows"));
        Assert.Equal("done", job.StageStatus["sql"]);
        Assert.Equal("cancelled", job.StageStatus["reconcile"]);
    }

    [Theory]
    [InlineData(JobStatus.Ok)]
    [InlineData(JobStatus.Partial)]
    [InlineData(JobStatus.Error)]
    [InlineData(JobStatus.Cancelled)]
    public async Task CancelAsync_job_que_no_esta_en_curso_no_hace_nada(JobStatus status)
    {
        var finished = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        _jobs.Create(new Job { Id = "job-done", PipelineId = PipelineId, Status = status, FinishedAt = finished });
        await _tasks.EnqueueAsync(NewTask("task-job-done-sql", PipelineStage.Sql, "job-done"));

        await _service.CancelAsync("job-done");

        var job = _jobs.Get("job-done")!;
        Assert.Equal(status, job.Status);
        Assert.Equal(finished, job.FinishedAt);
        Assert.Empty(job.Events);
        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
    }

    // --- Reconcile: una sola etapa ---

    [Theory]
    [InlineData(JobType.Reconcile)]
    [InlineData(JobType.Diff)]
    public async Task RunPipelineAsync_Reconcile_omite_ingest_y_sql_y_encola_solo_la_conciliacion(JobType type)
    {
        var job = await _service.RunPipelineAsync(PipelineId, type);

        var stored = _jobs.Get(job.Id)!;
        Assert.Equal(PipelineStage.Reconcile, stored.CurrentStage);
        Assert.Equal("skipped", stored.StageStatus["ingest"]);
        Assert.Equal("skipped", stored.StageStatus["sql"]);
        Assert.Equal("pending", stored.StageStatus["reconcile"]);
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Reconcile, PipelineTaskStatus.Pending));
        Assert.NotNull(await _tasks.GetAsync($"task-{job.Id}-reconcile", PipelineStage.Reconcile));
    }

    // --- Partial en curso ---

    [Fact]
    public async Task RunPipelineAsync_con_un_Partial_en_curso_del_pipeline_no_arranca_otro()
    {
        _jobs.Create(new Job { Id = "job-partial-live", PipelineId = PipelineId, Status = JobStatus.Partial });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RunPipelineAsync(PipelineId, JobType.Incremental));

        Assert.Contains("en ejecución", ex.Message);
        Assert.Single(_jobs.List(pipelineId: PipelineId));
    }

    [Fact]
    public async Task RetryJobAsync_rechaza_un_job_que_sigue_en_curso()
    {
        _jobs.Create(new Job
        {
            Id = "job-partial-retry",
            PipelineId = PipelineId,
            Status = JobStatus.Partial,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "error", ["sql"] = "running", ["reconcile"] = "pending" },
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RetryJobAsync("job-partial-retry"));

        Assert.Contains("en curso", ex.Message);
        Assert.Single(_jobs.List(pipelineId: PipelineId));
    }

    // El watchdog cierra en Error un job huérfano con el ingest hecho y SQL a medias (pending o
    // running): el snapshot ya cuenta como procesado, así que reintentarlo como Incremental no
    // encontraría nada nuevo y no cargaría SQL. Debe recargar desde el parquet consolidado.
    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    public async Task RetryJobAsync_cerrado_por_el_watchdog_con_SQL_a_medias_recarga_desde_parquet(string sqlStage)
    {
        _jobs.Create(new Job
        {
            Id = "job-orphan",
            PipelineId = PipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Error,
            FinishedAt = DateTimeOffset.UtcNow,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = sqlStage, ["reconcile"] = "pending" },
        });

        var retry = await _service.RetryJobAsync("job-orphan");

        Assert.Equal(JobType.Force, retry.Type);
        Assert.NotEqual("job-orphan", retry.Id);
    }

    // Con ingest y SQL hechos y Reconcile a medias, un Incremental no conciliaría nada (no hay
    // snapshots nuevos): se reintenta solo la conciliación.
    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    public async Task RetryJobAsync_cerrado_por_el_watchdog_con_Reconcile_a_medias_reintenta_la_conciliacion(string reconcileStage)
    {
        _jobs.Create(new Job
        {
            Id = "job-orphan-recon",
            PipelineId = PipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Error,
            FinishedAt = DateTimeOffset.UtcNow,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = reconcileStage },
        });

        var retry = await _service.RetryJobAsync("job-orphan-recon");

        Assert.Equal(JobType.Reconcile, retry.Type);
        Assert.NotEqual("job-orphan-recon", retry.Id);
    }

    [Fact]
    public async Task CancelAsync_job_inexistente_no_lanza_ni_toca_la_cola()
    {
        await _tasks.EnqueueAsync(NewTask("task-job-x-sql", PipelineStage.Sql, "job-x"));

        await _service.CancelAsync("job-que-no-existe");

        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
        Assert.Empty(_jobs.List());
    }

    // --- ResyncTableAsync ---

    [Fact]
    public async Task ResyncTableAsync_crea_job_en_SQL_con_manifest_remediacion_y_una_sola_tarea()
    {
        var job = await _service.ResyncTableAsync(PipelineId, "account", ResyncMode.Reload);

        Assert.Equal(JobType.ResyncTable, job.Type);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(PipelineStage.Sql, job.CurrentStage);
        var stored = _jobs.Get(job.Id)!;
        Assert.Equal(JobStatus.Running, stored.Status);
        Assert.Equal("skipped", stored.StageStatus["ingest"]);
        Assert.Equal("pending", stored.StageStatus["sql"]);
        Assert.Equal("pending", stored.StageStatus["reconcile"]);

        var manifest = _manifests.Load(PipelineId, job.Id)!;
        var entity = Assert.Single(manifest.Entities);
        Assert.Equal("account", entity.Entity);
        Assert.Equal(IngestManifestStore.GetConsolidatedBlobPath(PipelineId, "account"), entity.ParquetPath);
        Assert.Equal(IngestManifestStore.GetConsolidatedSchemaBlobPath(PipelineId, "account"), entity.SchemaPath);

        var remediation = Assert.Single(_reports.ListRemediations(PipelineId));
        Assert.Equal($"remed-{job.Id}", remediation.Id);
        Assert.Equal(job.Id, remediation.JobId);
        Assert.Equal("account", remediation.Entity);
        Assert.Equal(ResyncMode.Reload, remediation.Mode);

        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Ingest, PipelineTaskStatus.Pending));
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Reconcile, PipelineTaskStatus.Pending));
        var task = (await _tasks.GetAsync($"task-{job.Id}-sql", PipelineStage.Sql))!;
        Assert.Equal(job.Id, task.JobId);
        Assert.Equal("account", task.Payload["entity"]?.ToString());
        Assert.Equal("Reload", task.Payload["resyncMode"]?.ToString());
        Assert.Equal("ResyncTable", task.Payload["jobType"]?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResyncTableAsync_rechaza_una_entidad_vacia_sin_crear_nada(string entity)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ResyncTableAsync(PipelineId, entity, ResyncMode.Remerge));

        Assert.Equal("Entity is required.", ex.Message);
        AssertNothingCreated();
    }

    [Fact]
    public async Task ResyncTableAsync_rechaza_si_ya_hay_un_job_en_curso_del_mismo_pipeline()
    {
        _jobs.Create(new Job { Id = "job-running", PipelineId = PipelineId, Status = JobStatus.Running });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ResyncTableAsync(PipelineId, "account", ResyncMode.Reload));

        Assert.Equal(["job-running"], _jobs.List().Select(j => j.Id));
        Assert.Empty(_reports.ListRemediations(PipelineId));
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
    }

    [Fact]
    public async Task ResyncTableAsync_un_job_en_curso_de_otro_pipeline_no_bloquea()
    {
        _jobs.Create(new Job { Id = "job-other-pipe", PipelineId = "pipe-2", Status = JobStatus.Running });

        var job = await _service.ResyncTableAsync(PipelineId, "account", ResyncMode.Remerge);

        Assert.Equal(PipelineId, job.PipelineId);
        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
    }

    [Fact]
    public async Task ResyncTableAsync_segundo_resync_seguido_queda_bloqueado_por_el_primero()
    {
        await _service.ResyncTableAsync(PipelineId, "account", ResyncMode.Reload);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ResyncTableAsync(PipelineId, "contact", ResyncMode.Reload));

        Assert.Equal(1, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
        Assert.Single(_reports.ListRemediations(PipelineId));
    }

    [Fact]
    public async Task ResyncTableAsync_pipeline_inexistente_lanza()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ResyncTableAsync("no-existe", "account", ResyncMode.Reload));

        Assert.Equal("Pipeline not found.", ex.Message);
        AssertNothingCreated();
    }

    private void AssertNothingCreated()
    {
        Assert.Empty(_jobs.List());
        Assert.Empty(_reports.ListRemediations(PipelineId));
        Assert.Equal(0, _tasks.CountByStatus(PipelineStage.Sql, PipelineTaskStatus.Pending));
    }

    private static PipelineTask NewTask(string id, PipelineStage stage, string jobId) =>
        new() { Id = id, Stage = stage, PipelineId = PipelineId, JobId = jobId };

    // TaskStore real que ejecuta una escritura "concurrente" justo al purgar las tareas del job.
    private sealed class InterleavingTaskStore(ITaskStore inner, Action duringCancel) : ITaskStore
    {
        public Task EnqueueAsync(PipelineTask task, CancellationToken cancellationToken = default) => inner.EnqueueAsync(task, cancellationToken);
        public Task<PipelineTask?> TryClaimAsync(PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => inner.TryClaimAsync(stage, workerId, cancellationToken);
        public Task<PipelineTask?> GetAsync(string taskId, PipelineStage stage, CancellationToken cancellationToken = default) => inner.GetAsync(taskId, stage, cancellationToken);
        public Task<PipelineTask?> HeartbeatAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => inner.HeartbeatAsync(taskId, stage, workerId, cancellationToken);
        public Task<PipelineTask?> CompleteAsync(string taskId, PipelineStage stage, string workerId, CancellationToken cancellationToken = default) => inner.CompleteAsync(taskId, stage, workerId, cancellationToken);
        public Task<PipelineTask?> FailAsync(string taskId, PipelineStage stage, string workerId, string error, CancellationToken cancellationToken = default) => inner.FailAsync(taskId, stage, workerId, error, cancellationToken);
        public Task ReclaimExpiredLeasesAsync(CancellationToken cancellationToken = default) => inner.ReclaimExpiredLeasesAsync(cancellationToken);
        public int CountByStatus(PipelineStage stage, PipelineTaskStatus status) => inner.CountByStatus(stage, status);

        public Task<int> CancelJobTasksAsync(string jobId, CancellationToken cancellationToken = default)
        {
            duringCancel();
            return inner.CancelJobTasksAsync(jobId, cancellationToken);
        }
    }

    private sealed class SinglePipelineService(string id) : IPipelineService
    {
        public IReadOnlyList<PipelineModel> List() => throw new NotSupportedException();

        public PipelineModel? Get(string pipelineId) => pipelineId == id
            ? new PipelineModel { Id = id, Name = id, SourceId = "src-1", DestinationId = "dst-1" }
            : null;

        public PipelineModel Create(PipelineModel pipeline) => throw new NotSupportedException();
        public PipelineModel? Update(string pipelineId, PipelineModel pipeline) => throw new NotSupportedException();
        public bool Delete(string pipelineId, bool wipeState, out string? error) => throw new NotSupportedException();
        public PipelineState GetState(string pipelineId) => throw new NotSupportedException();
        public IReadOnlyList<object> GetTables(string pipelineId) => throw new NotSupportedException();
    }
}
