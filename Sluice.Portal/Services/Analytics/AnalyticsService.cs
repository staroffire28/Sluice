using System.Text.Json;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Analytics;

// Servicio de analítica: agrega el historial de jobs y el estado actual de la
// orquestación para alimentar los gráficos de la página /analytics.
// Todo es robusto a Summary nulo o claves ausentes; nunca lanza.

public interface IAnalyticsService
{
    // pipelineId: solo los jobs de ese pipeline. La cola y los workers son compartidos por todos los
    // pipelines y no se filtran. zone: en qué zona se toman los días (la del navegador; sin ella, UTC).
    AnalyticsSnapshot GetSnapshot(string? pipelineId = null, TimeZoneInfo? zone = null);

    // Reconstruye el crecimiento de una tabla desde el historial de Job.Entities — no hay
    // snapshots diarios dedicados, pero SqlRows (COUNT(*) real post-MERGE, ver SqlExecutor) y
    // CsvRows (filas ingeridas en cada run) ya son series temporales genuinas por job.
    TableGrowthReport GetTableGrowth(string pipelineId, string entity, int days = 90, TimeZoneInfo? zone = null);
}

// ---- DTOs expuestos para los gráficos ----

// Punto genérico etiqueta/valor para series de barra, donut y línea.
public sealed record LabeledValue(string Label, double Value);

// Punto de la serie temporal "jobs por día" (separa éxito vs error).
public sealed record DailyJobCount(DateOnly Date, int Success, int Error, int Total);

// Punto de la serie temporal de divergencia máxima.
public sealed record DivergencePoint(DateTimeOffset At, double MaxDivergencePct);

// Profundidad de cola por etapa (pending/claimed/done/error).
public sealed record StageQueueDepth(string Stage, int Pending, int Claimed, int Done, int Error);

// Resumen del estado de los workers.
public sealed record WorkerSummary(int Busy, int Idle, int Total);

// Punto de la serie de crecimiento (tamaño real de la tabla en SQL en ese día).
public sealed record TableGrowthPoint(DateOnly Date, long RowCount);

// Punto de la serie de carga diaria (filas ingeridas ese día, no acumuladas).
public sealed record TableLoadPoint(DateOnly Date, long CsvRows);

// Un día entre los de mayor carga en la ventana consultada.
public sealed record TableSpikeDay(DateOnly Date, long CsvRows);

public sealed record TableGrowthReport
{
    public required string PipelineId { get; init; }
    public required string Entity { get; init; }
    public int Days { get; init; }
    public IReadOnlyList<TableGrowthPoint> Growth { get; init; } = [];
    public IReadOnlyList<TableLoadPoint> DailyLoad { get; init; } = [];
    public IReadOnlyList<TableSpikeDay> TopSpikeDays { get; init; } = [];
    public long CurrentRowCount { get; init; }
    public long TotalRowsLoadedInWindow { get; init; }
}

public sealed record AnalyticsSnapshot
{
    // KPIs
    public int TotalJobs { get; init; }
    public double SuccessRatePct { get; init; }
    public double CurrentMaxDivergencePct { get; init; }
    public int WorkersBusy { get; init; }
    public int WorkersIdle { get; init; }
    public int TotalQueueDepth { get; init; }

    // Gráficos
    public IReadOnlyList<LabeledValue> StatusDistribution { get; init; } = [];
    public IReadOnlyList<DailyJobCount> JobsPerDay { get; init; } = [];
    public IReadOnlyList<LabeledValue> MergedRowsByPipeline { get; init; } = [];
    public IReadOnlyList<DivergencePoint> DivergenceOverTime { get; init; } = [];
    public IReadOnlyList<LabeledValue> AvgDurationByType { get; init; } = [];
    public IReadOnlyList<StageQueueDepth> QueueDepths { get; init; } = [];
    public WorkerSummary Workers { get; init; } = new(0, 0, 0);
}

public sealed class AnalyticsService : IAnalyticsService
{
    private const int HistorySize = 200;
    private const int DivergencePoints = 30;
    private const int DaysWindow = 14;

    private readonly IJobService _jobs;
    private readonly IOrchestrationService _orchestration;
    private readonly IPipelineService _pipelines;

