using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Pipeline;

public interface IReconcileService
{
    ReconcileReport BuildReport(string pipelineId, string jobId, IEnumerable<ReconcileEntityResult> entities);
    ReconcileReport BuildScopedReport(string pipelineId, string jobId, IReadOnlyCollection<string> scopedEntities, IEnumerable<ReconcileEntityResult> results);
    DiffReport BuildDiffReport(string pipelineId, string jobId, IEnumerable<DiffEntityResult> entities);

    // Tablas que justifican actuar (alerta / corrección automática): superan el umbral y no tienen
    // una divergencia aceptada vigente.
    IReadOnlyList<string> ActionableEntities(ReconcileReport report);

    ValueCompareReport BuildValuesReport(string pipelineId, string jobId, IReadOnlyList<ValueCompareEntityResult> entities);

    // Tablas con diferencias de valores y sin divergencia aceptada (una tabla aceptada no alerta ni
    // se corrige sola, tampoco por valores).
    IReadOnlyList<string> ActionableValueEntities(ValueCompareReport report);
}

public sealed class ReconcileService : IReconcileService
{
    private readonly SettingsStore _settings;
    private readonly ReportStore _reports;

    public ReconcileService(SettingsStore settings, ReportStore reports)
    {
        _settings = settings;
        _reports = reports;
    }

    public ReconcileReport BuildReport(string pipelineId, string jobId, IEnumerable<ReconcileEntityResult> entities)
    {
        var list = entities.ToList();
        var threshold = _settings.Get().ReconDivergencePct;
        var acks = _reports.GetAcks(pipelineId);

        // MaxDivergencePct refleja la divergencia REAL (todas las entidades) para que la UI no
        // mienta. IsPartial (alerta/banner) ignora las entidades aceptadas cuya divergencia se
        // mantiene cerca del valor aceptado: una divergencia legítima no debe seguir alertando.
        var maxDiv = list.Count == 0 ? 0 : list.Max(e => e.DivergencePct);
        var unsuppressedMax = list
            .Where(e => !IsSuppressed(e, acks, threshold))
            .Select(e => e.DivergencePct)
            .DefaultIfEmpty(0)
            .Max();

        var report = new ReconcileReport
        {
            Id = $"recon-{jobId}",
            PipelineId = pipelineId,
            JobId = jobId,
            Entities = list,
            MaxDivergencePct = maxDiv,
            IsPartial = unsuppressedMax > threshold,
        };

        _reports.SaveReconcile(report);
        return report;
    }

    // Re-sync por tabla: solo se reconciliaron las entidades re-sincronizadas, pero el reporte debe
    // seguir siendo la foto completa del pipeline. Se arrastran las demás entidades del último
    // reporte (con su último conteo conocido) y se reemplazan solo las re-sincronizadas; si no, el
    // "último reporte" quedaría con esas tablas y la UI (banner, /reconcile, /tables) perdería el resto.
    // Una entidad re-sincronizada que no se pudo contar no está en results: tampoco arrastra su
    // fila anterior (su divergencia vieja no debe quedar sellada con este job ni alertar).
    public ReconcileReport BuildScopedReport(string pipelineId, string jobId, IReadOnlyCollection<string> scopedEntities, IEnumerable<ReconcileEntityResult> results)
    {
        var scoped = scopedEntities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prior = _reports.GetLatestReconcile(pipelineId);
        var entities = (prior?.Entities ?? [])
            .Where(e => !scoped.Contains(e.Entity))
            .Concat(results);

        return BuildReport(pipelineId, jobId, entities);
    }

    public IReadOnlyList<string> ActionableEntities(ReconcileReport report)
    {
        var threshold = _settings.Get().ReconDivergencePct;
        var acks = _reports.GetAcks(report.PipelineId);
        return report.Entities
            .Where(e => e.DivergencePct > threshold && !IsSuppressed(e, acks, threshold))
            .Select(e => e.Entity)
            .ToList();
    }

    public ValueCompareReport BuildValuesReport(string pipelineId, string jobId, IReadOnlyList<ValueCompareEntityResult> entities)
    {
        var report = new ValueCompareReport
        {
            Id = $"values-{jobId}",
            PipelineId = pipelineId,
            JobId = jobId,
            Entities = entities.ToList(),
        };

        _reports.SaveValues(report);
        return report;
    }

    public IReadOnlyList<string> ActionableValueEntities(ValueCompareReport report)
    {
        var acks = _reports.GetAcks(report.PipelineId);
        return report.Entities
            .Where(e => e.HasDifferences && !acks.ContainsKey(e.Entity))
            .Select(e => e.Entity)
            .ToList();
    }

    private static bool IsSuppressed(ReconcileEntityResult entity, IReadOnlyDictionary<string, ReconAck> acks, double threshold) =>
        acks.TryGetValue(entity.Entity, out var ack) && Math.Abs(entity.DivergencePct - ack.AckPct) <= threshold;

    public DiffReport BuildDiffReport(string pipelineId, string jobId, IEnumerable<DiffEntityResult> entities)
    {
        var report = new DiffReport
        {
            Id = $"diff-{jobId}",
            PipelineId = pipelineId,
            JobId = jobId,
            Entities = entities.ToList(),
        };

        _reports.SaveDiff(report);
        return report;
    }

    public static double ComputeDivergencePct(long source, long destination)
    {
        if (source == 0 && destination == 0)
        {
            return 0;
        }

        var max = Math.Max(source, destination);
        return Math.Abs(source - destination) * 100.0 / max;
    }
}