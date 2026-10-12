using System.Collections.Concurrent;
using System.Text.Json;
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

// Qué parquet mergea la etapa SQL y en qué modo: el delta de la corrida (solo lo nuevo) en un
// Incremental, o el consolidado completo cuando el delta no basta para dejar SQL al día.
public sealed class SqlExecutorDeltaTests : IDisposable
{
    private const string PipelineId = "pipe-delta";
    private const string JobId = "job-delta";
    private const string Entity = "account";

    private static readonly Guid Old1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Old2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Fresh = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-sqldelta-" + Guid.NewGuid().ToString("N"));
    private readonly IOptions<AppOptions> _options;
    private readonly IngestManifestStore _manifests;
    private readonly JobService _jobs;
    private readonly BytesParquetStore _parquet = new();

    public SqlExecutorDeltaTests()
    {
        Directory.CreateDirectory(_dataDir);
        _options = Options.Create(new AppOptions { DataDir = _dataDir, TranslateEnums = false });
        _manifests = new IngestManifestStore(_options);
        _jobs = new JobService(new JobStore(_options));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    // Incremental con delta y tabla con datos: solo las filas de la corrida, sin reescribir las
    // que no cambiaron; el delta se borra al terminar la etapa.
    [Fact]
    public async Task Incremental_with_delta_merges_only_the_delta_and_then_deletes_it()
    {
        var merge = await RunAsync(JobType.Incremental, targetRows: 10, writeDelta: true);

        var call = Assert.Single(merge.Calls);
        Assert.Equal(MergeMode.UpsertNewer, call.Mode);
        Assert.Equal([Fresh], call.Ids);
        Assert.False(Directory.Exists(Path.GetDirectoryName(_manifests.GetDeltaPath(PipelineId, JobId, Entity))));
    }

    // Tabla destino vacía (destino nuevo o vaciada a mano): el delta no la dejaría completa.
    [Fact]
    public async Task Incremental_into_an_empty_table_loads_the_consolidated_with_a_plain_insert()
    {
        var merge = await RunAsync(JobType.Incremental, targetRows: 0, writeDelta: true);

        var call = Assert.Single(merge.Calls);
        Assert.Equal(MergeMode.InsertIntoEmpty, call.Mode);
        Assert.Equal(3, call.Ids.Count);
    }

    // Force/Reload: siempre el consolidado, reescribiendo todo (p. ej. etiquetas de enums nuevas).
    [Fact]
    public async Task Force_merges_the_consolidated_unconditionally_even_if_a_delta_exists()
    {
        var merge = await RunAsync(JobType.Force, targetRows: 10, writeDelta: true);

        var call = Assert.Single(merge.Calls);
        Assert.Equal(MergeMode.Upsert, call.Mode);
        Assert.Equal(3, call.Ids.Count);
    }

    // El último job terminado no completó SQL: hoy un "Ejecutar" lo reparaba remergeando todo;
    // con solo el delta las filas que no llegaron quedarían fuera.
    [Fact]
    public async Task Incremental_after_a_job_whose_sql_did_not_finish_uses_the_consolidated()
    {
        _jobs.Create(new Job
        {
            Id = "job-prev",
            PipelineId = PipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Partial,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            FinishedAt = DateTimeOffset.UtcNow.AddHours(-1),
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "error", ["reconcile"] = "done" },
        });

        var merge = await RunAsync(JobType.Incremental, targetRows: 10, writeDelta: true);

        var call = Assert.Single(merge.Calls);
        Assert.Equal(MergeMode.Upsert, call.Mode);
        Assert.Equal(3, call.Ids.Count);
        Assert.Contains(_jobs.Get(JobId)!.Events, e => e.Message.Contains("consolidado completo"));
    }