    public AnalyticsService(
        IJobService jobs,
        IOrchestrationService orchestration,
        IPipelineService pipelines)
    {
        _jobs = jobs;
        _orchestration = orchestration;
        _pipelines = pipelines;
    }

    public AnalyticsSnapshot GetSnapshot(string? pipelineId = null, TimeZoneInfo? zone = null)
    {
        var jobs = LoadRecentJobs(string.IsNullOrWhiteSpace(pipelineId) ? null : pipelineId);

        return new AnalyticsSnapshot
        {
            TotalJobs = jobs.Count,
            SuccessRatePct = ComputeSuccessRate(jobs),
            CurrentMaxDivergencePct = ComputeCurrentMaxDivergence(jobs),
            WorkersBusy = CountWorkers(WorkerState.Busy),
            WorkersIdle = CountWorkers(WorkerState.Idle),
            TotalQueueDepth = ComputeTotalQueueDepth(),

            StatusDistribution = BuildStatusDistribution(jobs),
            JobsPerDay = BuildJobsPerDay(jobs, zone),
            MergedRowsByPipeline = BuildMergedRowsByPipeline(jobs),
            DivergenceOverTime = BuildDivergenceOverTime(jobs),
            AvgDurationByType = BuildAvgDurationByType(jobs),
            QueueDepths = BuildQueueDepths(),
            Workers = BuildWorkerSummary(),
        };
    }

    public TableGrowthReport GetTableGrowth(string pipelineId, string entity, int days = 90, TimeZoneInfo? zone = null)
    {
        var windowDays = Math.Max(1, days);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-windowDays);

        // pageSize alto en vez de paginar: mismo costo de I/O (JsonFileStore.ListAll siempre
        // relee el directorio completo), y evita reintroducir la lógica de acumulación por
        // páginas de LoadRecentJobs para un caso que además ya filtra por pipelineId.
        var jobsInWindow = _jobs.List(page: 1, pageSize: int.MaxValue, pipelineId: pipelineId)
            .Where(j => j.StartedAt >= cutoff)
            .OrderBy(j => j.StartedAt)
            .ToList();

