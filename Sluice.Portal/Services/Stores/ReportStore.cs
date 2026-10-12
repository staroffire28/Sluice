using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Stores;

public sealed class ReportStore
{
    private readonly AppOptions _options;
    private readonly object _lock = new();

    public ReportStore(IOptions<AppOptions> options) => _options = options.Value;

    public void SaveReconcile(ReconcileReport report)
    {
        lock (_lock)
        {
            var path = Path.Combine(_options.DataDir, "reports", "reconcile", $"{report.Id}.json");
            AtomicJsonFile.Write(path, report);
        }
    }

    public ReconcileReport? GetLatestReconcile(string pipelineId)
    {
        lock (_lock)
        {
            var dir = Path.Combine(_options.DataDir, "reports", "reconcile");
            if (!Directory.Exists(dir))
            {
                return null;
            }

            return Directory.GetFiles(dir, "*.json")
                .Select(f =>
                {
                    using var stream = File.OpenRead(f);
                    return JsonSerializer.Deserialize<ReconcileReport>(stream, JsonDefaults.Options);
                })
                .Where(r => r?.PipelineId == pipelineId)
                .OrderByDescending(r => r!.CreatedAt)
                .FirstOrDefault();
        }
    }

    // Reporte de reconcile de un job concreto (Id = recon-{jobId}). Para el PDF por job:
    // así el reporte refleja la conciliación de ESE job, no la más reciente del pipeline.
    public ReconcileReport? GetReconcileByJob(string jobId)
    {
        lock (_lock)
        {
            var path = Path.Combine(_options.DataDir, "reports", "reconcile", $"recon-{jobId}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ReconcileReport>(stream, JsonDefaults.Options);
        }
    }

    public void SaveDiff(DiffReport report)
    {
        lock (_lock)
        {
            var path = Path.Combine(_options.DataDir, "reports", "diff", $"{report.Id}.json");
            AtomicJsonFile.Write(path, report);
        }
    }

    public DiffReport? GetDiffByJob(string jobId)
    {
        lock (_lock)
        {
            var path = Path.Combine(_options.DataDir, "reports", "diff", $"diff-{jobId}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<DiffReport>(stream, JsonDefaults.Options);
        }
    }

    public DiffReport? GetLatestDiff(string pipelineId)
    {
        lock (_lock)
        {
            var dir = Path.Combine(_options.DataDir, "reports", "diff");
            if (!Directory.Exists(dir))
            {
                return null;
            }

            return Directory.GetFiles(dir, "*.json")
                .Select(f =>
                {
                    using var stream = File.OpenRead(f);
                    return JsonSerializer.Deserialize<DiffReport>(stream, JsonDefaults.Options);
                })
                .Where(r => r?.PipelineId == pipelineId)
                .OrderByDescending(r => r!.CreatedAt)
                .FirstOrDefault();
        }
    }

    // --- Comparación de valores (reports/values, aparte de la conciliación de conteos) ---

    public void SaveValues(ValueCompareReport report)
    {
        lock (_lock)
        {
            var dir = Path.Combine(_options.DataDir, "reports", "values");
            Directory.CreateDirectory(dir);
            AtomicJsonFile.Write(Path.Combine(dir, $"{report.Id}.json"), report);
        }
    }

    public ValueCompareReport? GetValuesByJob(string jobId)
    {
        lock (_lock)
        {
            var path = Path.Combine(_options.DataDir, "reports", "values", $"values-{jobId}.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ValueCompareReport>(stream, JsonDefaults.Options);
        }
    }

    public ValueCompareReport? GetLatestValues(string pipelineId)
    {
        lock (_lock)
        {
            var dir = Path.Combine(_options.DataDir, "reports", "values");
            if (!Directory.Exists(dir))
            {
                return null;
            }

            return Directory.GetFiles(dir, "*.json")
                .Select(f =>
                {
                    using var stream = File.OpenRead(f);
                    return JsonSerializer.Deserialize<ValueCompareReport>(stream, JsonDefaults.Options);
                })
                .Where(r => r?.PipelineId == pipelineId)
                .OrderByDescending(r => r!.CreatedAt)
                .FirstOrDefault();
        }
    }

    // --- Historial de remediación (re-sync por tabla) ---

    public void SaveRemediation(ReconcileRemediation remediation)
    {
        lock (_lock)
        {
            var dir = Path.Combine(_options.DataDir, "reports", "remediation");
            Directory.CreateDirectory(dir);
            AtomicJsonFile.Write(Path.Combine(dir, $"{remediation.Id}.json"), remediation);
        }
    }

    public IReadOnlyList<ReconcileRemediation> ListRemediations(string pipelineId)
    {
        lock (_lock)
        {
            var dir = Path.Combine(_options.DataDir, "reports", "remediation");
            if (!Directory.Exists(dir))
            {
                return [];
            }

            return Directory.GetFiles(dir, "*.json")
                .Select(f =>
                {
                    using var stream = File.OpenRead(f);
                    return JsonSerializer.Deserialize<ReconcileRemediation>(stream, JsonDefaults.Options);
                })
                .Where(r => r?.PipelineId == pipelineId)
                .OrderByDescending(r => r!.TriggeredAt)
                .Select(r => r!)
                .ToList();
        }
    }

    // --- Aceptación de divergencias (un archivo por pipeline: entidad -> ack) ---

    public IReadOnlyDictionary<string, ReconAck> GetAcks(string pipelineId)
    {
        lock (_lock)
        {
            var path = AckPath(pipelineId);
            if (!File.Exists(path))
            {
                return new Dictionary<string, ReconAck>(StringComparer.OrdinalIgnoreCase);
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, ReconAck>>(stream, JsonDefaults.Options)
                ?? new Dictionary<string, ReconAck>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SaveAck(string pipelineId, string entity, ReconAck ack)
    {
        lock (_lock)
        {
            var acks = new Dictionary<string, ReconAck>(GetAcks(pipelineId), StringComparer.OrdinalIgnoreCase)
            {
                [entity] = ack,
            };
            WriteAcks(pipelineId, acks);
        }
    }

    public void RemoveAck(string pipelineId, string entity)
    {
        lock (_lock)
        {
            var acks = new Dictionary<string, ReconAck>(GetAcks(pipelineId), StringComparer.OrdinalIgnoreCase);
            if (acks.Remove(entity))
            {
                WriteAcks(pipelineId, acks);
            }
        }
    }

    private void WriteAcks(string pipelineId, Dictionary<string, ReconAck> acks)
    {
        var path = AckPath(pipelineId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicJsonFile.Write(path, acks);
    }

    private string AckPath(string pipelineId) =>
        Path.Combine(_options.DataDir, "reports", "recon-ack", $"{pipelineId}.json");
}