    // Un re-sync de UNA tabla terminado después no repara lo que el Incremental anterior dejó sin
    // cargar en las demás: no cuenta como "SQL completado" del pipeline.
    [Fact]
    public async Task A_later_single_table_resync_does_not_hide_an_unfinished_sql_stage()
    {
        _jobs.Create(new Job
        {
            Id = "job-prev",
            PipelineId = PipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Partial,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
            FinishedAt = DateTimeOffset.UtcNow.AddHours(-2),
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "error", ["reconcile"] = "done" },
        });
        _jobs.Create(new Job
        {
            Id = "job-resync",
            PipelineId = PipelineId,
            Type = JobType.ResyncTable,
            Status = JobStatus.Ok,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-50),
            StageStatus = new Dictionary<string, string> { ["ingest"] = "skipped", ["sql"] = "done", ["reconcile"] = "done" },
        });

        var merge = await RunAsync(JobType.Incremental, targetRows: 10, writeDelta: true);

        var call = Assert.Single(merge.Calls);
        Assert.Equal(MergeMode.Upsert, call.Mode);
        Assert.Equal(3, call.Ids.Count);
    }

    // Las sentencias corren sin límite de tiempo: si una se queda más del umbral sin avanzar (un
    // bloqueo en el destino), la línea de tiempo del job lo avisa en vez de cortarla.
    [Fact]
    public async Task A_statement_that_runs_past_the_threshold_is_flagged_as_a_possible_block()
    {
        await RunAsync(JobType.Force, targetRows: 10, writeDelta: false,
            mergeDelay: TimeSpan.FromMilliseconds(800), stallAlertAfter: TimeSpan.FromMilliseconds(150));

        var warning = Assert.Single(_jobs.Get(JobId)!.Events, e => e.Message.Contains("posible bloqueo"));
        Assert.Equal(EventLevel.Warning, warning.Level);
        Assert.Contains($"{Entity} · merge", warning.Message);
    }

    // El delta es desechable: si no está (otro volumen, borrado), se usa el consolidado.
    [Fact]
    public async Task Incremental_whose_delta_file_is_missing_uses_the_consolidated()
    {
        var merge = await RunAsync(JobType.Incremental, targetRows: 10, writeDelta: false);

        var call = Assert.Single(merge.Calls);
        Assert.Equal(MergeMode.Upsert, call.Mode);
        Assert.Equal(3, call.Ids.Count);
    }

    private async Task<RecordingMergeExecutor> RunAsync(
        JobType type, long targetRows, bool writeDelta, TimeSpan? mergeDelay = null, TimeSpan? stallAlertAfter = null)
    {
        var schema = new CdmEntitySchema
        {
            EntityName = Entity,
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };
        var consolidatedPath = IngestManifestStore.GetConsolidatedBlobPath(PipelineId, Entity);
        var schemaPath = IngestManifestStore.GetConsolidatedSchemaBlobPath(PipelineId, Entity);
        await _parquet.UploadTextAsync(schemaPath, JsonSerializer.Serialize(schema, JsonDefaults.Options));
        await using (var consolidated = new MemoryStream())
        {
            await ParquetMaterializer.WriteToStreamAsync(consolidated, schema, [Row(Old1), Row(Old2), Row(Fresh)]);
            consolidated.Position = 0;
            await _parquet.UploadParquetAsync(consolidatedPath, consolidated);
        }

        var deltaPath = _manifests.GetDeltaPath(PipelineId, JobId, Entity);
        if (writeDelta)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(deltaPath)!);
            await using var delta = File.Create(deltaPath);
            await ParquetMaterializer.WriteToStreamAsync(delta, schema, [Row(Fresh)]);
        }

        _manifests.Save(new IngestManifest
        {
            JobId = JobId,
            PipelineId = PipelineId,
            Entities =
            [
                new IngestEntityManifest
                {
                    Entity = Entity,
                    ParquetPath = consolidatedPath,
                    SchemaPath = schemaPath,
                    Snapshot = "s1",
                    HasDelta = true,
                },
            ],
        });

        _jobs.Create(new Job
        {
            Id = JobId,
            PipelineId = PipelineId,
            Type = type,
            Status = JobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentStage = PipelineStage.Sql,
            StageStatus = new Dictionary<string, string> { ["ingest"] = "done", ["sql"] = "pending", ["reconcile"] = "pending" },
        });

        var merge = new RecordingMergeExecutor(targetRows, mergeDelay ?? TimeSpan.Zero);
        var protector = new ConnectionSecretProtector(new EphemeralDataProtectionProvider());
        var connections = new ConnectionStore(_options, protector);
        var pipelines = new PipelineStore(_options);
        pipelines.Save(new Pipeline { Id = PipelineId, Name = "Sales", SourceId = "src-1", DestinationId = "dst-1", Enabled = true });
        var executor = new SqlExecutor(
            _jobs,
            new PipelineService(pipelines, new PipelineStateStore(_options), connections),
            _manifests,
            new FakeSqlConnectionFactory(),
            merge,
            new ParquetOnlyAdlsFactory(_parquet),
            new AlertService(_options, new SettingsStore(_options), NullLogger<AlertService>.Instance, new NoHttpClientFactory()),
            _options,
            NullLogger<SqlExecutor>.Instance)
        {
            StallAlertAfter = stallAlertAfter ?? TimeSpan.FromHours(1),
        };

        await executor.ExecuteAsync(new PipelineTask { Id = "task-sql", JobId = JobId, PipelineId = PipelineId, Stage = PipelineStage.Sql });
        return merge;
    }

    private static Dictionary<string, object?> Row(Guid id) => new()
    {
        ["Id"] = id,
        ["SinkModifiedOn"] = DateTimeOffset.Parse("2026-06-08T10:00:00Z"),
    };

    private sealed record MergeCall(MergeMode Mode, List<Guid> Ids);

    // Registra modo y filas de cada MERGE; CountRows devuelve las filas simuladas del destino.
    // mergeDelay simula una sentencia lenta o bloqueada.
    private sealed class RecordingMergeExecutor(long targetRows, TimeSpan mergeDelay) : ISqlMergeExecutor
    {
        public List<MergeCall> Calls { get; } = [];

        public async Task<int> MergeEntityAsync(
            SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema,
            IReadOnlyList<Dictionary<string, object?>> rows, SqlServerVersion sqlVersion,
            MergeMode mode, CancellationToken cancellationToken = default)
        {
            if (mergeDelay > TimeSpan.Zero)
            {
                await Task.Delay(mergeDelay, cancellationToken);
            }

            var last = Calls.LastOrDefault();
            if (last is not null && last.Mode == mode)
            {
                last.Ids.AddRange(rows.Select(r => (Guid)r["Id"]!));
            }
            else
            {
                Calls.Add(new MergeCall(mode, rows.Select(r => (Guid)r["Id"]!).ToList()));
            }

            return rows.Count;
        }

        public Task<int> MergeEntityAsync(
            SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema,
            IReadOnlyList<Dictionary<string, object?>> rows, SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("La etapa SQL debe indicar el modo de MERGE.");

        public Task<long> CountRowsAsync(
            SqlConnection connection, string schema, string entity, SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(targetRows);

        public Task<int> DeleteOrphansAsync(
            SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema,
            IReadOnlyCollection<object?> sourceKeys, SqlServerVersion sqlVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<CdmEntitySchema> EnsureTableAsync(
            SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema,
            SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(cdmSchema);

        public Task<bool> TruncateTableAsync(
            SqlConnection connection, string schema, string entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

        public Task DeleteParquetAsync(string blobPath, CancellationToken cancellationToken = default) => Task.CompletedTask;

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

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
