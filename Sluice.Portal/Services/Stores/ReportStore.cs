using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Stores;

// El último reporte de cada pipeline (reconcile, diff, values) sale de un puntero
// latest-{pipeline}.json que deja cada Save: antes se deserializaban todos los reportes de todos los
// pipelines en cada llamada, y Pipelines la hace una vez por pipeline. Un reporte ilegible se omite
// con un aviso en el log en vez de tumbar la página o el re-sync.
public sealed class ReportStore
{
    private const string PointerPrefix = "latest-";

    private readonly AppOptions _options;
    private readonly ILogger<ReportStore>? _logger;
    private readonly object _lock = new();

    // Ficheros ilegibles ya avisados (ruta y última escritura): un aviso por versión del fichero, no
    // uno por consulta.
    private readonly HashSet<(string Path, DateTime WriteTime)> _unreadableLogged = [];

    public ReportStore(IOptions<AppOptions> options, ILogger<ReportStore>? logger = null)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void SaveReconcile(ReconcileReport report)
    {
        lock (_lock)
        {
            var dir = ReportDir("reconcile");
            AtomicJsonFile.Write(Path.Combine(dir, $"{report.Id}.json"), report);
            UpdatePointer(dir, report.PipelineId, report.Id, report.CreatedAt);
        }
    }

    public ReconcileReport? GetLatestReconcile(string pipelineId) =>
        GetLatest<ReconcileReport>("reconcile", pipelineId, r => r.Id, r => r.PipelineId, r => r.CreatedAt);

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
            var dir = ReportDir("diff");
            AtomicJsonFile.Write(Path.Combine(dir, $"{report.Id}.json"), report);
            UpdatePointer(dir, report.PipelineId, report.Id, report.CreatedAt);
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

    public DiffReport? GetLatestDiff(string pipelineId) =>
        GetLatest<DiffReport>("diff", pipelineId, r => r.Id, r => r.PipelineId, r => r.CreatedAt);

    // --- Comparación de valores (reports/values, aparte de la conciliación de conteos) ---

    public void SaveValues(ValueCompareReport report)
    {
        lock (_lock)
        {
            var dir = ReportDir("values");
            Directory.CreateDirectory(dir);
            AtomicJsonFile.Write(Path.Combine(dir, $"{report.Id}.json"), report);
            UpdatePointer(dir, report.PipelineId, report.Id, report.CreatedAt);
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

    public ValueCompareReport? GetLatestValues(string pipelineId) =>
        GetLatest<ValueCompareReport>("values", pipelineId, r => r.Id, r => r.PipelineId, r => r.CreatedAt);

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

            return ReportFiles(dir)
                .Select(TryRead<ReconcileRemediation>)
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

    private string ReportDir(string kind) => Path.Combine(_options.DataDir, "reports", kind);

    private static string PointerPath(string dir, string pipelineId) => Path.Combine(dir, $"{PointerPrefix}{pipelineId}.json");

    // Sin puntero (reportes de antes de él) o si apunta a un fichero que ya no está, se busca entre
    // todos una vez y se deja el puntero para la siguiente. Lo encontrado así manda: sustituye al puntero
    // aunque este sea más reciente, porque ya no sirve. Si no hay ninguno, el puntero queda sin Id
    // ("aún sin reporte", con la fecha mínima para que el primer Save lo sustituya) y no se vuelve a buscar.
    private T? GetLatest<T>(string kind, string pipelineId, Func<T, string> idOf, Func<T, string> pipelineOf, Func<T, DateTimeOffset> createdAt)
        where T : class
    {
        // El id llega de la ruta: si no es un id válido no se lee ni se escribe nada fuera de reports/{kind}.
        if (!EntityIds.IsValid(pipelineId))
        {
            return null;
        }

        lock (_lock)
        {
            var dir = ReportDir(kind);
            if (!Directory.Exists(dir))
            {
                return null;
            }

            var pointerPath = PointerPath(dir, pipelineId);
            if (TryRead<LatestPointer>(pointerPath) is { } pointer)
            {
                if (pointer.Id is null)
                {
                    return null;
                }

                if (ReadPointed(pointer) is { } pointed)
                {
                    return pointed;
                }
            }

            var latest = ReportFiles(dir)
                .Select(TryRead<T>)
                .Where(r => r is not null && pipelineOf(r) == pipelineId)
                .OrderByDescending(r => createdAt(r!))
                .FirstOrDefault();
            try
            {
                // Si mientras se buscaba un Save de otro proceso dejó el puntero a su reporte, manda ese.
                using (PointerLock(dir, pipelineId))
                {
                    if (TryRead<LatestPointer>(pointerPath) is { Id: not null } current && ReadPointed(current) is { } saved)
                    {
                        return saved;
                    }

                    AtomicJsonFile.Write(
                        pointerPath,
                        latest is null ? new LatestPointer(null, DateTimeOffset.MinValue) : new LatestPointer(idOf(latest), createdAt(latest)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // Sin puntero se vuelve a buscar en la siguiente consulta: la respuesta no cambia.
            }

            return latest;

            T? ReadPointed(LatestPointer p) =>
                TryRead<T>(Path.Combine(dir, $"{p.Id}.json")) is { } r && pipelineOf(r) == pipelineId ? r : null;
        }
    }

    // Un reporte más antiguo guardado después (un reintento tardío) no mueve el puntero hacia atrás.
    private void UpdatePointer(string dir, string pipelineId, string id, DateTimeOffset createdAt)
    {
        var path = PointerPath(dir, pipelineId);
        using (PointerLock(dir, pipelineId))
        {
            if (TryRead<LatestPointer>(path) is { } current && current.CreatedAt > createdAt)
            {
                return;
            }

            AtomicJsonFile.Write(path, new LatestPointer(id, createdAt));
        }
    }

    // Lock entre procesos (api y workers comparten DATA_DIR) para leer y escribir el puntero de una vez.
    private IDisposable PointerLock(string dir, string pipelineId) =>
        FileLock.Acquire(
            Path.Combine(_options.DataDir, "locks", $"report-{Path.GetFileName(dir)}-{pipelineId}.lock"), TimeSpan.FromSeconds(10));

    private static IEnumerable<string> ReportFiles(string dir) =>
        Directory.GetFiles(dir, "*.json")
            .Where(f => !Path.GetFileName(f).StartsWith(PointerPrefix, StringComparison.OrdinalIgnoreCase));

    // null si no existe o no se puede leer; lo ilegible queda en el log una vez por versión del fichero.
    private T? TryRead<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (_unreadableLogged.Add((path, File.GetLastWriteTimeUtc(path))))
            {
                _logger?.LogWarning(ex, "Reporte ilegible {Path}: se omite.", path);
            }

            return null;
        }
    }

    private sealed record LatestPointer(string? Id, DateTimeOffset CreatedAt);
}