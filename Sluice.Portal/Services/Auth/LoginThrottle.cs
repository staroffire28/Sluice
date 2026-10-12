using System.Collections.Concurrent;

namespace Sluice.Portal.Services.Auth;

// Frena la fuerza bruta de UI_API_KEY en /auth/login: tras MaxFailures intentos fallidos desde el
// mismo cliente dentro de Window se rechaza hasta que la ventana expire. Solo cuentan los fallos,
// así que los inicios de sesión correctos (scripts, tests, varios usuarios) no se ven afectados.
// Ojo: el cliente es la IP remota; con ForwardedHeaders aceptando cualquier proxy, quien pueda
// llegar sin pasar por el proxy puede falsear X-Forwarded-For y rotar de IP.
public sealed class LoginThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    // Por encima de este número de clientes se purgan las entradas caducadas (memoria acotada
    // aunque un atacante rote IPs).
    private const int PruneThreshold = 1000;

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public LoginThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public bool IsBlocked(string client) =>
        _entries.TryGetValue(client, out var entry)
        && _time.GetUtcNow() - entry.WindowStart < Window
        && entry.Failures >= MaxFailures;

    public void RecordFailure(string client)
    {
        var now = _time.GetUtcNow();
        if (_entries.Count > PruneThreshold)
        {
            foreach (var (key, entry) in _entries)
            {
                if (now - entry.WindowStart >= Window)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }

        _entries.AddOrUpdate(
            client,
            _ => new Entry(now, 1),
            (_, entry) => now - entry.WindowStart >= Window ? new Entry(now, 1) : entry with { Failures = entry.Failures + 1 });
    }

    public void Reset(string client) => _entries.TryRemove(client, out _);

    private sealed record Entry(DateTimeOffset WindowStart, int Failures);
}
