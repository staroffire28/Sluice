using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Janitor de retención: acota el crecimiento del almacenamiento file-based purgando
// jobs antiguos y todos sus artefactos derivados (runs, reportes, tareas terminadas,
// staging huérfano). El job es la ÚNICA unidad de retención: lo demás se purga cuando
// su jobId deja de existir, así el piso-K y la consistencia (nunca un job sin su PDF,
// ni un reporte apuntando a un job ya borrado) salen gratis.
public sealed class RetentionService
{
    // Piso de seguridad: conservar siempre los últimos K jobs por pipeline aunque
    // excedan la antigüedad, para no vaciar el histórico de un pipeline poco activo.
    public const int KeepLastPerPipeline = 50;

    // Un artefacto cuyo jobId no tiene job.json puede ser de un job que se está creando
    // (el artefacto aparece antes que su job): solo se purga pasado este margen.
    public static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(1);

    // El staging de un job terminado en Error o Cancelled ya no lo lee nadie (un reintento es otro
    // job): se purga pasado este margen aunque el job siga dentro de la retención.
    public static readonly TimeSpan FailedStagingRetention = TimeSpan.FromHours(24);

    private readonly AppOptions _options;
    private readonly IActivityLog _activity;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IOptions<AppOptions> options,
        IActivityLog activity,
        ILogger<RetentionService> logger)
    {
        _options = options.Value;
        _activity = activity;
        _logger = logger;
    }

    // Decide qué borrar dado "ahora" + settings. Lee el FS pero NO borra: testeable
    // con un DataDir temporal y un "ahora" inyectado, sin reloj real ni efectos.
    public RetentionPlan Plan(DateTimeOffset now, AppSettings settings)
    {
        // RetentionDays <= 0 desactiva la purga: no se borra nada.
        if (settings.RetentionDays <= 0)
        {
            return new RetentionPlan();
        }

        var cutoff = now - TimeSpan.FromDays(settings.RetentionDays);
        var (jobs, unreadableJobIds) = LoadJobs();
        // Un job ilegible (corrupto o a medio escribir) se conserva con todos sus artefactos:
        // sin poder leerlo no se sabe si está vivo, y su id sale del nombre del archivo.
        var survivingJobIds = new HashSet<string>(unreadableJobIds, StringComparer.OrdinalIgnoreCase);
        var knownJobIds = new HashSet<string>(unreadableJobIds, StringComparer.OrdinalIgnoreCase);
        knownJobIds.UnionWith(jobs.Select(j => j.Id));
        var orphans = new OrphanFilter(survivingJobIds, knownJobIds, now - OrphanGracePeriod);
        var purgedJobFiles = new List<string>();

        foreach (var group in jobs.GroupBy(j => j.PipelineId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(j => j.FinishedAt ?? j.StartedAt)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var job = ordered[i];
                // Conservar: jobs en curso, los K más recientes del pipeline, y los
                // más nuevos que el cutoff. Todo lo demás se purga.
                var keep = JobProgressHelper.IsActive(job)
                    || i < KeepLastPerPipeline
                    || (job.FinishedAt ?? job.StartedAt) >= cutoff;

                if (keep)
                {
                    survivingJobIds.Add(job.Id);
                }
                else
                {
                    purgedJobFiles.Add(Path.Combine(JobsDir, $"{job.Id}.json"));
                }
            }
        }

        return new RetentionPlan
        {
            PurgedJobCount = purgedJobFiles.Count,
            JobFiles = purgedJobFiles,
            ReportFiles = OrphanReportFiles(orphans),
            TaskFiles = OrphanTerminalTaskFiles(orphans),
            RunDirectories = OrphanRunDirectories(orphans),
            StagingDirectories = OrphanStagingDirectories(orphans, FailedJobIds(jobs, now)),
        };
    }

    // Ejecuta el plan con borrado defensivo (archivos corruptos/bloqueados se ignoran
    // sin tumbar el barrido) y registra el resultado en el log de actividad.
    public RetentionSweepResult Sweep(DateTimeOffset now, AppSettings settings)
    {
        var plan = Plan(now, settings);

        var result = new RetentionSweepResult
        {
            Jobs = DeleteFiles(plan.JobFiles),
            Reports = DeleteFiles(plan.ReportFiles),
            Tasks = DeleteFiles(plan.TaskFiles),
            Runs = DeleteDirectories(plan.RunDirectories),
            Staging = DeleteDirectories(plan.StagingDirectories),
        };

        if (result.Total > 0)
        {
            _activity.LogSystem(
                "retention.sweep",
                $"Retención: {result.Jobs} jobs, {result.Reports} reportes, {result.Tasks} tareas, " +
                $"{result.Runs} runs, {result.Staging} staging eliminados " +
                $"(antigüedad {settings.RetentionDays}d, piso {KeepLastPerPipeline}/pipeline).");
        }

        return result;
    }

    private string JobsDir => Path.Combine(_options.DataDir, "jobs");

    private (List<Job> Jobs, List<string> UnreadableIds) LoadJobs()
    {
        if (!Directory.Exists(JobsDir))
        {
            return ([], []);
        }

        var jobs = new List<Job>();
        var unreadable = new List<string>();
        foreach (var file in Directory.GetFiles(JobsDir, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var job = JsonSerializer.Deserialize<Job>(stream, JsonDefaults.Options);
                if (job is not null)
                {
                    jobs.Add(job);
                    continue;
                }
            }
            catch
            {
                // Corrupto o bloqueado: no tumba el barrido (se conserva abajo).
            }

            unreadable.Add(Path.GetFileNameWithoutExtension(file));
        }

        return (jobs, unreadable);
    }

    // reports/{reconcile,diff,remediation,values}: cada archivo lleva su JobId en el contenido.
    // NO se recorre reports/recon-ack (decisiones del usuario, fuera de retención).
    private List<string> OrphanReportFiles(OrphanFilter orphans)
    {
        var result = new List<string>();
        foreach (var sub in new[] { "reconcile", "diff", "remediation", "values" })
        {
            CollectOrphansByJobId(Path.Combine(_options.DataDir, "reports", sub), orphans, result);
        }

        return result;
    }

    // "done" y "error" son ambas terminales: una tarea fallida cuyo job ya fue purgado
    // no debe acumularse indefinidamente en tasks/{stage}/error/ (antes solo se purgaba "done").
    private List<string> OrphanTerminalTaskFiles(OrphanFilter orphans)
    {
        var result = new List<string>();
        foreach (PipelineStage stage in Enum.GetValues<PipelineStage>())
        {
            foreach (var terminal in new[] { "done", "error" })
            {
                var dir = Path.Combine(_options.DataDir, "tasks", stage.ToString().ToLowerInvariant(), terminal);
                CollectOrphansByJobId(dir, orphans, result);
            }
        }

        return result;
    }

    // Directorios de corrida {pipelineId}/runs/{jobId}: un pipeline es cualquier dir
    // top-level del DataDir que contenga un subdirectorio "runs". Cubre tanto los runs
    // del job recién purgado como residuos antiguos sin job vivo.
    private List<string> OrphanRunDirectories(OrphanFilter orphans)
    {
        var result = new List<string>();
        if (!Directory.Exists(_options.DataDir))
        {
            return result;
        }

        foreach (var pipelineDir in Directory.GetDirectories(_options.DataDir))
        {
            var runsDir = Path.Combine(pipelineDir, "runs");
            if (!Directory.Exists(runsDir))
            {
                continue;
            }

            foreach (var runDir in Directory.GetDirectories(runsDir))
            {
                if (orphans.IsPurgeable(Path.GetFileName(runDir), Directory.GetLastWriteTimeUtc(runDir)))
                {
                    result.Add(runDir);
                }
            }
        }

        return result;
    }

    // Jobs terminados en Error o Cancelled hace más de FailedStagingRetention: su staging sobra.
    private static HashSet<string> FailedJobIds(List<Job> jobs, DateTimeOffset now) =>
        jobs.Where(j => j.Status is JobStatus.Error or JobStatus.Cancelled
                && (j.FinishedAt ?? j.StartedAt) <= now - FailedStagingRetention)
            .Select(j => j.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private List<string> OrphanStagingDirectories(OrphanFilter orphans, HashSet<string> failedJobIds)
    {
        var result = new List<string>();
        var stagingRoot = Path.Combine(_options.DataDir, "staging");
        if (!Directory.Exists(stagingRoot))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(stagingRoot))
        {
            var jobId = Path.GetFileName(dir);
            if (failedJobIds.Contains(jobId) || orphans.IsPurgeable(jobId, Directory.GetLastWriteTimeUtc(dir)))
            {
                result.Add(dir);
            }
        }

        return result;
    }

    private void CollectOrphansByJobId(string dir, OrphanFilter orphans, List<string> sink)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            string? jobId;
            try
            {
                using var stream = File.OpenRead(file);
                jobId = JsonSerializer.Deserialize<JobIdProbe>(stream, JsonDefaults.Options)?.JobId;
            }
            catch
            {
                // No identificable: por seguridad no se borra.
                continue;
            }

            // Sin JobId no se puede atar a un job: se conserva por seguridad.
            if (!string.IsNullOrEmpty(jobId) && orphans.IsPurgeable(jobId, File.GetLastWriteTimeUtc(file)))
            {
                sink.Add(file);
            }
        }
    }

    private int DeleteFiles(IReadOnlyList<string> paths)
    {
        var deleted = 0;
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Retención: no se pudo borrar archivo {Path}", path);
            }
        }

        return deleted;
    }

    private int DeleteDirectories(IReadOnlyList<string> paths)
    {
        var deleted = 0;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Retención: no se pudo borrar directorio {Path}", path);
            }
        }

        return deleted;
    }

    // Un artefacto se purga si su job no sobrevive y, además, o su job existe (se está
    // purgando ahora) o el artefacto es más viejo que el periodo de gracia.
    private sealed record OrphanFilter(
        HashSet<string> Surviving,
        HashSet<string> Known,
        DateTimeOffset GraceCutoff)
    {
        public bool IsPurgeable(string jobId, DateTime lastWriteUtc) =>
            !Surviving.Contains(jobId)
            && (Known.Contains(jobId) || new DateTimeOffset(lastWriteUtc, TimeSpan.Zero) <= GraceCutoff);
    }

    // Sonda mínima para extraer solo el JobId de cualquier artefacto (reporte/tarea)
    // sin acoplarse a su tipo concreto. System.Text.Json ignora las demás propiedades.
    private sealed record JobIdProbe
    {
        public string? JobId { get; init; }
    }
}

public sealed record RetentionPlan
{
    public int PurgedJobCount { get; init; }
    public IReadOnlyList<string> JobFiles { get; init; } = [];
    public IReadOnlyList<string> ReportFiles { get; init; } = [];
    public IReadOnlyList<string> TaskFiles { get; init; } = [];
    public IReadOnlyList<string> RunDirectories { get; init; } = [];
    public IReadOnlyList<string> StagingDirectories { get; init; } = [];
}

public sealed record RetentionSweepResult
{
    public int Jobs { get; init; }
    public int Reports { get; init; }
    public int Tasks { get; init; }
    public int Runs { get; init; }
    public int Staging { get; init; }
    public int Total => Jobs + Reports + Tasks + Runs + Staging;
}
