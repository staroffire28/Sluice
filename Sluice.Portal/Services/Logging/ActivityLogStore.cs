using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Logging;

public interface IActivityLog
{
    // Eventos del sistema que no son la acción de nadie (retención, avisos, huérfanos). Lo que
    // alguien (o el scheduler, la cola...) decide hacer va por IAuditLog.Record.
    void LogSystem(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info");

    // Con la clave del texto localizable (ActivityLogEntry.MessageKey). Por defecto guarda solo el
    // mensaje, para los dobles de test.
    void LogSystem(string action, string message, string? pipelineId, string? jobId, string level, string messageKey, params object?[] messageArgs) =>
        LogSystem(action, message, pipelineId, jobId, level);
    // Devuelve la página y el total filtrado en UNA sola lectura/deserialización
    // (evita releer y deserializar todos los archivos dos veces por render).
    ActivityLogPage QueryPage(ActivityLogFilter filter, int page, int pageSize);

    // Todo lo filtrado, más reciente primero (la exportación CSV): mismo filtro que QueryPage.
    IReadOnlyList<ActivityLogEntry> Query(ActivityLogFilter filter);
}

// Auditoría de las acciones que mutan algo: la llaman los servicios (no las páginas), con el actor
// que pasó el llamador. El nivel sale del outcome (Ok=info, Rejected y Cancelled=warning, Error=error) salvo que
// se indique otro: un Ok que merece atención (p. ej. desactivar el dry-run de la retención) es warning.
public interface IAuditLog
{
    void Record(AuditActor actor, string action, AuditOutcome outcome, string? pipelineId, string? jobId, string? target, string message, string? level = null);

    // Con la clave del texto localizable (ActivityLogEntry.MessageKey). Por defecto guarda solo el
    // mensaje, para los dobles de test.
    void Record(AuditActor actor, string action, AuditOutcome outcome, string? pipelineId, string? jobId, string? target, string message, string? level, string messageKey, params object?[] messageArgs) =>
        Record(actor, action, outcome, pipelineId, jobId, target, message, level);
}

public sealed record ActivityLogPage(IReadOnlyList<ActivityLogEntry> Items, int Total, int Page);

// Filtros de la página Logs. Zone: en qué zona se toma el día del rango (la del navegador, que es
// la que muestra la tabla; por defecto, la del servidor). Text: el mensaje tal como se pinta
// (EventText, en el idioma de quien busca), para que la búsqueda encuentre también por él.
public sealed record ActivityLogFilter(
    string? Source = null,
    string? Level = null,
    string? Search = null,
    DateRange? Range = null,
    TimeZoneInfo? Zone = null,
    Func<ActivityLogEntry, string>? Text = null);

// Registro de actividad con persistencia en archivo (JSONL), robusto bajo réplicas.
// Cada proceso (api + N workers) escribe SU PROPIO archivo por día UTC,
// activity-{Máquina}-{yyyyMMdd}.jsonl, para no competir por el mismo archivo. La lectura agrega
// todos los archivos; una consulta por fechas abre solo los de esos días. La retención los purga
// por la fecha del nombre (AuditRetentionDays). Los de antes del nombre por día
// (activity-{Máquina}.jsonl y su .1 rotado) se siguen leyendo.
public sealed class ActivityLogStore : IActivityLog, IAuditLog
{
    // Tope de entradas que cumplen el filtro a cargar en memoria al consultar (las más recientes).
    private const int MaxEntriesToRead = 20_000;

    // Bloque con el que se lee un archivo desde el final.
    private const int ReadBlockBytes = 64 * 1024;

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

