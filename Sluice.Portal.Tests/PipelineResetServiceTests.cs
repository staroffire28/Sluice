using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Reset de pipeline para pruebas (Fase 3): limpia estado + parquets, y opcionalmente trunca
// tablas SQL de destino. Los tres tests son puramente fake (no requieren SQL real): rechazo con
// un job en curso, reset sin truncar (nunca abre SQL) y truncado que falla antes de borrar nada.
// El TRUNCATE exitoso contra una tabla real lo cubre SqlMergeExecutorTruncateTests ([SqlFact]).
public sealed class PipelineResetServiceTests : IDisposable
{
    private readonly string _dataDir;

    public PipelineResetServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-pipeline-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    // Un reset bajo un job en curso truncaba SQL y borraba parquets que el job estaba usando,
    // y al terminar el job pisaba el estado reseteado con el viejo. Debe rechazarse sin tocar nada.
    [Fact]
    public async Task ResetAsync_WithRunningJob_IsRejectedAndTouchesNothing()
    {
        const string pipelineId = "pipe-reset-running";
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var stateStore = new PipelineStateStore(options);
        stateStore.Save(new PipelineState { PipelineId = pipelineId, LastProcessedSnapshot = "2026-06-08T10.00.00Z" });

        var jobs = new JobService(new JobStore(options));
        jobs.Create(new Job
        {
            Id = "job-running",
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
        });

        var sqlFactory = new NeverCalledSqlConnectionFactory();
        var service = new PipelineResetService(
            new FakePipelineService(pipelineId),
            stateStore,
            new FakeParquetArtifactService(pipelineId, [new ParquetArtifact($"{pipelineId}/account.parquet", 10, null)]),
            sqlFactory,
            new NeverCalledSqlMergeExecutor(),
            jobs);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetAsync(pipelineId, truncateSql: true));