        var perJobEntity = jobsInWindow
            .Select(j => (Job: j, Entry: j.Entities.FirstOrDefault(e =>
                string.Equals(e.Entity, entity, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Entry is not null)
            .ToList();

        // Crecimiento: SqlRows (COUNT(*) real) del último job del día — si hubo 2 corridas ese
        // día, se queda con la de fin de día porque perJobEntity ya está ordenado ascendente y
        // el Dictionary sobreescribe con el último valor visto por clave.
        var growthByDay = new Dictionary<DateOnly, long>();
        foreach (var (job, entry) in perJobEntity)
        {
            if (entry!.SqlRows is { } rows)
            {
                growthByDay[Day(job.StartedAt, zone)] = rows;
            }
        }

        var growth = growthByDay
            .OrderBy(kv => kv.Key)
            .Select(kv => new TableGrowthPoint(kv.Key, kv.Value))
            .ToList();

        // Carga: suma de CsvRows (filas ingeridas, no acumuladas) agrupada por día — proxy
        // directo de "días con más picos de carga".
        var loadByDay = perJobEntity
            .GroupBy(x => Day(x.Job.StartedAt, zone))
            .ToDictionary(g => g.Key, g => g.Sum(x => (long)x.Entry!.CsvRows));

        var dailyLoad = loadByDay
            .OrderBy(kv => kv.Key)
            .Select(kv => new TableLoadPoint(kv.Key, kv.Value))
            .ToList();

        var topSpikes = loadByDay
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(5)
            .Select(kv => new TableSpikeDay(kv.Key, kv.Value))
            .ToList();

        return new TableGrowthReport
        {
            PipelineId = pipelineId,
            Entity = entity,
            Days = windowDays,
            Growth = growth,
            DailyLoad = dailyLoad,
            TopSpikeDays = topSpikes,
            CurrentRowCount = growth.Count > 0 ? growth[^1].RowCount : 0,
            TotalRowsLoadedInWindow = loadByDay.Values.Sum(),
        };
    }

    // Lista hasta HistorySize jobs recientes (de un pipeline o de todos) paginando con List();
    // ordena por fecha desc.
    private List<Job> LoadRecentJobs(string? pipelineId)
    {
        var collected = new List<Job>();
        const int pageSize = 50;
        var maxPages = (HistorySize / pageSize) + 1;

        for (var page = 1; page <= maxPages && collected.Count < HistorySize; page++)
        {
            var batch = _jobs.List(page: page, pageSize: pageSize, pipelineId: pipelineId);
            if (batch.Count == 0)
            {
                break;
            }

            collected.AddRange(batch);
        }

        return collected
            .Take(HistorySize)
            .OrderByDescending(j => j.StartedAt)
            .ToList();
    }

    // ---- KPIs ----

    private static double ComputeSuccessRate(IReadOnlyCollection<Job> jobs)
    {
        var finished = jobs.Where(IsTerminal).ToList();
        if (finished.Count == 0)
        {
            return 0;
        }

        var ok = finished.Count(j => j.Status is JobStatus.Ok or JobStatus.Partial);
        return Math.Round(ok * 100.0 / finished.Count, 1);
    }

    private static double ComputeCurrentMaxDivergence(IEnumerable<Job> jobs)
    {
        // Toma la divergencia del job más reciente que la reporte.
        foreach (var job in jobs)
        {
            var value = ReadDouble(job.Summary, "maxDivergencePct");
            if (value is not null)
            {
                return Math.Round(value.Value, 2);
            }
        }

        return 0;
    }

    private int CountWorkers(WorkerState state)
    {
        try
        {
            return _orchestration.GetWorkers().Count(w => w.Status == state);
        }
        catch
        {
            return 0;
        }
    }

    private int ComputeTotalQueueDepth()
    {
        try
        {
            var snap = _orchestration.GetQueueSnapshot();
            return Depth(snap.Ingest) + Depth(snap.Sql) + Depth(snap.Reconcile);
        }
        catch
        {
            return 0;
        }

        static int Depth(StageQueueCounts c) => c.Pending + c.Claimed;
    }

    // ---- Gráficos ----

    private static IReadOnlyList<LabeledValue> BuildStatusDistribution(IEnumerable<Job> jobs)
    {
        var byStatus = jobs
            .GroupBy(j => j.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        // Mantiene un orden estable usando todos los valores del enum.
        return Enum.GetValues<JobStatus>()
            .Select(s => new LabeledValue(s.ToString(), byStatus.TryGetValue(s, out var n) ? n : 0))
            .Where(lv => lv.Value > 0)
            .ToList();
    }

    private static IReadOnlyList<DailyJobCount> BuildJobsPerDay(IEnumerable<Job> jobs, TimeZoneInfo? zone)
    {
        var today = Day(DateTimeOffset.UtcNow, zone);
        var startDay = today.AddDays(-(DaysWindow - 1));

        var grouped = jobs
            .Where(j => Day(j.StartedAt, zone) >= startDay)
            .GroupBy(j => Day(j.StartedAt, zone))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<DailyJobCount>(DaysWindow);
        for (var i = 0; i < DaysWindow; i++)
        {
            var day = startDay.AddDays(i);
            if (grouped.TryGetValue(day, out var dayJobs))
            {
                var error = dayJobs.Count(j => j.Status is JobStatus.Error or JobStatus.Blocked);
                // Un Partial sin FinishedAt sigue en curso: no cuenta como éxito (como en IsTerminal).
                var success = dayJobs.Count(j => IsTerminal(j) && j.Status is JobStatus.Ok or JobStatus.Partial);
                result.Add(new DailyJobCount(day, success, error, dayJobs.Count));
            }
            else
            {
                result.Add(new DailyJobCount(day, 0, 0, 0));
            }
        }

        return result;
    }

    private IReadOnlyList<LabeledValue> BuildMergedRowsByPipeline(IEnumerable<Job> jobs)
    {
        var names = PipelineNames();

        var sums = jobs
            .Select(j => (j.PipelineId, Rows: ReadLong(j.Summary, "sqlMergedRows") ?? 0))
            .Where(x => x.Rows > 0)
            .GroupBy(x => x.PipelineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Rows));

        return sums
            .Select(kv => new LabeledValue(
                names.TryGetValue(kv.Key, out var name) ? name : kv.Key,
                kv.Value))
            .OrderByDescending(lv => lv.Value)
            .ToList();
    }

    private static IReadOnlyList<DivergencePoint> BuildDivergenceOverTime(IEnumerable<Job> jobs)
    {
        // Últimos N jobs Reconcile/Force con maxDivergencePct, en orden cronológico.
        var points = jobs
            .Where(j => j.Type is JobType.Reconcile or JobType.Force)
            .Select(j => (Job: j, Value: ReadDouble(j.Summary, "maxDivergencePct")))
            .Where(x => x.Value is not null)
            .OrderByDescending(x => x.Job.StartedAt)
            .Take(DivergencePoints)
            .OrderBy(x => x.Job.StartedAt)
            .Select(x => new DivergencePoint(
                x.Job.StartedAt,
                Math.Round(x.Value!.Value, 2)))
            .ToList();

        return points;
    }

    private static IReadOnlyList<LabeledValue> BuildAvgDurationByType(IEnumerable<Job> jobs)
    {
        // Duración media (en minutos) de jobs terminados, agrupada por tipo.
        var durations = jobs
            .Where(j => j.FinishedAt is not null)
            .Select(j => (j.Type, Minutes: (j.FinishedAt!.Value - j.StartedAt).TotalMinutes))
            .Where(x => x.Minutes >= 0)
            .GroupBy(x => x.Type)
            .ToDictionary(g => g.Key, g => g.Average(x => x.Minutes));

        return Enum.GetValues<JobType>()
            .Where(durations.ContainsKey)
            .Select(t => new LabeledValue(t.ToString(), Math.Round(durations[t], 2)))
            .ToList();
    }

    private IReadOnlyList<StageQueueDepth> BuildQueueDepths()
    {
        try
        {
            var snap = _orchestration.GetQueueSnapshot();
            return
            [
                ToDepth("Ingest", snap.Ingest),
                ToDepth("Sql", snap.Sql),
                ToDepth("Reconcile", snap.Reconcile),
            ];
        }
        catch
        {
            return [];
        }

        static StageQueueDepth ToDepth(string stage, StageQueueCounts c) =>
            new(stage, c.Pending, c.Claimed, c.Done, c.Error);
    }

    private WorkerSummary BuildWorkerSummary()
    {
        try
        {
            var workers = _orchestration.GetWorkers();
            var busy = workers.Count(w => w.Status == WorkerState.Busy);
            var idle = workers.Count(w => w.Status == WorkerState.Idle);
            return new WorkerSummary(busy, idle, workers.Count);
        }
        catch
        {
            return new WorkerSummary(0, 0, 0);
        }
    }

    private Dictionary<string, string> PipelineNames()
    {
        try
        {
            return _pipelines.List().ToDictionary(p => p.Id, p => p.Name);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    // ---- Helpers de parseo robusto de Summary ----

    private static bool IsTerminal(Job j) =>
        !JobProgressHelper.IsActive(j);

    // Día de un instante en la zona pedida (UTC si no se indica).
    private static DateOnly Day(DateTimeOffset at, TimeZoneInfo? zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Utc).DateTime);

    // Lee un valor numérico de Summary tolerando long, int, double, string o JsonElement.
    private static double? ReadDouble(IReadOnlyDictionary<string, object?>? summary, string key)
    {
        if (summary is null || !summary.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        switch (raw)
        {
            case double d:
                return d;
            case float f:
                return f;
            case long l:
                return l;
            case int i:
                return i;
            case decimal m:
                return (double)m;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sv):
                return sv;
            case JsonElement je:
                return ReadJsonDouble(je);
            default:
                return double.TryParse(
                    raw.ToString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : null;
        }
    }

    private static long? ReadLong(IReadOnlyDictionary<string, object?>? summary, string key)
    {
        var value = ReadDouble(summary, key);
        // Descarta NaN/Infinity: (long)double.PositiveInfinity es indefinido en C#.
        return value is null || !double.IsFinite(value.Value) ? null : (long)value.Value;
    }

    private static double? ReadJsonDouble(JsonElement je)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Number when je.TryGetDouble(out var d):
                return d;
            case JsonValueKind.String when double.TryParse(
                je.GetString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var sv):
                return sv;
            default:
                return null;
        }
    }
}