    public void LogSystem(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info") =>
        Append(NewEntry("system", level, action, message, pipelineId, jobId));

    public void LogSystem(string action, string message, string? pipelineId, string? jobId, string level, string messageKey, params object?[] messageArgs) =>
        Append(NewEntry("system", level, action, message, pipelineId, jobId) with
        {
            MessageKey = messageKey,
            MessageArgs = messageArgs.Length > 0 ? messageArgs : null,
        });

    public void Record(AuditActor actor, string action, AuditOutcome outcome, string? pipelineId, string? jobId, string? target, string message, string? level = null) =>
        RecordEntry(actor, action, outcome, pipelineId, jobId, target, message, level, null, null);

    public void Record(AuditActor actor, string action, AuditOutcome outcome, string? pipelineId, string? jobId, string? target, string message, string? level, string messageKey, params object?[] messageArgs) =>
        RecordEntry(actor, action, outcome, pipelineId, jobId, target, message, level, messageKey, messageArgs);

    private void RecordEntry(AuditActor actor, string action, AuditOutcome outcome, string? pipelineId, string? jobId, string? target, string message, string? level, string? messageKey, object?[]? messageArgs)
    {
        level ??= outcome switch
        {
            AuditOutcome.Rejected or AuditOutcome.Cancelled => "warning",
            AuditOutcome.Error => "error",
            _ => "info",
        };

        Append(NewEntry(actor.Source, level, action, message, pipelineId, jobId) with
        {
            Channel = actor.Channel,
            Client = NullIfBlank(actor.Client),
            Outcome = outcome,
            Target = NullIfBlank(target),
            MessageKey = messageKey,
            MessageArgs = messageArgs is { Length: > 0 } ? messageArgs : null,
        });
    }

    private ActivityLogEntry NewEntry(string source, string level, string action, string message, string? pipelineId, string? jobId) => new()
    {
        Timestamp = _time.GetUtcNow(),
        Source = source,
        Level = string.IsNullOrWhiteSpace(level) ? "info" : level,
        Action = action,
        Message = message,
        PipelineId = NullIfBlank(pipelineId),
        JobId = NullIfBlank(jobId),
        Host = Environment.MachineName,
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private void Append(ActivityLogEntry entry)
    {
        // El logging NUNCA debe romper la acción que lo invoca: todo va envuelto y se traga.
        try
        {

            // Una línea = un ActivityLogEntry; sin saltos de línea internos.
            var line = JsonSerializer.Serialize(entry, JsonDefaults.Options).Replace("\r", "").Replace("\n", " ") + Environment.NewLine;

            lock (_writeLock)
            {
                var dir = GetLogDirectory();
                Directory.CreateDirectory(dir);
                var path = GetDailyFilePath(dir, entry.Timestamp);

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

    public IReadOnlyList<ActivityLogEntry> Query(ActivityLogFilter filter) =>
        ReadEntries(filter).OrderByDescending(e => e.Timestamp).ToList();

    private static bool Matches(ActivityLogFilter filter, ActivityLogEntry e)
    {
        if (!string.IsNullOrWhiteSpace(filter.Source) && !string.Equals(e.Source, filter.Source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Level) && !string.Equals(e.Level, filter.Level, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Range is { IsEmpty: false } range && !range.Contains(e.Timestamp, filter.Zone))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(filter.Search))
        {
            return true;
        }

        // Substring case-insensitive sobre Message + Action + PipelineId + JobId + quién actuó.
        var search = filter.Search;
        return Contains(e.Message, search) ||
            (filter.Text is not null && Contains(filter.Text(e), search)) ||
            Contains(e.Action, search) ||
            Contains(e.PipelineId, search) ||
            Contains(e.JobId, search) ||
            Contains(e.Channel, search) ||
            Contains(e.Client, search) ||
            Contains(e.HostName, search) ||
            Contains(e.Target, search);
    }

    private static bool Contains(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    // Recorre los activity-*.jsonl de los días del rango (todos sin rango; los de antes del nombre
    // por día, siempre), cada uno desde el final, ignora líneas corruptas y se queda con las
    // MaxEntriesToRead más recientes que cumplen el filtro. El tope va después del filtro: un día
    // con mucho tráfico ya no deja fuera los días que se piden.
    private List<ActivityLogEntry> ReadEntries(ActivityLogFilter filter)
    {
        var dir = GetLogDirectory();
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var entries = new List<ActivityLogEntry>();
        var range = filter.Range is { IsEmpty: false } r ? r : null;

        // Más recientes primero: los diarios por su fecha, los de antes por la última escritura (el
        // .1 rotado es más viejo que su archivo base).
        var files = Directory.GetFiles(dir, "activity-*.jsonl")
            .Select(file => (File: file, Day: DayOf(file)))
            .Where(f => f.Day is not { } day || range is null || MayHoldRangeDays(day, range))
            .OrderByDescending(f => f.Day is { } day ? day.ToDateTime(TimeOnly.MaxValue) : File.GetLastWriteTimeUtc(f.File));

        foreach (var (file, _) in files)
        {
            if (entries.Count >= MaxEntriesToRead)
            {
                break;
            }

            try
            {
                foreach (var raw in ReadLinesFromEnd(file))
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    ActivityLogEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<ActivityLogEntry>(raw, JsonDefaults.Options);
                    }
                    catch (JsonException)
                    {
                        // Línea corrupta o parcial: se ignora.
                        continue;
                    }

                    if (entry is not null && Matches(filter, entry))
                    {
                        entries.Add(entry);
                        if (entries.Count >= MaxEntriesToRead)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Otra réplica está escribiendo o la retención lo borró: se salta en esta consulta.
            }
        }

        return entries;
    }

    // Un día UTC guarda entradas de su día y de los vecinos en la zona de quien consulta (como mucho
    // ±14 h): se lee si cualquiera de los tres cae en el rango.
    private static bool MayHoldRangeDays(DateOnly utcDay, DateRange range) =>
        (range.From is null || utcDay.AddDays(1) >= range.From) && (range.To is null || utcDay.AddDays(-1) <= range.To);

    // Líneas de la última a la primera, por bloques: las más recientes salen sin leer el archivo
    // entero. Partir por '\n' es seguro en UTF-8 (ningún carácter multibyte contiene ese byte).
    // FileShare.ReadWrite: no bloquear a un escritor concurrente (otra réplica haciendo append);
    // FileShare.Delete: no impedir que la retención purgue el archivo mientras se lee.
    private static IEnumerable<string> ReadLinesFromEnd(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[ReadBlockBytes];
        var carry = Array.Empty<byte>();
        var position = stream.Length;
        while (position > 0)
        {
            var read = (int)Math.Min(buffer.Length, position);
            position -= read;
            stream.Position = position;
            stream.ReadExactly(buffer, 0, read);

            var end = read;
            for (var i = read - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                var line = DecodeLine(buffer, i + 1, end, carry);
                carry = [];
                end = i;
                yield return line;
            }

            carry = [.. buffer[..end], .. carry];
        }

        if (carry.Length > 0)
        {
            yield return DecodeLine(carry, 0, carry.Length, []);
        }
    }

    // La línea es buffer[start..end] seguida de lo que quedó del bloque siguiente (tail). La primera
    // del archivo lleva el BOM que escribe File.AppendAllText con Encoding.UTF8.
    private static string DecodeLine(byte[] buffer, int start, int end, byte[] tail) =>
        (tail.Length == 0
            ? Encoding.UTF8.GetString(buffer, start, end - start)
            : Encoding.UTF8.GetString([.. buffer[start..end], .. tail])).TrimStart('\uFEFF').TrimEnd('\r');

    private string GetLogDirectory() => Path.Combine(_options.DataDir, "logs");

    // Fecha en cultura invariante: el nombre no puede depender del idioma de quien provoca la entrada.
    private static string GetDailyFilePath(string dir, DateTimeOffset at) =>
        Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"activity-{Sanitize(Environment.MachineName)}-{at.UtcDateTime:yyyyMMdd}.jsonl"));

    // El día UTC de un archivo diario (activity-{Máquina}-{yyyyMMdd}.jsonl), o null en los de antes
    // del nombre por día. internal: la retención purga los diarios por esta fecha.
    internal static DateOnly? DayOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        return dash > "activity".Length
            && DateOnly.TryParseExact(name[(dash + 1)..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : null;
    }

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
