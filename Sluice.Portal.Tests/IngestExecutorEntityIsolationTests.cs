using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests;

// Regresión: antes del fix, una excepción al leer/parsear UNA entidad (schema corrupto, CSV
// inválido, etc.) se propagaba fuera del bucle de entidades y abortaba TODO el ingest — las
// demás entidades sanas de las ~95 tablas de un pipeline real no llegaban a consolidarse.
// IngestExecutor ahora aísla el fallo por entidad, igual que ya hacía SqlExecutor.
public sealed class IngestExecutorEntityIsolationTests : IDisposable
{
    private readonly string _dataDir;

    public IngestExecutorEntityIsolationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-ingest-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        DataDirectoryInitializer.EnsureLayout(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OneEntityFails_OthersStillConsolidateAndJobEndsPartial()
    {
        const string pipelineId = "pipe-1";
        const string jobId = "job-ingest-1";
        const string snapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z"; // changelog "en escritura": marca el límite seguro

        var options = Options.Create(new AppOptions { DataDir = _dataDir });

        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline
        {
            Id = pipelineId,
            Name = "Sales",
            SourceId = "src-1",
            DestinationId = "dst-1",
            Enabled = true,
        });
        var connections = new ConnectionStore(
            options,
            new Sluice.Portal.Services.Connections.ConnectionSecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var pipelineService = new PipelineService(pipelines, new PipelineStateStore(options), connections);

        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "running" },
        });

        var schema = new CdmEntitySchema
        {
            EntityName = "good",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };

        const string goodCsv = """
            11111111-1111-1111-1111-111111111111,Alice,2026-06-08T10:00:00Z
            22222222-2222-2222-2222-222222222222,Bob,2026-06-08T10:05:00Z
            """;

        var adlsClient = new FakeAdlsClient(
            snapshots: [snapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [snapshot] = ["good", "bad"] },
            schemaByEntity: new() { ["good"] = schema },
            csvByEntity: new() { ["good"] = goodCsv });

        var executor = new IngestExecutor(
            jobs,
            pipelineService,
            new PipelineStateStore(options),
            new TablePauseStore(options),
            new SingleAdlsClientFactory(adlsClient),
            new IngestManifestStore(options),
            options,
            NullLogger<IngestExecutor>.Instance);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-ingest",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Ingest,
        });

        var manifest = new IngestManifestStore(options).Load(pipelineId, jobId);
        Assert.NotNull(manifest);
        var manifestEntity = Assert.Single(manifest!.Entities);
        Assert.Equal("good", manifestEntity.Entity, ignoreCase: true);

        var job = jobs.Get(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Partial, job!.Status);
        Assert.Equal("error", job.StageStatus["ingest"]);

        var goodResult = job.Entities.Single(e => string.Equals(e.Entity, "good", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ok", goodResult.Status);

        var badResult = job.Entities.Single(e => string.Equals(e.Entity, "bad", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("error", badResult.Status);
        Assert.Contains("simulated schema failure", badResult.Message);

        // Con una entidad fallida el snapshot NO se da por procesado: si avanzara, el siguiente
        // Incremental/Retry no lo reintentaría y los datos de "bad" se perderían en silencio.
        // Las entidades sanas sí registran su estado (re-ingerirlas es idempotente).
        var state = new PipelineStateStore(options).GetOrCreate(pipelineId);
        Assert.Null(state.LastProcessedSnapshot);
        Assert.True(state.Tables.ContainsKey("good"));
    }

    // Una cancelación (SIGTERM/redeploy) a mitad de ingest no es un fallo de entidad: debe
    // propagarse sin marcar entidades como fallidas ni dar el snapshot por procesado.
    [Fact]
    public async Task ExecuteAsync_Cancelled_PropagatesAndDoesNotAdvanceState()
    {
        const string pipelineId = "pipe-3";
        const string jobId = "job-ingest-3";
        const string snapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";

        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline { Id = pipelineId, Name = "Sales", SourceId = "src-1", DestinationId = "dst-1", Enabled = true });
        var connections = new ConnectionStore(
            options,
            new Sluice.Portal.Services.Connections.ConnectionSecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "running" },
        });

        var schema = new CdmEntitySchema
        {
            EntityName = "good",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };
        var adlsClient = new FakeAdlsClient(
            snapshots: [snapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [snapshot] = ["good"] },
            schemaByEntity: new() { ["good"] = schema },
            csvByEntity: new() { ["good"] = "11111111-1111-1111-1111-111111111111,2026-06-08T10:00:00Z" });

        var executor = new IngestExecutor(
            jobs,
            new PipelineService(pipelines, new PipelineStateStore(options), connections),
            new PipelineStateStore(options),
            new TablePauseStore(options),
            new SingleAdlsClientFactory(adlsClient),
            new IngestManifestStore(options),
            options,
            NullLogger<IngestExecutor>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-ingest-3",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Ingest,
        }, cts.Token));

        Assert.Null(new PipelineStateStore(options).GetOrCreate(pipelineId).LastProcessedSnapshot);
        Assert.Empty(jobs.Get(jobId)!.Entities);
    }

    // Regresión del enmascaramiento de fallos parciales: una entidad falla (OOM simulado) en el
    // primer snapshot procesado (el que "trae" el volcado grande) pero tiene éxito en el segundo
    // (incremental, pequeño) — antes del fix, esto quedaba invisible: la entidad terminaba "ok"
    // sin ninguna señal de que se perdió el snapshot fallido. Caso real confirmado en producción
    // con sysdatabaseloglines (670MB en un snapshot -> OutOfMemoryException).
    [Fact]
    public async Task ExecuteAsync_EntityFailsInEarlySnapshot_SucceedsLater_JobIsPartialWithWarning()
    {
        const string pipelineId = "pipe-2";
        const string jobId = "job-ingest-2";
        const string bigSnapshot = "2026-06-08T09.00.00Z";
        const string smallSnapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";

        var options = Options.Create(new AppOptions { DataDir = _dataDir });

        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline
        {
            Id = pipelineId,
            Name = "Sales",
            SourceId = "src-1",
            DestinationId = "dst-1",
            Enabled = true,
        });
        var connections = new ConnectionStore(
            options,
            new Sluice.Portal.Services.Connections.ConnectionSecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var pipelineService = new PipelineService(pipelines, new PipelineStateStore(options), connections);

        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "running" },
        });

        var schema = new CdmEntitySchema
        {
            EntityName = "growing",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };

        const string smallCsv = """
            33333333-3333-3333-3333-333333333333,Carol,2026-06-08T10:05:00Z
            """;

        var adlsClient = new FakeAdlsClient(
            snapshots: [bigSnapshot, smallSnapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [bigSnapshot] = ["growing"], [smallSnapshot] = ["growing"] },
            schemaByEntity: new() { ["growing"] = schema },
            csvByEntity: new() { ["growing"] = smallCsv },
            failCsvFor: [(bigSnapshot, "growing")]);

        var executor = new IngestExecutor(
            jobs,
            pipelineService,
            new PipelineStateStore(options),
            new TablePauseStore(options),
            new SingleAdlsClientFactory(adlsClient),
            new IngestManifestStore(options),
            options,
            NullLogger<IngestExecutor>.Instance);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-ingest-2",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Ingest,
        });

        var manifest = new IngestManifestStore(options).Load(pipelineId, jobId);
        Assert.NotNull(manifest);
        var manifestEntity = Assert.Single(manifest!.Entities);
        Assert.Equal("growing", manifestEntity.Entity, ignoreCase: true);

        var job = jobs.Get(jobId);
        Assert.NotNull(job);
        // Antes del fix: JobStatus.Done / StageStatus "done" (fallo enmascarado). Con el fix:
        // Partial, porque hubo un fallo parcial aunque la entidad terminó con datos.
        Assert.Equal(JobStatus.Partial, job!.Status);

        var result = job.Entities.Single(e => string.Equals(e.Entity, "growing", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ok", result.Status);
        Assert.Contains("ADVERTENCIA", result.Message);
        Assert.Contains(bigSnapshot, result.Message);
        Assert.Contains("OutOfMemoryException", result.Message);

        // Los datos del snapshot fallido no se cargaron: no se da por procesado, así el
        // siguiente run vuelve a leer bigSnapshot.
        Assert.Null(new PipelineStateStore(options).GetOrCreate(pipelineId).LastProcessedSnapshot);
    }

    // Salida temprana "sin snapshots nuevos": IngestExecutor guarda con la copia del job leída al
    // empezar (obsoleta tras su propio Update de inicio). El inicio de la etapa solo sobrevive si
    // ese último Update lo vuelve a escribir junto al fin.
    [Fact]
    public async Task ExecuteAsync_NoNewSnapshots_RecordsStageStartAndFinish()
    {
        const string currentFolder = "2026-06-08T11.00.00Z";
        var (options, jobs) = SetUpPipelineAndJob("pipe-nonew", "job-nonew");
        var adlsClient = new FakeAdlsClient(
            snapshots: [currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new(),
            schemaByEntity: new(),
            csvByEntity: new());

        var before = DateTimeOffset.UtcNow;
        await CreateExecutor(options, jobs, adlsClient).ExecuteAsync(IngestTask("pipe-nonew", "job-nonew"));

        var job = jobs.Get("job-nonew")!;
        Assert.Equal("done", job.StageStatus["ingest"]);
        var timing = job.StageTimings["ingest"];
        Assert.NotNull(timing.StartedAt);
        Assert.NotNull(timing.FinishedAt);
        Assert.True(timing.StartedAt >= before);
        Assert.True(timing.StartedAt <= timing.FinishedAt);
        Assert.Equal(0, JobSummary.GetInt(job, "snapshotsSelected"));
        Assert.Equal(0, JobSummary.GetInt(job, "snapshotsProcessed"));
        Assert.Contains(job.Events, e => e.Message == JobEventMarkers.NoNewSnapshots);
    }

    [Fact]
    public async Task ExecuteAsync_Blocked_RecordsStageStartAndFinish()
    {
        var (options, jobs) = SetUpPipelineAndJob("pipe-blocked", "job-blocked");
        // La carpeta del changelog no está en el listado: SnapshotSelector bloquea.
        var adlsClient = new FakeAdlsClient(
            snapshots: ["2026-06-08T10.00.00Z"],
            settledSnapshot: "2026-06-08T11.00.00Z",
            entitiesBySnapshot: new(),
            schemaByEntity: new(),
            csvByEntity: new());

        await CreateExecutor(options, jobs, adlsClient).ExecuteAsync(IngestTask("pipe-blocked", "job-blocked"));

        var job = jobs.Get("job-blocked")!;
        Assert.Equal(JobStatus.Blocked, job.Status);
        var timing = job.StageTimings["ingest"];
        Assert.NotNull(timing.StartedAt);
        Assert.NotNull(timing.FinishedAt);
        Assert.True(timing.StartedAt <= timing.FinishedAt);
    }

    [Fact]
    public async Task ExecuteAsync_Completes_RecordsStageStartAndFinish()
    {
        const string snapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";
        var (options, jobs) = SetUpPipelineAndJob("pipe-ok", "job-ok");
        var adlsClient = new FakeAdlsClient(
            snapshots: [snapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [snapshot] = ["good"] },
            schemaByEntity: new() { ["good"] = SimpleSchema("good") },
            csvByEntity: new() { ["good"] = "11111111-1111-1111-1111-111111111111,2026-06-08T10:00:00Z" });

        await CreateExecutor(options, jobs, adlsClient).ExecuteAsync(IngestTask("pipe-ok", "job-ok"));

        var job = jobs.Get("job-ok")!;
        Assert.Equal("done", job.StageStatus["ingest"]);
        var timing = job.StageTimings["ingest"];
        Assert.NotNull(timing.StartedAt);
        Assert.NotNull(timing.FinishedAt);
        Assert.True(timing.StartedAt <= timing.FinishedAt);
    }

    // Una entidad enorme con un solo CSV: las filas leídas avanzan durante la entidad (por lote,
    // con el throttle de 1 s), no solo al terminarla.
    [Fact]
    public async Task ExecuteAsync_WithProgressStore_ReadRowsAdvanceWithinALargeEntity()
    {
        const string snapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";
        var (options, jobs) = SetUpPipelineAndJob("pipe-rows-live", "job-rows-live");
        var clock = new FixedTimeProvider();
        var progressStore = new JobProgressStore(new IngestManifestStore(options), clock);
        var csv = string.Join('\n', Enumerable.Range(0, 12_000).Select(_ => $"{Guid.NewGuid()},2026-06-08T10:00:00Z"));
        long? rowsSeenMidEntity = null;
        var adlsClient = new FakeAdlsClient(
            snapshots: [snapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [snapshot] = ["big"] },
            schemaByEntity: new() { ["big"] = SimpleSchema("big") },
            csvByEntity: new() { ["big"] = csv },
            onLine: i =>
            {
                if (i == 4_999)
                {
                    // Pasa el segundo del throttle justo antes de completar el primer lote.
                    clock.Advance(TimeSpan.FromSeconds(2));
                }
                else if (i == 6_000)
                {
                    rowsSeenMidEntity = progressStore.Load("pipe-rows-live", "job-rows-live")!.Steps[JobStepCatalog.Snapshots].Rows;
                }
            });

        await CreateExecutor(options, jobs, adlsClient, progressStore).ExecuteAsync(IngestTask("pipe-rows-live", "job-rows-live"));

        Assert.Equal(5_000, rowsSeenMidEntity);
        Assert.Equal(12_000, progressStore.Load("pipe-rows-live", "job-rows-live")!.Steps[JobStepCatalog.Snapshots].Rows);
    }

    [Fact]
    public async Task ExecuteAsync_WithProgressStore_RecordsEveryStepWithCounters()
    {
        const string s1 = "2026-06-08T09.00.00Z";
        const string s2 = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";
        var (options, jobs) = SetUpPipelineAndJob("pipe-progress", "job-progress");
        var adlsClient = new FakeAdlsClient(
            snapshots: [s1, s2, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [s1] = ["good"], [s2] = ["good"] },
            schemaByEntity: new() { ["good"] = SimpleSchema("good") },
            csvByEntity: new() { ["good"] = "11111111-1111-1111-1111-111111111111,2026-06-08T10:00:00Z" });
        var progressStore = new JobProgressStore(new IngestManifestStore(options));

        await CreateExecutor(options, jobs, adlsClient, progressStore).ExecuteAsync(IngestTask("pipe-progress", "job-progress"));

        var progress = progressStore.Load("pipe-progress", "job-progress");
        Assert.NotNull(progress);
        Assert.Equal("ingest", progress!.Stage);
        Assert.Null(progress.CurrentStep);
        Assert.Equal("done", progress.Steps[JobStepCatalog.Discover].Status);
        var snapshots = progress.Steps[JobStepCatalog.Snapshots];
        Assert.Equal("done", snapshots.Status);
        Assert.Equal(2, snapshots.Done);
        Assert.Equal(2, snapshots.Total);
        Assert.Equal(2, snapshots.Rows);
        var consolidate = progress.Steps[JobStepCatalog.Consolidate];
        Assert.Equal("done", consolidate.Status);
        Assert.Equal(1, consolidate.Done);
        Assert.Equal(1, consolidate.Total);
        Assert.Equal("done", progress.Steps[JobStepCatalog.Register].Status);
        Assert.All(progress.Steps.Values, step => Assert.NotNull(step.FinishedAt));
    }

    [Fact]
    public async Task ExecuteAsync_WithProgressStore_EntityFailure_MarksSnapshotsStepError()
    {
        const string snapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";
        var (options, jobs) = SetUpPipelineAndJob("pipe-progress-err", "job-progress-err");
        var adlsClient = new FakeAdlsClient(
            snapshots: [snapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [snapshot] = ["good", "bad"] },
            schemaByEntity: new() { ["good"] = SimpleSchema("good") },
            csvByEntity: new() { ["good"] = "11111111-1111-1111-1111-111111111111,2026-06-08T10:00:00Z" });
        var progressStore = new JobProgressStore(new IngestManifestStore(options));

        await CreateExecutor(options, jobs, adlsClient, progressStore).ExecuteAsync(IngestTask("pipe-progress-err", "job-progress-err"));

        var progress = progressStore.Load("pipe-progress-err", "job-progress-err")!;
        var snapshots = progress.Steps[JobStepCatalog.Snapshots];
        Assert.Equal("error", snapshots.Status);
        Assert.Equal(1, snapshots.Done);
        Assert.Equal(1, snapshots.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_WithProgressStore_NoNewSnapshots_SkipsRemainingSteps()
    {
        const string currentFolder = "2026-06-08T11.00.00Z";
        var (options, jobs) = SetUpPipelineAndJob("pipe-progress-nonew", "job-progress-nonew");
        var adlsClient = new FakeAdlsClient(
            snapshots: [currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new(),
            schemaByEntity: new(),
            csvByEntity: new());
        var progressStore = new JobProgressStore(new IngestManifestStore(options));

        await CreateExecutor(options, jobs, adlsClient, progressStore).ExecuteAsync(IngestTask("pipe-progress-nonew", "job-progress-nonew"));

        var steps = progressStore.Load("pipe-progress-nonew", "job-progress-nonew")!.Steps;
        Assert.Equal("done", steps[JobStepCatalog.Discover].Status);
        Assert.Equal("skipped", steps[JobStepCatalog.Snapshots].Status);
        Assert.Equal("skipped", steps[JobStepCatalog.Consolidate].Status);
        Assert.Equal("skipped", steps[JobStepCatalog.Register].Status);
    }

    [Fact]
    public async Task ExecuteAsync_WithProgressStore_Blocked_MarksDiscoverBlocked()
    {
        var (options, jobs) = SetUpPipelineAndJob("pipe-progress-blocked", "job-progress-blocked");
        var adlsClient = new FakeAdlsClient(
            snapshots: ["2026-06-08T10.00.00Z"],
            settledSnapshot: "2026-06-08T11.00.00Z",
            entitiesBySnapshot: new(),
            schemaByEntity: new(),
            csvByEntity: new());
        var progressStore = new JobProgressStore(new IngestManifestStore(options));

        await CreateExecutor(options, jobs, adlsClient, progressStore).ExecuteAsync(IngestTask("pipe-progress-blocked", "job-progress-blocked"));

        var steps = progressStore.Load("pipe-progress-blocked", "job-progress-blocked")!.Steps;
        Assert.Equal("blocked", steps[JobStepCatalog.Discover].Status);
        Assert.Equal("pending", steps[JobStepCatalog.Snapshots].Status);
    }

    // Incremental con consolidado previo: además del consolidado (todo), un delta en DATA_DIR con
    // solo las filas de los snapshots de esta corrida, para que SQL no remergee la tabla entera.
    [Fact]
    public async Task ExecuteAsync_Incremental_with_previous_consolidated_writes_a_delta_with_only_this_runs_rows()
    {
        const string snapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";
        const string oldId = "11111111-1111-1111-1111-111111111111";
        const string freshId = "33333333-3333-3333-3333-333333333333";
        var (options, jobs) = SetUpPipelineAndJob("pipe-delta", "job-delta");
        var adlsClient = new FakeAdlsClient(
            snapshots: [snapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [snapshot] = ["good"] },
            schemaByEntity: new() { ["good"] = SimpleSchema("good") },
            csvByEntity: new() { ["good"] = $"{freshId},2026-06-08T10:00:00Z" });
        await using (var previous = new MemoryStream())
        {
            await ParquetMaterializer.WriteToStreamAsync(previous, SimpleSchema("good"),
                [new Dictionary<string, object?> { ["Id"] = Guid.Parse(oldId), ["SinkModifiedOn"] = DateTimeOffset.Parse("2026-06-01T00:00:00Z") }]);
            previous.Position = 0;
            await ((IParquetStore)adlsClient).UploadParquetAsync(IngestManifestStore.GetConsolidatedBlobPath("pipe-delta", "good"), previous);
        }

        await CreateExecutor(options, jobs, adlsClient).ExecuteAsync(IngestTask("pipe-delta", "job-delta"));

        var manifests = new IngestManifestStore(options);
        var entry = Assert.Single(manifests.Load("pipe-delta", "job-delta")!.Entities);
        Assert.True(entry.HasDelta);
        await using var delta = File.OpenRead(manifests.GetDeltaPath("pipe-delta", "job-delta", "good"));
        var deltaRows = await ParquetMaterializer.ReadFromStreamAsync(delta, SimpleSchema("good"));
        Assert.Equal(Guid.Parse(freshId), Assert.Single(deltaRows)["Id"]);
        Assert.Equal(2, entry.FilteredRows); // el consolidado sigue teniendo todo
    }

    // Primera corrida (sin consolidado previo): la tabla destino está vacía y SQL carga el
    // consolidado; un delta sería una copia completa en disco que nadie lee.
    [Fact]
    public async Task ExecuteAsync_first_run_without_previous_consolidated_writes_no_delta()
    {
        const string snapshot = "2026-06-08T10.00.00Z";
        const string currentFolder = "2026-06-08T11.00.00Z";
        var (options, jobs) = SetUpPipelineAndJob("pipe-nodelta", "job-nodelta");
        var adlsClient = new FakeAdlsClient(
            snapshots: [snapshot, currentFolder],
            settledSnapshot: currentFolder,
            entitiesBySnapshot: new() { [snapshot] = ["good"] },
            schemaByEntity: new() { ["good"] = SimpleSchema("good") },
            csvByEntity: new() { ["good"] = "11111111-1111-1111-1111-111111111111,2026-06-08T10:00:00Z" });

        await CreateExecutor(options, jobs, adlsClient).ExecuteAsync(IngestTask("pipe-nodelta", "job-nodelta"));

        var manifests = new IngestManifestStore(options);
        Assert.False(Assert.Single(manifests.Load("pipe-nodelta", "job-nodelta")!.Entities).HasDelta);
        Assert.False(File.Exists(manifests.GetDeltaPath("pipe-nodelta", "job-nodelta", "good")));
    }

    private (IOptions<AppOptions> Options, JobService Jobs) SetUpPipelineAndJob(string pipelineId, string jobId)
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        new PipelineStore(options).Save(new Pipeline
        {
            Id = pipelineId,
            Name = "Sales",
            SourceId = "src-1",
            DestinationId = "dst-1",
            Enabled = true,
        });

        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "pending", ["sql"] = "pending", ["reconcile"] = "pending" },
        });

        return (options, jobs);
    }

    private static IngestExecutor CreateExecutor(
        IOptions<AppOptions> options, JobService jobs, IAdlsClient adlsClient, JobProgressStore? progress = null)
    {
        var connections = new ConnectionStore(
            options,
            new Sluice.Portal.Services.Connections.ConnectionSecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        return new IngestExecutor(
            jobs,
            new PipelineService(new PipelineStore(options), new PipelineStateStore(options), connections),
            new PipelineStateStore(options),
            new TablePauseStore(options),
            new SingleAdlsClientFactory(adlsClient),
            new IngestManifestStore(options),
            options,
            NullLogger<IngestExecutor>.Instance,
            progress);
    }

    private static PipelineTask IngestTask(string pipelineId, string jobId) => new()
    {
        Id = $"task-{jobId}-ingest",
        JobId = jobId,
        PipelineId = pipelineId,
        Stage = PipelineStage.Ingest,
    };

    private static CdmEntitySchema SimpleSchema(string entity) => new()
    {
        EntityName = entity,
        Attributes =
        [
            new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
            new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
        ],
    };

    private sealed class SingleAdlsClientFactory(IAdlsClient client) : IAdlsClientFactory
    {
        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(client);

        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult((IParquetStore)client);

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "ok"));
    }

    // Fake mínimo de IAdlsClient + IParquetStore (misma interfaz de origen y de destino del
    // parquet en este cliente, igual que el AdlsClient real): basta para ejercitar el flujo
    // completo de IngestExecutor sin ADLS real. Un solo snapshot procesable, dos entidades.
    private sealed class FakeAdlsClient(
        IReadOnlyList<string> snapshots,
        string settledSnapshot,
        Dictionary<string, string[]> entitiesBySnapshot,
        Dictionary<string, CdmEntitySchema> schemaByEntity,
        Dictionary<string, string> csvByEntity,
        IEnumerable<(string Snapshot, string Entity)>? failCsvFor = null,
        Action<int>? onLine = null) : IAdlsClient, IParquetStore
    {
        private readonly HashSet<(string Snapshot, string Entity)> _failCsvFor = new(failCsvFor ?? []);

        private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public Task TestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> GetSettledSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(settledSnapshot);

        public Task<DateTimeOffset?> GetChangelogLastModifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task<IReadOnlyList<string>> ListSnapshotFoldersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshots);

        public Task<SnapshotIntegrity> ValidateSnapshotAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SnapshotIntegrity(true, null, null));

        public Task<IReadOnlyList<string>> ListEntitiesAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(entitiesBySnapshot.GetValueOrDefault(snapshot, []));

        public Task<CdmEntitySchema> GetEntitySchemaAsync(string entity, string snapshot, string? settledSnapshot, CancellationToken cancellationToken = default) =>
            schemaByEntity.TryGetValue(entity, out var schema)
                ? Task.FromResult(schema)
                : throw new InvalidOperationException($"simulated schema failure for '{entity}'");

        public async IAsyncEnumerable<string> StreamEntityCsvLinesAsync(
            string snapshot, string entity, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_failCsvFor.Contains((snapshot, entity)))
            {
                // Simula el OutOfMemoryException real confirmado en producción al leer un CSV de
                // snapshot demasiado grande — el punto del test es verificar que el fallo en ESTE
                // snapshot no quede invisible si la entidad tiene éxito en otro.
                throw new OutOfMemoryException($"simulated OOM reading '{entity}' in snapshot '{snapshot}'");
            }

            if (!csvByEntity.TryGetValue(entity, out var csv))
            {
                throw new NotSupportedException($"no csv seeded for '{entity}'");
            }

            var lines = csv.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onLine?.Invoke(i);
                yield return lines[i];
                await Task.Yield();
            }
        }

        public Task<string?> ReadGlobalOptionsetMetadataCsvAsync(string snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task DeleteSnapshotFolderAsync(string snapshotFolder, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UploadParquetAsync(string blobPath, Stream content, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _blobs[blobPath] = ms.ToArray();
            return Task.CompletedTask;
        }

        public Task<Stream> OpenParquetReadAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_blobs[blobPath]));

        public Task<bool> ParquetExistsAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.ContainsKey(blobPath));

        public Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default)
        {
            _blobs.TryRemove(blobPath, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ParquetArtifact>>([]);

        public Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default)
        {
            _blobs[blobPath] = System.Text.Encoding.UTF8.GetBytes(content);
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.TryGetValue(blobPath, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null);
    }
}
