using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Logging;

public interface IActivityLog
{
    void LogUser(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info");
    void LogSystem(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info");
    // Devuelve la página y el total filtrado en UNA sola lectura/deserialización
    // (evita releer y deserializar todos los archivos dos veces por render).
    ActivityLogPage QueryPage(ActivityLogFilter filter, int page, int pageSize);

    // Todo lo filtrado, más reciente primero (la exportación CSV): mismo filtro que QueryPage.
    IReadOnlyList<ActivityLogEntry> Query(ActivityLogFilter filter);
}

public sealed record ActivityLogPage(IReadOnlyList<ActivityLogEntry> Items, int Total, int Page);

// Filtros de la página Logs. Zone: en qué zona se toma el día del rango (por defecto, la del
// servidor, que es la que muestra la tabla).
public sealed record ActivityLogFilter(
    string? Source = null,
    string? Level = null,
    string? Search = null,
    DateRange? Range = null,
    TimeZoneInfo? Zone = null);

// Registro de actividad con persistencia en archivo (JSONL), robusto bajo réplicas.
// Cada proceso (api + N workers) escribe SU PROPIO archivo activity-{Máquina}.jsonl
// para no competir por el mismo archivo. La lectura agrega todos los archivos.
public sealed class ActivityLogStore : IActivityLog
{
    // Tamaño máximo del archivo activo antes de rotar a .1 (~5 MB).
    private const long MaxFileBytes = 5 * 1024 * 1024;

    // Tope de líneas a cargar en memoria al consultar (las más recientes por archivo).
    private const int MaxLinesToRead = 20_000;

    // Reintentos cortos ante locks transitorios de Windows (mismo espíritu que TaskStore).
    private const int MaxWriteAttempts = 3;

    private readonly AppOptions _options;
    private readonly TimeProvider _time;
    private readonly object _writeLock = new();

    public ActivityLogStore(IOptions<AppOptions> options, TimeProvider? time = null)
    {
        _options = options.Value;
        _time = time ?? TimeProvider.System;
    }

    public void LogUser(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info") =>
        Append("user", level, action, message, pipelineId, jobId);

    public void LogSystem(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info") =>
        Append("system", level, action, message, pipelineId, jobId);

    private void Append(string source, string level, string action, string message, string? pipelineId, string? jobId)
    {
        // El logging NUNCA debe romper la acción que lo invoca: todo va envuelto y se traga.
        try
        {
            var entry = new ActivityLogEntry
            {
                Timestamp = _time.GetUtcNow(),
                Source = source,
                Level = string.IsNullOrWhiteSpace(level) ? "info" : level,
                Action = action,
                Message = message,
                PipelineId = string.IsNullOrWhiteSpace(pipelineId) ? null : pipelineId,
                JobId = string.IsNullOrWhiteSpace(jobId) ? null : jobId,
                Actor = Environment.MachineName,
            };

            // Una línea = un ActivityLogEntry; sin saltos de línea internos.
            var line = JsonSerializer.Serialize(entry, JsonDefaults.Options).Replace("\r", "").Replace("\n", " ") + Environment.NewLine;

            lock (_writeLock)
            {
                var dir = GetLogDirectory();
                Directory.CreateDirectory(dir);
                var path = GetCurrentFilePath(dir);

                RotateIfNeeded(path);

                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.AppendAllText(path, line, Encoding.UTF8);
                        return;
                    }
                    catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < MaxWriteAttempts)
                    {
                        // Lock transitorio (otro lector/escritor); breve espera y reintento.
                        Thread.Sleep(25);
                    }
                }
            }
        }
        catch
        {
            // Tragado a propósito: el audit log no debe propagar errores al llamador.
        }
    }

    // Rotación simple: si el archivo activo supera el tope, renómbralo a .1 (sobrescribe el .1 previo).
    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxFileBytes)
            {
                return;
            }

            var rotated = RotatedPath(path);
            if (File.Exists(rotated))
            {
                File.Delete(rotated);
            }

            File.Move(path, rotated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si la rotación falla por un lock transitorio, seguimos escribiendo en el archivo
            // actual: crecerá un poco más de lo ideal, pero no perdemos la entrada ni rompemos.
        }
    }

    public ActivityLogPage QueryPage(ActivityLogFilter filter, int page, int pageSize)
    {
        // Una sola lectura+filtrado: de ahí se derivan total Y página (antes Count y Query
        // releían y deserializaban todos los archivos por separado en cada render).
        var filtered = Query(filter);

        if (pageSize < 1)
        {
            pageSize = 1;
        }

        var total = filtered.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        var items = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new ActivityLogPage(items, total, page);
    }

    public IReadOnlyList<ActivityLogEntry> Query(ActivityLogFilter filter)
    {
        IEnumerable<ActivityLogEntry> query = ReadAllEntries();

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            query = query.Where(e => string.Equals(e.Source, filter.Source, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Level))
        {
            query = query.Where(e => string.Equals(e.Level, filter.Level, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Substring case-insensitive sobre Message + Action + PipelineId + JobId.
            var search = filter.Search;
            query = query.Where(e =>
                Contains(e.Message, search) ||
                Contains(e.Action, search) ||
                Contains(e.PipelineId, search) ||
                Contains(e.JobId, search));
        }

        if (filter.Range is { IsEmpty: false } range)
        {
            query = query.Where(e => range.Contains(e.Timestamp, filter.Zone));
        }

        return query.OrderByDescending(e => e.Timestamp).ToList();
    }

    private static bool Contains(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    // Enumera TODOS los activity-*.jsonl (incluye los .1), parsea línea por línea
    // (ignora líneas corruptas) y limita el total cargado a MaxLinesToRead.
    private List<ActivityLogEntry> ReadAllEntries()
    {
        var dir = GetLogDirectory();
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var entries = new List<ActivityLogEntry>();
        var remaining = MaxLinesToRead;

        // Más recientes primero (el .1 rotado es más viejo que su archivo base).
        var files = Directory.GetFiles(dir, "activity-*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var file in files)
        {
            if (remaining <= 0)
            {
                break;
            }

            string[] lines;
            try
            {
                lines = ReadLinesShared(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Otra réplica está escribiendo/rotando: saltar este archivo en esta consulta.
                continue;
            }

            // Tomamos las últimas líneas del archivo (las más recientes) hasta agotar el tope.
            var start = Math.Max(0, lines.Length - remaining);
            for (var i = lines.Length - 1; i >= start; i--)
            {
                var raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<ActivityLogEntry>(raw, JsonDefaults.Options);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                        remaining--;
                    }
                }
                catch (JsonException)
                {
                    // Línea corrupta o parcial: se ignora.
                }
            }
        }

        return entries;
    }

    // FileShare.ReadWrite: no bloquear a un escritor concurrente (otra réplica haciendo append).
    private static string[] ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private string GetLogDirectory() => Path.Combine(_options.DataDir, "logs");

    private static string GetCurrentFilePath(string dir) =>
        Path.Combine(dir, $"activity-{Sanitize(Environment.MachineName)}.jsonl");

    private static string RotatedPath(string currentPath) =>
        Path.ChangeExtension(currentPath, null) + ".1.jsonl";

    // El nombre de máquina puede traer caracteres no válidos para un path en algún entorno.
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}
