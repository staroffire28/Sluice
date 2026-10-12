using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

// Regresión: WorkerRegistry vivía solo en memoria por proceso (AddSingleton). En modo
// distribuido cada contenedor (api, worker-ingest, worker-sql, worker-reconcile) tiene su
// propia instancia, así que el Dashboard servido por 'api' nunca veía los heartbeats reales
// de los demás contenedores. Ahora persiste en DATA_DIR/workers/ (volumen ya compartido entre
// contenedores, mismo patrón que TaskStore) — estos tests simulan dos "contenedores" con dos
// instancias de WorkerRegistry apuntando al mismo DataDir.
public sealed class WorkerRegistryTests : IDisposable
{
    private readonly string _dataDir;

    public WorkerRegistryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-worker-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void RegisterHeartbeat_VisibleFromOtherInstance_SameDataDir()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });

        // 'worker-sql-container' registra su heartbeat con SU PROPIA instancia (simula el
        // contenedor worker-sql en docker-compose).
        var workerContainerRegistry = new WorkerRegistry(options);
        workerContainerRegistry.RegisterHeartbeat("worker-sql-container", PipelineStage.Sql, "task-1", "pipe-1", "job-1");

        // El Dashboard, servido por el contenedor 'api', consulta con OTRA instancia apuntando
        // al mismo DATA_DIR — antes del fix esto siempre daba una lista vacía.
        var apiContainerRegistry = new WorkerRegistry(options);
        var workers = apiContainerRegistry.ListAll();

        var worker = Assert.Single(workers);
        Assert.Equal("worker-sql-container", worker.Id);
        Assert.Equal(WorkerState.Busy, worker.Status);
        Assert.Equal("task-1", worker.TaskId);
        Assert.Equal(PipelineStage.Sql, worker.Stage);
    }

    [Fact]
    public void SetIdle_UpdatesStatus_VisibleFromOtherInstance()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });

        var workerContainerRegistry = new WorkerRegistry(options);
        workerContainerRegistry.RegisterHeartbeat("worker-ingest-container", PipelineStage.Ingest, "task-1", "pipe-1", "job-1");
        workerContainerRegistry.SetIdle("worker-ingest-container", PipelineStage.Ingest);

        var apiContainerRegistry = new WorkerRegistry(options);
        var worker = Assert.Single(apiContainerRegistry.ListAll());
        Assert.Equal(WorkerState.Idle, worker.Status);
        Assert.Null(worker.TaskId);
    }

    [Fact]
    public void ListAll_NoWorkersDirectory_ReturnsEmpty()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var registry = new WorkerRegistry(options);

        Assert.Empty(registry.ListAll());
    }

    [Fact]
    public void ListAll_MultipleWorkers_OrderedById()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var registry = new WorkerRegistry(options);

        registry.RegisterHeartbeat("worker-b", PipelineStage.Sql, "t1", "p1", "j1");
        registry.RegisterHeartbeat("worker-a", PipelineStage.Ingest, "t2", "p1", "j1");

        var workers = registry.ListAll();
        Assert.Equal(2, workers.Count);
        Assert.Equal("worker-a", workers[0].Id);
        Assert.Equal("worker-b", workers[1].Id);
    }

    // Cada contenedor recreado trae un hostname nuevo: sin purga, el Dashboard mostraba para
    // siempre workers que ya no existen. La vida se mide por la fecha del archivo, que el
    // worker refresca (Touch) aunque esté ocioso.
    [Fact]
    public void ListAll_OmiteYBorraWorkersSinSenalDeVida()
    {
        var registry = new WorkerRegistry(Options.Create(new AppOptions { DataDir = _dataDir }));
        registry.SetIdle("worker-vivo", PipelineStage.Ingest);
        registry.SetIdle("worker-muerto", PipelineStage.Sql);
        var deadFile = Path.Combine(_dataDir, "workers", "worker-muerto.json");
        File.SetLastWriteTimeUtc(deadFile, DateTime.UtcNow - WorkerRegistry.StaleAfter - TimeSpan.FromMinutes(1));

        var ids = registry.ListAll().Select(w => w.Id).ToList();

        Assert.Equal(["worker-vivo"], ids);
        Assert.False(File.Exists(deadFile));
    }

    [Fact]
    public void Touch_MantieneVivoAUnWorkerOcioso()
    {
        var registry = new WorkerRegistry(Options.Create(new AppOptions { DataDir = _dataDir }));
        registry.SetIdle("worker-ocioso", PipelineStage.Reconcile);
        var file = Path.Combine(_dataDir, "workers", "worker-ocioso.json");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - WorkerRegistry.StaleAfter - TimeSpan.FromMinutes(1));

        registry.Touch("worker-ocioso");

        Assert.Equal(["worker-ocioso"], registry.ListAll().Select(w => w.Id));
    }

    // Tras recrear los contenedores, el Panel mostraba los workers viejos (Libre/Estancado)
    // hasta la purga de 10 min. ListAlive los oculta en cuanto pierden tres señales, sin
    // borrarlos: ListAll y su purga siguen igual para el resto de consumidores.
    [Fact]
    public void ListAlive_OcultaSinBorrarLosWorkersSinSenalReciente()
    {
        var registry = new WorkerRegistry(Options.Create(new AppOptions { DataDir = _dataDir }));
        registry.SetIdle("worker-vivo", PipelineStage.Ingest);
        registry.SetIdle("worker-caido", PipelineStage.Sql);
        var within = WorkerRegistry.AliveWithin(120);
        var downFile = Path.Combine(_dataDir, "workers", "worker-caido.json");
        File.SetLastWriteTimeUtc(downFile, DateTime.UtcNow - within - TimeSpan.FromSeconds(5));

        Assert.Equal(["worker-vivo"], registry.ListAlive(within).Select(w => w.Id));
        Assert.True(File.Exists(downFile));
        Assert.Equal(["worker-caido", "worker-vivo"], registry.ListAll().Select(w => w.Id));
    }

    [Theory]
    [InlineData(30, 90)]
    [InlineData(120, 120)]
    [InlineData(600, 600)]
    public void AliveWithin_EsTresLatidosPerdidos(int leaseSeconds, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), WorkerRegistry.AliveWithin(leaseSeconds));
    }
}