        Assert.False(sqlFactory.OpenCalled);
        Assert.Equal("2026-06-08T10.00.00Z", stateStore.GetOrCreate(pipelineId).LastProcessedSnapshot);
    }

    [Fact]
    public async Task ResetAsync_WithoutTruncateSql_DeletesParquetsAndResetsState_NeverTouchesSql()
    {
        const string pipelineId = "pipe-reset-1";
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var stateStore = new PipelineStateStore(options);
        stateStore.Save(new PipelineState
        {
            PipelineId = pipelineId,
            LastProcessedSnapshot = "2026-06-08T10.00.00Z",
            Tables = new Dictionary<string, TableState>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = new TableState { RowCount = 100 },
                ["contact"] = new TableState { RowCount = 200 },
            },
        });

        var parquet = new FakeParquetArtifactService(pipelineId,
        [
            new ParquetArtifact($"{pipelineId}/account.parquet", 10, null),
            new ParquetArtifact($"{pipelineId}/contact.parquet", 20, null),
        ]);

        var sqlFactory = new NeverCalledSqlConnectionFactory();
        var service = new PipelineResetService(
            new FakePipelineService(pipelineId),
            stateStore,
            parquet,
            sqlFactory,
            new NeverCalledSqlMergeExecutor(),
            new JobService(new JobStore(options)));

        var result = await service.ResetAsync(pipelineId, truncateSql: false);

        Assert.True(result.Completed);
        Assert.True(result.StateReset);
        // 2 parquets + 2 sidecars de schema (borrado incondicional, best-effort).
        Assert.Equal(4, result.ParquetArtifactsDeleted);
        Assert.Empty(result.TablesTruncated);
        Assert.False(sqlFactory.OpenCalled);

        var resetState = stateStore.GetOrCreate(pipelineId);
        Assert.Null(resetState.LastProcessedSnapshot);
        Assert.Empty(resetState.Tables);
    }

    [Fact]
    public async Task ResetAsync_TruncateFails_StopsWithoutDeletingParquetsOrState()
    {
        const string pipelineId = "pipe-reset-2";
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var stateStore = new PipelineStateStore(options);
        var originalState = new PipelineState
        {
            PipelineId = pipelineId,
            LastProcessedSnapshot = "2026-06-08T10.00.00Z",
            Tables = new Dictionary<string, TableState>(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = new TableState { RowCount = 100 },
            },
        };
        stateStore.Save(originalState);

        var parquet = new FakeParquetArtifactService(pipelineId,
        [
            new ParquetArtifact($"{pipelineId}/account.parquet", 10, null),
        ]);

        // Conexión nunca se abre de verdad (SqlConnection se construye pero no se llama
        // OpenAsync sobre ella) — el merge executor fake lanza ANTES de tocarla.
        var service = new PipelineResetService(
            new FakePipelineService(pipelineId),
            stateStore,
            parquet,
            new ConstructOnlySqlConnectionFactory(),
            new ThrowingTruncateSqlMergeExecutor(),
            new JobService(new JobStore(options)));

        var result = await service.ResetAsync(pipelineId, truncateSql: true);

        Assert.False(result.Completed);
        var failure = Assert.Single(result.TableTruncateFailures);
        Assert.Equal("account", failure.Entity);

        // No se tocó nada más: ni parquets ni estado.
        Assert.Equal(0, parquet.DeleteCallCount);
        var stateAfter = stateStore.GetOrCreate(pipelineId);
        Assert.Equal(originalState.LastProcessedSnapshot, stateAfter.LastProcessedSnapshot);
        Assert.Single(stateAfter.Tables);
    }

    private sealed class FakePipelineService(string pipelineId) : IPipelineService
    {
        public IReadOnlyList<Pipeline> List() => [Get(pipelineId)!];

        public Pipeline? Get(string id) => id == pipelineId
            ? new Pipeline { Id = id, Name = "Test", SourceId = "src-1", DestinationId = "dst-1", Enabled = true }
            : null;

        public Pipeline Create(Pipeline pipeline) => throw new NotSupportedException();
        public Pipeline? Update(string id, Pipeline pipeline) => throw new NotSupportedException();
        public bool Delete(string id, bool wipeState, out string? error) => throw new NotSupportedException();
        public PipelineState GetState(string pid) => throw new NotSupportedException();
        public IReadOnlyList<object> GetTables(string pid) => throw new NotSupportedException();
    }

    private sealed class FakeParquetArtifactService(string pipelineId, List<ParquetArtifact> artifacts) : IParquetArtifactService
    {
        public int DeleteCallCount { get; private set; }

        public Task<IReadOnlyList<ParquetArtifact>> ListAsync(string pid, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ParquetArtifact>>(pid == pipelineId ? artifacts : []);

        public Task<Stream?> OpenAsync(string pid, string blobPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string pid, string blobPath, CancellationToken cancellationToken = default)
        {
            DeleteCallCount++;
            return Task.FromResult(true);
        }
    }

    // Simula ISqlConnectionFactory sin abrir una conexión real: basta con construir un
    // SqlConnection (no conecta hasta OpenAsync/ejecutar un comando), suficiente cuando el
    // ISqlMergeExecutor fake lanza antes de usarlo.
    private sealed class ConstructOnlySqlConnectionFactory : ISqlConnectionFactory
    {
        public Task<SqlConnection> OpenAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SqlConnection("Server=fake;Database=fake;"));

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetSchema(string connectionId) => "dbo";
        public SqlServerVersion GetSqlVersion(string connectionId) => SqlServerVersion.SqlServer2022;
    }

    private sealed class NeverCalledSqlConnectionFactory : ISqlConnectionFactory
    {
        public bool OpenCalled { get; private set; }

        public Task<SqlConnection> OpenAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            OpenCalled = true;
            throw new InvalidOperationException("No debería abrirse conexión SQL cuando truncateSql=false.");
        }

        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetSchema(string connectionId) => throw new NotSupportedException();
        public SqlServerVersion GetSqlVersion(string connectionId) => throw new NotSupportedException();
    }

    private sealed class NeverCalledSqlMergeExecutor : ISqlMergeExecutor
    {
        public Task<int> MergeEntityAsync(SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema, IReadOnlyList<Dictionary<string, object?>> rows, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CountRowsAsync(SqlConnection connection, string schema, string entity, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteOrphansAsync(SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema, IReadOnlyCollection<object?> sourceKeys, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CdmEntitySchema> EnsureTableAsync(SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TruncateTableAsync(SqlConnection connection, string schema, string entity, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingTruncateSqlMergeExecutor : ISqlMergeExecutor
    {
        public Task<int> MergeEntityAsync(SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema, IReadOnlyList<Dictionary<string, object?>> rows, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> CountRowsAsync(SqlConnection connection, string schema, string entity, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> DeleteOrphansAsync(SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema, IReadOnlyCollection<object?> sourceKeys, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CdmEntitySchema> EnsureTableAsync(SqlConnection connection, string schema, string entity, CdmEntitySchema cdmSchema, SqlServerVersion sqlVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TruncateTableAsync(SqlConnection connection, string schema, string entity, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated truncate failure (e.g. permisos insuficientes)");
    }
}
