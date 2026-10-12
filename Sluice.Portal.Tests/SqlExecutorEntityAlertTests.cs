using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
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

public sealed class SqlExecutorEntityAlertTests : IDisposable
{
    private readonly string _dataDir;
    private readonly RecordingHttpHandler _handler = new();

    public SqlExecutorEntityAlertTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-sql-alert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [SqlFact]
    public async Task ExecuteAsync_EntityFailure_SendsPipelineFailureWebhook()
    {
        var connStr = SqlFactAttribute.RequireConnectionString();

        const string pipelineId = "pipe-1";
        const string jobId = "job-1";
        const string entity = "account";
        var parquetPath = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity);
        var schemaPath = IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity);

        var options = Options.Create(new AppOptions { DataDir = _dataDir, TranslateEnums = false });
        var protector = new ConnectionSecretProtector(new EphemeralDataProtectionProvider());
        var connections = new ConnectionStore(options, protector);
        connections.Save(new Connection
        {
            Id = "dst-1",
            Name = "SQL",
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
            Config = new Dictionary<string, string?> { ["connection_string"] = connStr },
        });

        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline
        {
            Id = pipelineId,
            Name = "Sales",
            SourceId = "src-1",
            DestinationId = "dst-1",
            Enabled = true,
        });

        var pipelineService = new PipelineService(
            pipelines,
            new PipelineStateStore(options),
            connections);

        var settings = new SettingsStore(options);
        settings.Save(new AppSettings
        {
            AlertEnabled = true,
            AlertWebhookUrl = "https://logic.example/hook",
        });

        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = jobId,
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string> { ["sql"] = "running" },
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
        await parquetStore.UploadTextAsync(schemaPath, JsonSerializer.Serialize(schema, JsonDefaults.Options));
        await using (var parquet = new MemoryStream())
        {
            await ParquetMaterializer.WriteToStreamAsync(
                parquet,
                schema,
                [new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["Name"] = "x" }]);
            parquet.Position = 0;
            await parquetStore.UploadParquetAsync(parquetPath, parquet);
        }

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
                    SchemaPath = schemaPath,
                    CsvRows = 1,
                    Snapshot = "2026-06-24T08.00.00Z",
                },
            ],
        });

        var alerts = new AlertService(
            options,
            settings,
            NullLogger<AlertService>.Instance,
            new TestHttpClientFactory(_handler));

        var executor = new SqlExecutor(
            jobs,
            pipelineService,
            manifestStore,
            new SqlConnectionFactory(new ConnectionResolver(connections, protector)),
            new ThrowingSqlMergeExecutor(),
            new ParquetOnlyAdlsFactory(parquetStore),
            alerts,
            options,
            NullLogger<SqlExecutor>.Instance);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-sql",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Sql,
        });

        Assert.Equal(1, _handler.RequestCount);
        Assert.Contains("pipeline.failure", _handler.LastBody);
        Assert.Contains(pipelineId, _handler.LastBody);
        Assert.Contains(jobId, _handler.LastBody);
        Assert.Contains(entity, _handler.LastBody);

        var job = jobs.Get(jobId);
        Assert.Equal(JobStatus.Error, job!.Status);
        Assert.NotNull(job.FinishedAt);
    }

    // Salida temprana "sin manifest" (no necesita SQL: retorna antes de abrir la conexión). Guarda
    // con la copia del job leída al empezar, así que debe reescribir el inicio junto al fin.
    [Fact]
    public async Task ExecuteAsync_NoManifest_RecordsStageStartAndFinish()
    {
        const string pipelineId = "pipe-nomanifest";
        const string jobId = "job-nomanifest";
        var (executor, jobs) = CreateExecutorWithoutSql(pipelineId, jobId);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-sql",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Sql,
        });

        var job = jobs.Get(jobId)!;
        Assert.Equal("done", job.StageStatus["sql"]);
        var timing = job.StageTimings["sql"];
        Assert.NotNull(timing.StartedAt);
        Assert.NotNull(timing.FinishedAt);
        Assert.True(timing.StartedAt <= timing.FinishedAt);
        // El tiempo de la etapa anterior (escrito por IngestExecutor) no se pierde.
        Assert.Equal(IngestFinishedAt, job.StageTimings["ingest"].FinishedAt);
    }

    [Fact]
    public async Task ExecuteAsync_NoManifest_WithProgressStore_SkipsMerge()
    {
        const string pipelineId = "pipe-nomanifest-progress";
        const string jobId = "job-nomanifest-progress";
        var progressStore = new JobProgressStore(new IngestManifestStore(Options.Create(new AppOptions { DataDir = _dataDir })));
        var (executor, _) = CreateExecutorWithoutSql(pipelineId, jobId, progressStore);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-sql",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Sql,
        });

        var progress = progressStore.Load(pipelineId, jobId)!;
        Assert.Equal("sql", progress.Stage);
        Assert.Equal("done", progress.Steps[JobStepCatalog.Prepare].Status);
        Assert.Equal("skipped", progress.Steps[JobStepCatalog.Merge].Status);
    }

    // El bucle de MERGE ("entidad 12 de 63") con fakes: la conexión no se abre de verdad, solo
    // viaja hasta ISqlMergeExecutor. Una entidad sana y otra que falla al crear la tabla.
    [Fact]
    public async Task ExecuteAsync_WithProgressStore_RecordsMergeCountersAndFailures()
    {
        const string pipelineId = "pipe-merge-progress";
        const string jobId = "job-merge-progress";
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var parquetStore = new BytesParquetStore();
        var manifestStore = new IngestManifestStore(options);
        var entries = new List<IngestEntityManifest>();
        foreach (var entity in new[] { "good", "bad" })
        {
            var schema = new CdmEntitySchema
            {
                EntityName = entity,
                Attributes = [new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false }],
            };
            var parquetPath = IngestManifestStore.GetConsolidatedBlobPath(pipelineId, entity);
            var schemaPath = IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity);
            await parquetStore.UploadTextAsync(schemaPath, JsonSerializer.Serialize(schema, JsonDefaults.Options));
            await using (var parquet = new MemoryStream())
            {
                await ParquetMaterializer.WriteToStreamAsync(parquet, schema, [new Dictionary<string, object?> { ["Id"] = Guid.NewGuid() }]);
                parquet.Position = 0;
                await parquetStore.UploadParquetAsync(parquetPath, parquet);
            }

            entries.Add(new IngestEntityManifest { Entity = entity, ParquetPath = parquetPath, SchemaPath = schemaPath, CsvRows = 1, Snapshot = "s1" });
        }

        manifestStore.Save(new IngestManifest { JobId = jobId, PipelineId = pipelineId, Entities = entries });
        var progressStore = new JobProgressStore(manifestStore);
        var (executor, jobs) = CreateExecutorWithoutSql(
            pipelineId, jobId, progressStore, new FakeSqlConnectionFactory(), new FailingForSqlMergeExecutor("bad"), parquetStore);

        await executor.ExecuteAsync(new PipelineTask
        {
            Id = "task-sql",
            JobId = jobId,
            PipelineId = pipelineId,
            Stage = PipelineStage.Sql,
        });

        var progress = progressStore.Load(pipelineId, jobId)!;
        Assert.Null(progress.CurrentStep);
        Assert.Equal("done", progress.Steps[JobStepCatalog.Prepare].Status);
        var merge = progress.Steps[JobStepCatalog.Merge];
        Assert.Equal("error", merge.Status);
        Assert.Equal(2, merge.Done);
        Assert.Equal(2, merge.Total);
        Assert.Equal(1, merge.Failed);
        Assert.Equal(1, merge.Rows);
        Assert.Null(merge.Item);
        Assert.NotNull(merge.FinishedAt);

        var job = jobs.Get(jobId)!;
        Assert.Equal("error", job.StageStatus["sql"]);
        Assert.Equal(JobStatus.Partial, job.Status);
        Assert.NotNull(job.StageTimings["sql"].FinishedAt);
        Assert.Equal(1, JobSummary.GetInt(job, "sqlMergedRows"));
    }

    private static readonly DateTimeOffset IngestFinishedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private (SqlExecutor Executor, JobService Jobs) CreateExecutorWithoutSql(
        string pipelineId,
        string jobId,
        JobProgressStore? progress = null,
        ISqlConnectionFactory? sqlFactory = null,
        ISqlMergeExecutor? mergeExecutor = null,
        IParquetStore? parquetStore = null)
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir, TranslateEnums = false });
        var protector = new ConnectionSecretProtector(new EphemeralDataProtectionProvider());
        var connections = new ConnectionStore(options, protector);
        var pipelines = new PipelineStore(options);
        pipelines.Save(new Pipeline
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
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "pending", ["reconcile"] = "pending" },
            StageTimings = new Dictionary<string, StageTiming>
            {
                ["ingest"] = new() { StartedAt = IngestFinishedAt.AddMinutes(-1), FinishedAt = IngestFinishedAt },
            },
        });

        var settings = new SettingsStore(options);
        var executor = new SqlExecutor(
            jobs,
            new PipelineService(pipelines, new PipelineStateStore(options), connections),
            new IngestManifestStore(options),
            sqlFactory ?? new SqlConnectionFactory(new ConnectionResolver(connections, protector)),
            mergeExecutor ?? new ThrowingSqlMergeExecutor(),
            new ParquetOnlyAdlsFactory(parquetStore ?? new BytesParquetStore()),
            new AlertService(options, settings, NullLogger<AlertService>.Instance, new TestHttpClientFactory(_handler)),
            options,
            NullLogger<SqlExecutor>.Instance,
            progress);
        return (executor, jobs);
    }

    private sealed class FakeSqlConnectionFactory : ISqlConnectionFactory
    {
        public Task<Microsoft.Data.SqlClient.SqlConnection> OpenAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Microsoft.Data.SqlClient.SqlConnection());

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetSchema(string connectionId) => "dbo";

        public SqlServerVersion GetSqlVersion(string connectionId) => SqlServerVersion.SqlServer2022;
    }

    // MERGE simulado: cuenta las filas recibidas; la entidad indicada falla al crear la tabla.
    private sealed class FailingForSqlMergeExecutor(string failingEntity) : ISqlMergeExecutor
    {
        public Task<CdmEntitySchema> EnsureTableAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            entity == failingEntity
                ? throw new InvalidOperationException("simulated SQL entity failure")
                : Task.FromResult(cdmSchema);

        public Task<int> MergeEntityAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            IReadOnlyList<Dictionary<string, object?>> rows,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(rows.Count);

        public Task<long> CountRowsAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1L);

        public Task<int> DeleteOrphansAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            IReadOnlyCollection<object?> sourceKeys,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TruncateTableAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingSqlMergeExecutor : ISqlMergeExecutor
    {
        public Task<CdmEntitySchema> EnsureTableAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated SQL entity failure");

        public Task<int> MergeEntityAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            IReadOnlyList<Dictionary<string, object?>> rows,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CountRowsAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteOrphansAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
            string schema,
            string entity,
            CdmEntitySchema cdmSchema,
            IReadOnlyCollection<object?> sourceKeys,
            SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TruncateTableAsync(
            Microsoft.Data.SqlClient.SqlConnection connection,
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
            Task.FromResult<IReadOnlyList<ParquetArtifact>>([]);
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

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TestHttpClientFactory(RecordingHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}