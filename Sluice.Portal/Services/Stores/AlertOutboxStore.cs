using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services.Stores;

// Cola en disco de las alertas de un solo disparo que no llegaron al webhook: un fichero por alerta en
// el volumen compartido, para que cualquier proceso (api o worker) encole y el scheduler del orquestador
// las reenvíe. El nombre empieza por el primer fallo, así que el orden alfabético es el de antigüedad.
// Nunca guarda la URL (lleva un secreto): se resuelve al reenviar, con el webhook de ese momento.
public sealed class AlertOutboxStore
{
    // Espera desde cada intento fallido; a partir del último, cada hora.
    internal static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60),
    ];

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public const int MaxEntries = 500;

    // El nombre empieza por el primer fallo con este formato (20260601T120000000Z, 19 caracteres).
    private const string NameTimeFormat = "yyyyMMdd'T'HHmmssfff'Z'";
    private const int NameTimeLength = 19;

    private readonly string _dir;
    private readonly TimeProvider _time;
    private readonly object _lock = new();

    public AlertOutboxStore(IOptions<AppOptions> options, TimeProvider? time = null)
    {
        _dir = Path.Combine(options.Value.DataDir, "alerts", "outbox");
        _time = time ?? TimeProvider.System;
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    // Guarda una alerta que acaba de fallar y devuelve cuántas de las más antiguas se descartaron para
    // no pasar del tope. Puede lanzar (E/S, permisos): quien llama lo captura.
    public int Enqueue(string eventType, JsonNode payload)
    {
        var now = Now;
        lock (_lock)
        {
            Directory.CreateDirectory(_dir);
            var name = $"{now.UtcDateTime.ToString(NameTimeFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.json";
            AtomicJsonFile.Write(Path.Combine(_dir, name), new AlertOutboxEntry
            {
                EventType = eventType,
                Payload = payload,
                FirstFailedAt = now,
                Attempts = 1,
                NextAttemptAt = now + RetryDelays[0],
            });

            var files = Files();
            var dropped = 0;
            foreach (var oldest in files.Take(Math.Max(0, files.Count - MaxEntries)))
            {
                if (TryDelete(oldest))
                {
                    dropped++;
                }
            }

            return dropped;
        }
    }

    // Cuántas alertas esperan en la cola (también las ilegibles: aún no han caducado).
    public int Count()
    {
        lock (_lock)
        {
            return Files().Count;
        }
    }

    // Borra las que llevan más de 24 h desde el primer fallo y devuelve cuántas. Un fichero ilegible
    // caduca por la fecha de su nombre, para que no cuente en el tope ni en el Panel para siempre.
    public int RemoveExpired()
    {
        var limit = Now - MaxAge;
        var removed = 0;
        lock (_lock)
        {
            foreach (var path in Files())
            {
                var firstFailedAt = Read(path)?.FirstFailedAt ?? NameTime(path);
                if (firstFailedAt is { } at && at <= limit && TryDelete(path))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    // Las que ya toca reintentar, de la más antigua a la más reciente, como mucho max. Las ilegibles
    // (a medio escribir por otro proceso, o rotas) se saltan.
    public IReadOnlyList<(string Path, AlertOutboxEntry Entry)> Due(int max)
    {
        var now = Now;
        var due = new List<(string, AlertOutboxEntry)>();
        lock (_lock)
        {
            foreach (var path in Files())
            {
                if (due.Count >= max)
                {
                    break;
                }

                if (Read(path) is { } entry && entry.NextAttemptAt <= now)
                {
                    due.Add((path, entry));
                }
            }
        }

        return due;
    }

    public void Delete(string path)
    {
        lock (_lock)
        {
            TryDelete(path);
        }
    }

    // Otro intento fallido: cuenta y espera más. Si el fichero ya no está (caducó, se descartó), no se recrea.
    public void RecordFailedAttempt(string path, AlertOutboxEntry entry)
    {
        var attempts = entry.Attempts + 1;
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                return;
            }

            AtomicJsonFile.Write(path, entry with
            {
                Attempts = attempts,
                NextAttemptAt = Now + RetryDelays[Math.Min(attempts, RetryDelays.Length) - 1],
            });
        }
    }

    // Solo *.json: AtomicJsonFile deja x.json.<guid>.tmp en la misma carpeta mientras escribe.
    private List<string> Files()
    {
        if (!Directory.Exists(_dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(_dir, "*.json")
            .Where(f => string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();
    }

    private static AlertOutboxEntry? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AlertOutboxEntry>(stream, JsonDefaults.Options) is { Payload: JsonObject } entry ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? NameTime(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length >= NameTimeLength
            && DateTimeOffset.TryParseExact(name[..NameTimeLength], NameTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

// Una alerta en la cola: el payload tal como se envió la primera vez (con su timestamp original).
public sealed record AlertOutboxEntry
{
    public string EventType { get; init; } = "";
    public JsonNode? Payload { get; init; }
    public DateTimeOffset FirstFailedAt { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset NextAttemptAt { get; init; }
}
