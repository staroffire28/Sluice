using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Tests.Fakes;
using Sluice.Portal.Workers;
using PipelineModel = Sluice.Portal.Models.Pipeline;

namespace Sluice.Portal.Tests;

// Corrección automática: al cerrar un job con tablas accionables, StageRouter lanza UN re-sync
// (recarga) de todas ellas en un solo job, solo con el ajuste activo y nunca desde otro re-sync.
// Stores reales en un DATA_DIR temporal (el lock de run vive en DATA_DIR/locks).
public sealed class AutoRemediationTests : IDisposable
{
    private const string PipelineId = "pipe-auto";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-autofix-" + Guid.NewGuid().ToString("N"));
    private readonly JobService _jobs;
    private readonly TaskStore _tasks;
    private readonly IngestManifestStore _manifests;
    private readonly ReportStore _reports;
    private readonly SettingsStore _settings;
    private readonly OrchestrationService _orchestration;
    private readonly RecordingActivityLog _activity = new();

    public AutoRemediationTests()
    {
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        _jobs = new JobService(new JobStore(options));
        _settings = new SettingsStore(options);
        _tasks = new TaskStore(options, _settings);
        _manifests = new IngestManifestStore(options);
        _reports = new ReportStore(options);
        _orchestration = new OrchestrationService(
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

    // --- Orquestación ---

    [Fact]
    public async Task ResyncTablesAsync_varias_tablas_en_un_job_con_remediacion_por_tabla()
    {
        var job = await _orchestration.ResyncTablesAsync(PipelineId, ["account", "contact"], ResyncMode.Reload, AutoRemediation.TriggeredByAuto);

        Assert.Equal(JobType.ResyncTable, job.Type);
        Assert.Equal(["account", "contact"], _manifests.Load(PipelineId, job.Id)!.Entities.Select(e => e.Entity));
        var history = _reports.ListRemediations(PipelineId);
        Assert.Equal(2, history.Count);
        Assert.All(history, r =>
        {
            Assert.Equal(job.Id, r.JobId);
            Assert.Equal(AutoRemediation.TriggeredByAuto, r.TriggeredBy);
        });

        var task = (await _tasks.GetAsync($"task-{job.Id}-sql", PipelineStage.Sql))!;
        Assert.Equal("account,contact", task.Payload["entity"]?.ToString());
        Assert.Equal("true", task.Payload["auto"]?.ToString());
        Assert.Equal("Reload", task.Payload["resyncMode"]?.ToString());
    }

    [Fact]
    public async Task ResyncTableAsync_manual_queda_como_user_en_el_historial()
    {
        var job = await _orchestration.ResyncTableAsync(PipelineId, "account", ResyncMode.Remerge);

        var remediation = Assert.Single(_reports.ListRemediations(PipelineId));
        Assert.Equal($"remed-{job.Id}", remediation.Id);
        Assert.Equal(AutoRemediation.TriggeredByUser, remediation.TriggeredBy);
        Assert.Equal("false", (await _tasks.GetAsync($"task-{job.Id}-sql", PipelineStage.Sql))!.Payload["auto"]?.ToString());
    }

    [Theory]
    [InlineData("account")]
    [InlineData(null)]
    public async Task CompareValuesAsync_arranca_en_conciliacion_con_la_tabla_en_el_payload(string? entity)
    {
        var job = await _orchestration.CompareValuesAsync(PipelineId, entity);

        var stored = _jobs.Get(job.Id)!;
        Assert.Equal(JobType.CompareValues, stored.Type);
        Assert.Equal(PipelineStage.Reconcile, stored.CurrentStage);
        Assert.Equal("skipped", stored.StageStatus["ingest"]);
        Assert.Equal("skipped", stored.StageStatus["sql"]);
        var task = (await _tasks.GetAsync($"task-{job.Id}-reconcile", PipelineStage.Reconcile))!;
        Assert.Equal(entity, task.Payload.TryGetValue("entity", out var e) ? e?.ToString() : null);
    }

    // --- StageRouter ---

    [Theory]
    [InlineData(JobType.Incremental)]
    [InlineData(JobType.Reconcile)]
    [InlineData(JobType.CompareValues)]
    public async Task Al_cerrar_un_job_con_tablas_accionables_lanza_un_resync_automatico(JobType type)
    {
        _settings.Save(new AppSettings { AutoRemediate = true });
        CreateFinishingJob("job-src", type, "account,contact");

        await Router().RouteCompletedTaskAsync(ReconcileTask("job-src"));

        Assert.Equal(JobStatus.Partial, _jobs.Get("job-src")!.Status);
        var fix = Assert.Single(_jobs.List(pipelineId: PipelineId), j => j.Type == JobType.ResyncTable);
        Assert.Equal(["account", "contact"], _manifests.Load(PipelineId, fix.Id)!.Entities.Select(e => e.Entity));
        Assert.All(_reports.ListRemediations(PipelineId), r => Assert.Equal(AutoRemediation.TriggeredByAuto, r.TriggeredBy));
        Assert.Contains(_jobs.Get("job-src")!.Events, e => e.Message.Contains(fix.Id));
        Assert.Contains(_activity.Entries, e => e.Action == "reconcile.autofix");
    }

    [Fact]
    public async Task Con_el_ajuste_apagado_no_lanza_nada()
    {
        CreateFinishingJob("job-off", JobType.Incremental, "account");

        await Router().RouteCompletedTaskAsync(ReconcileTask("job-off"));

        Assert.Equal(["job-off"], _jobs.List(pipelineId: PipelineId).Select(j => j.Id));
    }

    [Theory]
    [InlineData(JobType.ResyncTable)]
    [InlineData(JobType.Diff)]
    public async Task Un_resync_o_un_diff_no_disparan_otra_correccion(JobType type)
    {
        _settings.Save(new AppSettings { AutoRemediate = true });
        CreateFinishingJob("job-loop", type, "account");

        await Router().RouteCompletedTaskAsync(ReconcileTask("job-loop"));

        Assert.Equal(["job-loop"], _jobs.List(pipelineId: PipelineId).Select(j => j.Id));
    }

    [Fact]
    public async Task Sin_tablas_accionables_no_lanza_nada()
    {
        _settings.Save(new AppSettings { AutoRemediate = true });
        CreateFinishingJob("job-clean", JobType.Incremental, "");

        await Router().RouteCompletedTaskAsync(ReconcileTask("job-clean"));

        Assert.Equal(["job-clean"], _jobs.List(pipelineId: PipelineId).Select(j => j.Id));
    }

    // El scheduler pudo arrancar otro job entretanto: la corrección no se lanza, se registra y la
    // tarea de conciliación no falla (si el router lanzara, el job acabaría marcado como Error).
    [Fact]
    public async Task Si_ya_hay_otro_job_en_curso_lo_registra_sin_lanzar()
    {
        _settings.Save(new AppSettings { AutoRemediate = true });
        CreateFinishingJob("job-busy", JobType.Incremental, "account");
        _jobs.Create(new Job { Id = "job-scheduled", PipelineId = PipelineId, Status = JobStatus.Running });

        await Router().RouteCompletedTaskAsync(ReconcileTask("job-busy"));

        var job = _jobs.Get("job-busy")!;
        Assert.Equal(JobStatus.Partial, job.Status);
        Assert.Contains(job.Events, e => e.Level == EventLevel.Warning && e.Message.Contains("Corrección automática no lanzada"));
        Assert.DoesNotContain(_jobs.List(pipelineId: PipelineId), j => j.Type == JobType.ResyncTable);
        Assert.Contains(_activity.Entries, e => e.Action == "reconcile.autofix.skipped");
    }

    // --- ReconcileService ---

    [Fact]
    public void ActionableEntities_solo_las_que_superan_el_umbral_y_no_estan_aceptadas()
    {
        _settings.Save(new AppSettings { ReconDivergencePct = 1.0 });
        _reports.SaveAck(PipelineId, "accepted", new ReconAck { AckPct = 50 });
        var service = new ReconcileService(_settings, _reports);

        var report = service.BuildReport(PipelineId, "job-r", [
            Entity("over", 100, 90),       // 10 %
            Entity("under", 1000, 995),    // 0,5 %
            Entity("accepted", 100, 50),   // 50 %, aceptada
        ]);

        Assert.Equal(["over"], service.ActionableEntities(report));
    }

    [Fact]
    public void BuildScopedReport_con_varias_tablas_conserva_las_demas_del_ultimo_reporte()
    {
        var service = new ReconcileService(_settings, _reports);
        service.BuildReport(PipelineId, "job-full", [Entity("a", 10, 5), Entity("b", 10, 5), Entity("c", 10, 5)]);

        var scoped = service.BuildScopedReport(PipelineId, "job-fix", [Entity("a", 10, 10), Entity("b", 10, 10)]);

        Assert.Equal(["a", "b", "c"], scoped.Entities.Select(e => e.Entity).Order());
        Assert.Equal(0, scoped.Entities.Single(e => e.Entity == "a").DivergencePct);
        Assert.Equal(50, scoped.Entities.Single(e => e.Entity == "c").DivergencePct);
    }

    [Fact]
    public void ActionableValueEntities_excluye_las_aceptadas_y_las_que_no_se_pudieron_comparar()
    {
        _reports.SaveAck(PipelineId, "accepted", new ReconAck { AckPct = 0 });
        var service = new ReconcileService(_settings, _reports);

        var report = service.BuildValuesReport(PipelineId, "job-v", [
            new ValueCompareEntityResult { Entity = "diff", Different = 1 },
            new ValueCompareEntityResult { Entity = "clean", Matching = 5 },
            new ValueCompareEntityResult { Entity = "accepted", ExtraInDestination = 2 },
            new ValueCompareEntityResult { Entity = "broken", Error = "sin tabla" },
        ]);

        Assert.Equal(["diff"], service.ActionableValueEntities(report));
        Assert.Equal("job-v", _reports.GetLatestValues(PipelineId)!.JobId);
        Assert.NotNull(_reports.GetValuesByJob("job-v"));
    }

    private StageRouter Router() => new(_tasks, _jobs, _orchestration, _settings, _activity);

    // Job en su etapa de conciliación con las tablas accionables ya anotadas en el Summary.
    private void CreateFinishingJob(string id, JobType type, string actionable) =>
        _jobs.Create(new Job
        {
            Id = id,
            PipelineId = PipelineId,
            Type = type,
            Status = actionable.Length > 0 ? JobStatus.Partial : JobStatus.Running,
            CurrentStage = PipelineStage.Reconcile,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "done", ["reconcile"] = "done" },
            Summary = new Dictionary<string, object?> { [AutoRemediation.SummaryKey] = actionable },
        });

    private static PipelineTask ReconcileTask(string jobId) =>
        new() { Id = $"task-{jobId}-reconcile", Stage = PipelineStage.Reconcile, PipelineId = PipelineId, JobId = jobId };

    private static ReconcileEntityResult Entity(string name, long source, long destination) => new()
    {
        Entity = name,
        SourceRows = source,
        DestinationRows = destination,
        DivergencePct = ReconcileService.ComputeDivergencePct(source, destination),
    };

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
