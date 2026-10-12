using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Cubre el janitor de retención: el job es la unidad de purga y sus artefactos (runs,
// reportes, tareas done, staging) lo siguen. Usa un DataDir temporal y un "ahora"
// inyectado, sin reloj real ni FS de producción.
public sealed class RetentionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int K = RetentionService.KeepLastPerPipeline; // 50

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "retention-" + Guid.NewGuid().ToString("N"));
    private readonly JobStore _jobs;
    private readonly ReportStore _reports;
    private readonly RetentionService _service;

    public RetentionServiceTests()
    {
        DataDirectoryInitializer.EnsureLayout(_dir);
        var opts = Options.Create(new AppOptions { DataDir = _dir });
        _jobs = new JobStore(opts);
        _reports = new ReportStore(opts);
        _service = new RetentionService(opts, new ActivityLogStore(opts), NullLogger<RetentionService>.Instance);
    }

    private static AppSettings Settings(int days = 90) => new() { RetentionDays = days };

    private void WriteJob(string id, string pipelineId, DateTimeOffset when, JobStatus status = JobStatus.Ok) =>
        _jobs.Save(new Job
        {
            Id = id,
            PipelineId = pipelineId,
            Status = status,
            StartedAt = when,
            FinishedAt = status == JobStatus.Running ? null : when,
        });

    private string WriteRun(string pipelineId, string jobId)
    {
        var dir = Path.Combine(_dir, pipelineId, "runs", jobId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ingest_manifest.json"), "{}");
        return dir;
    }

    private string WriteStaging(string jobId)
    {
        var dir = Path.Combine(_dir, "staging", jobId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "00000.parquet"), "x");
        return dir;
    }

    private void WriteDoneTask(string jobId, string pipelineId, PipelineStage stage = PipelineStage.Ingest) =>
        WriteTerminalTask(jobId, pipelineId, stage, "done", PipelineTaskStatus.Done);

    private void WriteErrorTask(string jobId, string pipelineId, PipelineStage stage = PipelineStage.Ingest) =>
        WriteTerminalTask(jobId, pipelineId, stage, "error", PipelineTaskStatus.Error);

    private void WriteTerminalTask(string jobId, string pipelineId, PipelineStage stage, string terminal, PipelineTaskStatus status)
    {
        var task = new PipelineTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Stage = stage,
            PipelineId = pipelineId,
            JobId = jobId,
            Status = status,
            FinishedAt = Now,
        };
        var dir = Path.Combine(_dir, "tasks", stage.ToString().ToLowerInvariant(), terminal);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{task.Id}.json"), JsonSerializer.Serialize(task, JsonDefaults.Options));
    }

    private int JobFileCount() => Directory.GetFiles(Path.Combine(_dir, "jobs"), "*.json").Length;

    private static string DoneTaskFile(string dir) => Directory.GetFiles(dir, "*.json").Single();

    // Un artefacto sin job solo se purga pasado el periodo de gracia (podría ser de un job
    // que se está creando): los "huérfanos viejos" de los tests se fechan antes de Now.
    private static void Age(string path)
    {
        var old = Now.AddDays(-2).UtcDateTime;
        if (Directory.Exists(path))
        {
            Directory.SetLastWriteTimeUtc(path, old);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, old);
        }
    }

    private void AgeAll(string relativeDir)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(_dir, relativeDir), "*.json"))
        {
            Age(file);
        }
    }

    // --- Piso K: conserva siempre los últimos K aunque sean viejos ---

    [Fact]
    public void PisoK_ConservaUltimosK_AunqueTodosViejos()
    {
        // K+3 jobs, todos más viejos que el umbral (fechas escalonadas para determinismo).
        for (var i = 0; i < K + 3; i++)
        {
            WriteJob($"old-{i:D3}", "p1", Now.AddDays(-200 - i));
        }

        var result = _service.Sweep(Now, Settings());

        Assert.Equal(3, result.Jobs);          // solo los 3 más viejos
        Assert.Equal(K, JobFileCount());       // se conservan exactamente K
        Assert.False(_jobs.Exists("old-052")); // el más viejo, purgado
        Assert.True(_jobs.Exists("old-000"));  // el más reciente, conservado
    }

    [Fact]
    public void NuevosFueraDePiso_SeConservanPorUmbral()
    {
        // K+5 jobs, todos recientes (dentro del umbral): ninguno se purga aunque
        // 5 queden fuera del piso K.
        for (var i = 0; i < K + 5; i++)
        {
            WriteJob($"new-{i:D3}", "p1", Now.AddDays(-1 - i));
        }

        var result = _service.Sweep(Now, Settings());

        Assert.Equal(0, result.Jobs);
        Assert.Equal(K + 5, JobFileCount());
    }

    [Fact]
    public void UmbralYPiso_PurgaViejosFueraDePiso_ConservaNuevos()
    {
        // 50 nuevos (llenan el piso) + 5 viejos fuera de piso y fuera de umbral.
        for (var i = 0; i < K; i++)
        {
            WriteJob($"new-{i:D3}", "p1", Now.AddDays(-1 - i));
        }
        for (var i = 0; i < 5; i++)
        {
            WriteJob($"old-{i:D3}", "p1", Now.AddDays(-200 - i));
        }

        var result = _service.Sweep(Now, Settings());

        Assert.Equal(5, result.Jobs);
        Assert.Equal(K, JobFileCount());
        Assert.False(_jobs.Exists("old-000"));
        Assert.True(_jobs.Exists("new-000"));
    }

    // --- Exclusiones: Running, recon-ack, state ---

    [Fact]
    public void NoPurga_Running_NiReconAck_NiState()
    {
        // K+1 jobs done viejos + un Running aún más viejo (el más antiguo de todos).
        // Sin la regla Running se purgaría (fuera de piso y de umbral).
        for (var i = 0; i < K + 1; i++)
        {
            WriteJob($"old-{i:D3}", "p1", Now.AddDays(-100 - i));
        }
        WriteJob("running", "p1", Now.AddDays(-500), JobStatus.Running);
        // Partial sin FinishedAt: ingest parcial que sigue con SQL/Reconcile, también vivo.
        _jobs.Save(new Job { Id = "partial-live", PipelineId = "p1", Status = JobStatus.Partial, StartedAt = Now.AddDays(-501) });

        _reports.SaveAck("p1", "account", new ReconAck { AckPct = 1.0 });
        var stateDir = Path.Combine(_dir, "p1", "state");
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, "state.json"), "{}");

        _service.Sweep(Now, Settings());

        Assert.True(_jobs.Exists("running"));                                            // Running intacto
        Assert.True(_jobs.Exists("partial-live"));                                       // Partial en curso intacto
        Assert.True(File.Exists(Path.Combine(_dir, "reports", "recon-ack", "p1.json"))); // ack intacto
        Assert.True(File.Exists(Path.Combine(stateDir, "state.json")));                  // state intacto
    }

    // --- Cascada: runs y staging siguen al job ---

    [Fact]
    public void PurgaRunYStaging_DelJobPurgado_ConservaLosDelVivo()
    {
        for (var i = 0; i < K; i++)
        {
            WriteJob($"new-{i:D3}", "p1", Now.AddDays(-1 - i));
        }
        WriteJob("old", "p1", Now.AddDays(-300));

        var oldRun = WriteRun("p1", "old");
        var oldStaging = WriteStaging("old");
        var keepRun = WriteRun("p1", "new-000");
        var keepStaging = WriteStaging("new-000");
        var orphanStaging = WriteStaging("ghost"); // jobId sin job alguno
        Age(orphanStaging);

        var result = _service.Sweep(Now, Settings());

        Assert.False(Directory.Exists(oldRun));
        Assert.False(Directory.Exists(oldStaging));
        Assert.False(Directory.Exists(orphanStaging));
        Assert.True(Directory.Exists(keepRun));
        Assert.True(Directory.Exists(keepStaging));
        Assert.Equal(1, result.Runs);    // solo oldRun (keepRun queda; ghost no tiene run)
        Assert.Equal(2, result.Staging); // oldStaging + orphanStaging (ghost)
    }

    // --- Reportes y tareas: huérfanos se purgan, los de un job vivo se conservan ---

    [Fact]
    public void ReportesYTareas_HuerfanosSePurgan_DelVivoSeConservan()
    {
        WriteJob("alive", "p1", Now.AddDays(-1)); // vivo (reciente)

        _reports.SaveReconcile(new ReconcileReport { Id = "recon-alive", PipelineId = "p1", JobId = "alive" });
        _reports.SaveDiff(new DiffReport { Id = "diff-alive", PipelineId = "p1", JobId = "alive" });
        var aliveTaskDir = Path.Combine(_dir, "tasks", "ingest", "done");
        WriteDoneTask("alive", "p1");

        _reports.SaveReconcile(new ReconcileReport { Id = "recon-ghost", PipelineId = "p1", JobId = "ghost" });
        _reports.SaveDiff(new DiffReport { Id = "diff-ghost", PipelineId = "p1", JobId = "ghost" });
        _reports.SaveRemediation(new ReconcileRemediation { Id = "rem1", PipelineId = "p1", Entity = "account", JobId = "ghost" });
        WriteDoneTask("ghost", "p1", PipelineStage.Sql);
        AgeAll("reports/reconcile");
        AgeAll("reports/diff");
        AgeAll("reports/remediation");
        AgeAll("tasks/sql/done");

        var result = _service.Sweep(Now, Settings());

        // Huérfanos (jobId 'ghost' sin job) purgados.
        Assert.False(File.Exists(Path.Combine(_dir, "reports", "reconcile", "recon-ghost.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "reports", "diff", "diff-ghost.json")));
        Assert.False(File.Exists(Path.Combine(_dir, "reports", "remediation", "rem1.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tasks", "sql", "done"), "*.json"));

        // Del job vivo, conservados.
        Assert.True(File.Exists(Path.Combine(_dir, "reports", "reconcile", "recon-alive.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "reports", "diff", "diff-alive.json")));
        Assert.Single(Directory.GetFiles(aliveTaskDir, "*.json"));

        Assert.Equal(3, result.Reports); // recon-ghost + diff-ghost + rem1
        Assert.Equal(1, result.Tasks);   // ghost (sql)
        Assert.Equal(0, result.Jobs);    // 'alive' es reciente
    }

    // --- tasks/{stage}/error/: mismo tratamiento que done/ (antes solo se purgaba done) ---

    [Fact]
    public void ErrorTasks_HuerfanasSePurgan_DelVivoSeConservan()
    {
        WriteJob("alive", "p1", Now.AddDays(-1)); // vivo (reciente)

        var aliveErrorDir = Path.Combine(_dir, "tasks", "ingest", "error");
        WriteErrorTask("alive", "p1");
        WriteErrorTask("ghost", "p1", PipelineStage.Sql);
        AgeAll("tasks/sql/error");

        var result = _service.Sweep(Now, Settings());

        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tasks", "sql", "error"), "*.json"));
        Assert.Single(Directory.GetFiles(aliveErrorDir, "*.json"));
        Assert.Equal(1, result.Tasks); // solo la de 'ghost' (sql)
    }

    // --- Interruptor: RetentionDays <= 0 no borra nada ---

    [Fact]
    public void RetentionDeshabilitada_NoBorraNada()
    {
        for (var i = 0; i < K + 10; i++)
        {
            WriteJob($"old-{i:D3}", "p1", Now.AddDays(-1000 - i));
        }
        WriteRun("p1", "old-000");
        WriteStaging("old-000");
        _reports.SaveReconcile(new ReconcileReport { Id = "recon-ghost", PipelineId = "p1", JobId = "ghost" });

        var result = _service.Sweep(Now, Settings(days: 0));

        Assert.Equal(0, result.Total);
        Assert.Equal(K + 10, JobFileCount());
        Assert.True(Directory.Exists(Path.Combine(_dir, "p1", "runs", "old-000")));
        Assert.True(File.Exists(Path.Combine(_dir, "reports", "reconcile", "recon-ghost.json")));
    }

    // --- Un job ilegible (corrupto o a medio escribir) no deja huérfanos sus artefactos ---

    [Fact]
    public void JobIlegible_ConservaElJobYSusArtefactos()
    {
        File.WriteAllText(Path.Combine(_dir, "jobs", "broken.json"), "{ esto no es json");
        var run = WriteRun("p1", "broken");
        var staging = WriteStaging("broken");
        _reports.SaveReconcile(new ReconcileReport { Id = "recon-broken", PipelineId = "p1", JobId = "broken" });
        Age(run);
        Age(staging);
        AgeAll("reports/reconcile");

        var result = _service.Sweep(Now, Settings());

        Assert.Equal(0, result.Total);
        Assert.True(File.Exists(Path.Combine(_dir, "jobs", "broken.json")));
        Assert.True(Directory.Exists(run));
        Assert.True(Directory.Exists(staging));
        Assert.True(File.Exists(Path.Combine(_dir, "reports", "reconcile", "recon-broken.json")));
    }

    // --- Un artefacto reciente sin job puede ser de un job que se está creando: se conserva ---

    [Fact]
    public void HuerfanoReciente_SinJob_NoSePurga_ElViejoSi()
    {
        var recent = WriteStaging("being-created");
        Directory.SetLastWriteTimeUtc(recent, Now.AddMinutes(-5).UtcDateTime);
        var old = WriteStaging("ghost");
        Age(old);

        var result = _service.Sweep(Now, Settings());

        Assert.True(Directory.Exists(recent));
        Assert.False(Directory.Exists(old));
        Assert.Equal(1, result.Staging);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
