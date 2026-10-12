using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests;

// Regresión de un bug conocido: un manifest sintético de recarga
// (Force/ReloadFromParquet, ver OrchestrationService.EnqueueParquetReloadAsync) trae
// FilteredRows=0 por defecto en cada IngestEntityManifest. Antes del fix, ReconcileExecutor
// confiaba ciegamente en ese campo (entry.FilteredRows) y reportaba ~100% de divergencia falsa
// aunque el parquet consolidado tuviera filas reales. El fix recuenta el parquet real, igual
// que ya hacían las otras dos ramas de ReconcileExecutor.ExecuteAsync.
public sealed class ReconcileExecutorManifestTests : IDisposable
{
    private readonly string _dataDir;

    public ReconcileExecutorManifestTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-reconcile-manifest-" + Guid.NewGuid().ToString("N"));
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
    public async Task ExecuteAsync_ManifestWithZeroFilteredRows_RecountsRealParquetRows()
    {
        const string pipelineId = "pipe-1";
        const string jobId = "job-force-1";
        const string entity = "account";
        const int realSourceRows = 3;
        const long destinationRows = 3; // destino ya sincronizado: sin divergencia real

        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var parquetPath = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity);

        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline
        {
            Id = pipelineId,
            Name = "Sales",
            SourceId = "src-1",
            DestinationId = "dst-1",
            Enabled = true,
        });
        var connections = new ConnectionStore(options, new ConnectionSecretProtector(new EphemeralDataProtectionProvider()));
        var pipelineService = new PipelineService(pipelines, new PipelineStateStore(options), connections);

        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.Force,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Reconcile,
            StageStatus = new Dictionary<string, string> { ["reconcile"] = "running" },
        });

        var manifestStore = new IngestManifestStore(options);
        manifestStore.Save(new IngestManifest
        {
            JobId = jobId,
            PipelineId = pipelineId,
            Entities =
            [
                new IngestEntityManifest
                {
                    Entity = entity,
                    ParquetPath = parquetPath,
                    SchemaPath = "unused",
                    Snapshot = "",
                    // CsvRows/FilteredRows quedan en su default (0): así los deja
                    // EnqueueParquetReloadAsync para un manifest sintético de Force/Reload.
                },
            ],
        });

        var schema = new CdmEntitySchema
        {
            EntityName = entity,
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(100)", IsNullable = true },
            ],
        };

        var parquetStore = new BytesParquetStore();
        var rows = Enumerable.Range(0, realSourceRows)
            .Select(_ => new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["Name"] = "x" })
            .ToList();
        await using (var parquet = new MemoryStream())
        {
            await ParquetMaterializer.WriteToStreamAsync(parquet, schema, rows);
            parquet.Position = 0;
            await parquetStore.UploadParquetAsync(parquetPath, parquet);
        }

        var reports = new ReportStore(options);
        var reconcile = new ReconcileService(new SettingsStore(options), reports);
        var alerts = new AlertService(
            options,
            new SettingsStore(options),
            NullLogger<AlertService>.Instance,
            new UnusedHttpClientFactory());

        var progressStore = new JobProgressStore(manifestStore);
        var executor = new ReconcileExecutor(
            jobs,
            pipelineService,
            reconcile,
            manifestStore,
            new ParquetOnlyAdlsFactory(parquetStore),
            new FakeSqlConnectionFactory(),
            new FixedCountSqlMergeExecutor(destinationRows),
            alerts,
            progressStore);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-reconcile",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Reconcile,
        });

        var report = reports.GetLatestReconcile(pipelineId);
        Assert.NotNull(report);
        var result = Assert.Single(report!.Entities);
        Assert.Equal(realSourceRows, result.SourceRows);
        Assert.Equal(destinationRows, result.DestinationRows);
        Assert.Equal(0, result.DivergencePct);
        Assert.False(report.IsPartial);

        var job = jobs.Get(jobId);
        Assert.NotEqual(JobStatus.Partial, job!.Status);

        // La etapa deja registrados su inicio y su fin.
        var timing = job.StageTimings["reconcile"];
        Assert.NotNull(timing.StartedAt);
        Assert.NotNull(timing.FinishedAt);
        Assert.True(timing.StartedAt <= timing.FinishedAt);

        // Y su progreso por paso: una entidad comparada y el informe emitido.
        var progress = progressStore.Load(pipelineId, jobId)!;
        var compare = progress.Steps[JobStepCatalog.Compare];
        Assert.Equal("done", compare.Status);
        Assert.Equal(1, compare.Done);
        Assert.Equal(1, compare.Total);
        Assert.Equal("done", progress.Steps[JobStepCatalog.Report].Status);
    }

    // Reconcile (una sola etapa, sin manifest): compara todos los parquet consolidados del
    // pipeline contra SQL, guarda el ReconcileReport y marca Partial si supera el umbral.
    [Fact]
    public async Task ExecuteAsync_ReconcileWithoutManifest_ComparesEveryParquet_AndMarksPartialOverThreshold()
    {
        var (jobs, reports, progressStore) = await RunCountOnlyJobAsync(JobType.Reconcile, "job-recon-only", sourceRows: 3, destinationRows: 1);

        var report = reports.GetLatestReconcile("pipe-1");
        Assert.NotNull(report);
        Assert.Equal("job-recon-only", report!.JobId);
        Assert.True(report.IsPartial);
        var job = jobs.Get("job-recon-only")!;
        Assert.Equal(JobStatus.Partial, job.Status);
        Assert.Equal("done", job.StageStatus["reconcile"]);
        Assert.NotNull(JobSummary.GetDouble(job, "maxDivergencePct"));
        AssertCompareCountedEveryEntity(progressStore, "job-recon-only");
    }

    // Reconcile en un pipeline sin ningún parquet: guardar un ReconcileReport vacío lo convertía
    // en la "última conciliación" y dejaba en blanco Reconcile, Tablas y el banner de Pipelines.
    [Fact]
    public async Task ExecuteAsync_ReconcileWithNoParquet_KeepsThePreviousReportAndWarns()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        new ReportStore(options).SaveReconcile(new ReconcileReport { Id = "recon-job-old", PipelineId = "pipe-1", JobId = "job-old" });

        var (jobs, reports, _) = await RunCountOnlyJobAsync(JobType.Reconcile, "job-recon-empty", sourceRows: 0, destinationRows: 0, uploadParquet: false);

        Assert.Equal("job-old", reports.GetLatestReconcile("pipe-1")!.JobId);
        Assert.Null(reports.GetReconcileByJob("job-recon-empty"));
        var job = jobs.Get("job-recon-empty")!;
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal("done", job.StageStatus["reconcile"]);
        Assert.Null(JobSummary.GetDouble(job, "maxDivergencePct"));
        Assert.Contains(job.Events, e => e.Level == EventLevel.Warning && e.Message.Contains("Parquet"));
    }

    // Un Incremental sin snapshots nuevos llega a Reconcile con el manifest vacío: tampoco debe
    // pisar la última conciliación (pasaba en cada run programado sin datos nuevos).
    [Fact]
    public async Task ExecuteAsync_IncrementalWithNothingToCompare_KeepsThePreviousReport()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        new ReportStore(options).SaveReconcile(new ReconcileReport { Id = "recon-job-old", PipelineId = "pipe-1", JobId = "job-old" });

        var (jobs, reports, _) = await RunCountOnlyJobAsync(JobType.Incremental, "job-incr-empty", sourceRows: 0, destinationRows: 0, uploadParquet: false);

        Assert.Equal("job-old", reports.GetLatestReconcile("pipe-1")!.JobId);
        Assert.Null(reports.GetReconcileByJob("job-incr-empty"));
        var job = jobs.Get("job-incr-empty")!;
        Assert.Equal("done", job.StageStatus["reconcile"]);
        Assert.DoesNotContain(job.Events, e => e.Level == EventLevel.Warning);
    }

    // CompareValues: compara por Id el parquet consolidado con las filas de SQL, guarda su propio
    // reporte (sin tocar la conciliación de conteos), deja la comparación por tabla en Job.Entities
    // y anota la tabla para la corrección automática.
    [Fact]
    public async Task ExecuteAsync_CompareValues_SavesValuesReport_AndMarksPartialWithDifferences()
    {
        const string pipelineId = "pipe-1";
        const string entity = "account";
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline { Id = pipelineId, Name = "Sales", SourceId = "src-1", DestinationId = "dst-1", Enabled = true });
        var connections = new ConnectionStore(options, new ConnectionSecretProtector(new EphemeralDataProtectionProvider()));
        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = "job-values",
            PipelineId = pipelineId,
            Type = JobType.CompareValues,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Reconcile,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "skipped", ["sql"] = "skipped", ["reconcile"] = "pending" },
        });

        var schema = new CdmEntitySchema
        {
            EntityName = entity,
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
            ],
        };
        var same = Guid.NewGuid();
        var changed = Guid.NewGuid();
        var parquetStore = new BytesParquetStore();
        await using (var parquet = new MemoryStream())
        {
            await ParquetMaterializer.WriteToStreamAsync(parquet, schema,
            [
                new Dictionary<string, object?> { ["Id"] = same, ["Name"] = "Contoso" },
                new Dictionary<string, object?> { ["Id"] = changed, ["Name"] = "Fabrikam" },
            ]);
            parquet.Position = 0;
            await parquetStore.UploadParquetAsync(IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity), parquet);
        }

        await parquetStore.UploadTextAsync(
            IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity),
            System.Text.Json.JsonSerializer.Serialize(schema, JsonDefaults.Options));

        var reports = new ReportStore(options);
        var executor = new ReconcileExecutor(
            jobs,
            new PipelineService(pipelines, new PipelineStateStore(options), connections),
            new ReconcileService(new SettingsStore(options), reports),
            new IngestManifestStore(options),
            new ParquetOnlyAdlsFactory(parquetStore),
            new FakeSqlConnectionFactory(),
            new FixedCountSqlMergeExecutor(2, [[same, "Contoso"], [changed, "Fabrikam Inc"]]),
            new AlertService(options, new SettingsStore(options), NullLogger<AlertService>.Instance, new UnusedHttpClientFactory()),
            new JobProgressStore(new IngestManifestStore(options)));

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-job-values-reconcile",
            JobId = "job-values",
            PipelineId = pipelineId,
            Stage = PipelineStage.Reconcile,
        });

        var values = Assert.Single(reports.GetValuesByJob("job-values")!.Entities);
        Assert.Equal((1L, 1L, 0L, 0L), (values.Matching, values.Different, values.MissingInDestination, values.ExtraInDestination));
        Assert.Equal([changed.ToString()], values.SampleDifferent);
        Assert.Null(reports.GetLatestReconcile(pipelineId));

        var job = jobs.Get("job-values")!;
        Assert.Equal(JobStatus.Partial, job.Status);
        Assert.Equal("done", job.StageStatus["reconcile"]);
        var row = Assert.Single(job.Entities);
        Assert.Equal(("account", "partial", 2, (int?)2), (row.Entity, row.Status, row.CsvRows, row.SqlRows));
        Assert.Equal(["account"], AutoRemediation.EntitiesFrom(job));
    }

    // El paso compare se cerraba con el contador de antes de la última entidad (62 de 63).
    private static void AssertCompareCountedEveryEntity(JobProgressStore progressStore, string jobId)
    {
        var compare = progressStore.Load("pipe-1", jobId)!.Steps[JobStepCatalog.Compare];
        Assert.Equal("done", compare.Status);
        Assert.Equal(1, compare.Total);
        Assert.Equal(1, compare.Done);
    }

    // Diff es informativo: guarda el DiffReport (origen, destino y delta por entidad) sin marcar
    // Partial ni alertar, y sin tocar el último ReconcileReport del pipeline.
    [Fact]
    public async Task ExecuteAsync_Diff_SavesDiffReport_WithoutPartialNorTouchingTheReconcileReport()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var previous = new ReportStore(options);
        previous.SaveReconcile(new ReconcileReport { Id = "recon-job-old", PipelineId = "pipe-1", JobId = "job-old" });

        var (jobs, reports, progressStore) = await RunCountOnlyJobAsync(JobType.Diff, "job-diff", sourceRows: 3, destinationRows: 1);

        Assert.Equal("job-old", reports.GetLatestReconcile("pipe-1")!.JobId);
        var diff = reports.GetDiffByJob("job-diff");
        Assert.NotNull(diff);
        var entity = Assert.Single(diff!.Entities);
        Assert.Equal(("account", 3L, 1L, 2L), (entity.Entity, entity.SourceRows, entity.DestinationRows, entity.Delta));

        var job = jobs.Get("job-diff")!;
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal("done", job.StageStatus["reconcile"]);
        Assert.NotNull(job.StageTimings["reconcile"].FinishedAt);
        Assert.Equal(1, JobSummary.GetInt(job, "diffEntities"));
        Assert.Equal(2, JobSummary.GetLong(job, "diffTotalDelta"));
        Assert.Null(JobSummary.GetDouble(job, "maxDivergencePct"));
        AssertCompareCountedEveryEntity(progressStore, "job-diff");
    }

    // Diff de punta a punta: tras su única etapa, StageRouter lo cierra en Ok con FinishedAt
    // (no hay Partial por divergencia en un Diff).
    [Fact]
    public async Task Diff_RoutedAfterItsOnlyStage_EndsOkWithFinishedAt()
    {
        var (jobs, _, _) = await RunCountOnlyJobAsync(JobType.Diff, "job-diff-e2e", sourceRows: 3, destinationRows: 1);
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var tasks = new TaskStore(options, new SettingsStore(options));

        await new StageRouter(tasks, jobs).RouteCompletedTaskAsync(new PipelineTask
        {
            Id = "task-job-diff-e2e-reconcile",
            JobId = "job-diff-e2e",
            PipelineId = "pipe-1",
            Stage = PipelineStage.Reconcile,
        });

        var job = jobs.Get("job-diff-e2e")!;
        Assert.Equal(JobStatus.Ok, job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.CurrentStage);
        Assert.Equal("skipped", job.StageStatus["ingest"]);
        Assert.Equal("skipped", job.StageStatus["sql"]);
        Assert.Equal("done", job.StageStatus["reconcile"]);
        Assert.False(JobProgressHelper.IsActive(job));
    }

    private async Task<(JobService Jobs, ReportStore Reports, JobProgressStore Progress)> RunCountOnlyJobAsync(
        JobType type, string jobId, int sourceRows, long destinationRows, bool uploadParquet = true)
    {
        const string pipelineId = "pipe-1";
        const string entity = "account";
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline { Id = pipelineId, Name = "Sales", SourceId = "src-1", DestinationId = "dst-1", Enabled = true });
        var connections = new ConnectionStore(options, new ConnectionSecretProtector(new EphemeralDataProtectionProvider()));

        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = type,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Reconcile,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "skipped", ["sql"] = "skipped", ["reconcile"] = "pending" },
        });

        var schema = new CdmEntitySchema
        {
            EntityName = entity,
            Attributes = [new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false }],
        };
        var parquetStore = new BytesParquetStore();
        if (uploadParquet)
        {
            await using var parquet = new MemoryStream();
            await ParquetMaterializer.WriteToStreamAsync(
                parquet,
                schema,
                Enumerable.Range(0, sourceRows).Select(_ => new Dictionary<string, object?> { ["Id"] = Guid.NewGuid() }).ToList());
            parquet.Position = 0;
            await parquetStore.UploadParquetAsync(IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity), parquet);
        }

        var reports = new ReportStore(options);
        var progressStore = new JobProgressStore(new IngestManifestStore(options));
        var executor = new ReconcileExecutor(
            jobs,
            new PipelineService(pipelines, new PipelineStateStore(options), connections),
            new ReconcileService(new SettingsStore(options), reports),
            new IngestManifestStore(options),
            new ParquetOnlyAdlsFactory(parquetStore),
            new FakeSqlConnectionFactory(),
            new FixedCountSqlMergeExecutor(destinationRows),
            new AlertService(options, new SettingsStore(options), NullLogger<AlertService>.Instance, new UnusedHttpClientFactory()),
            progressStore);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = $"task-{jobId}-reconcile",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Reconcile,
        });

        return (jobs, reports, progressStore);
    }

    private sealed class FakeSqlConnectionFactory : ISqlConnectionFactory
    {
        public Task<SqlConnection> OpenAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SqlConnection());

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetSchema(string connectionId) => "dbo";

        public SqlServerVersion GetSqlVersion(string connectionId) => SqlServerVersion.SqlServer2022;
    }

    private sealed class FixedCountSqlMergeExecutor(long count, IReadOnlyList<object?[]>? rows = null) : ISqlMergeExecutor
    {
        public async IAsyncEnumerable<object?[]> ReadRowsAsync(
            SqlConnection connection,
            string schema,
            string entity,
            IReadOnlyList<string> columns,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in rows ?? throw new NotSupportedException())
            {
                yield return row;
            }

            await Task.CompletedTask;
        }

        public Task<int> MergeEntityAsync(
            SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            IReadOnlyList<Dictionary<string, object?>> rows,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CountRowsAsync(
            SqlConnection connection,
            string schema,
            string entity,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(count);

        public Task<int> DeleteOrphansAsync(
            SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            IReadOnlyCollection<object?> sourceKeys,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CdmEntitySchema> EnsureTableAsync(
            SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TruncateTableAsync(
            SqlConnection connection,
            string schema,
            string entity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BytesParquetStore : IParquetStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

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

        public Task UploadTextAsync(string blobPath, string content, CancellationToken cancellationToken = default)
        {
            _blobs[blobPath] = System.Text.Encoding.UTF8.GetBytes(content);
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync(string blobPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_blobs.TryGetValue(blobPath, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : null);

        public Task<IReadOnlyList<ParquetArtifact>> ListParquetAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ParquetArtifact>>(_blobs
                .Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal) && b.Key.EndsWith(".parquet", StringComparison.Ordinal))
                .Select(b => new ParquetArtifact(b.Key, b.Value.Length, null))
                .ToList());
    }

    private sealed class ParquetOnlyAdlsFactory(IParquetStore store) : IAdlsClientFactory
    {
        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(store);

        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "ok"));
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("No debería intentarse ningún HTTP en este test (sin webhook configurado).");
    }
}
