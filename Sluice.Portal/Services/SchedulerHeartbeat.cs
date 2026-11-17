namespace Sluice.Portal.Services;

// Último sondeo completado por el scheduler de este proceso (el orquestador, que es también el que
// sirve /health y Ajustes). En memoria, como DeadmanService.LastPing: sin monitor externo es lo único
// que delata un bucle del scheduler que dejó de dar vueltas.
public sealed class SchedulerHeartbeat
{
    // Parado: más de tres intervalos de sondeo sin completar ninguno, con un mínimo de 15 min.
    public const int StalePolls = 3;
    public static readonly TimeSpan MinStaleAfter = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private DateTimeOffset? _lastPollAt;
    private int _waitingPollMinutes;

    public SchedulerHeartbeat(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        StartedAt = _time.GetUtcNow();
    }

    // Antes del primer sondeo el margen cuenta desde el arranque.
    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? LastPollAt
    {
        get
        {
            lock (_lock)
            {
                return _lastPollAt;
            }
        }
    }

    public void MarkPollCompleted()
    {
        lock (_lock)
        {
            _lastPollAt = _time.GetUtcNow();
        }
    }

    // Intervalo de la espera en curso del scheduler: si se baja el intervalo en Ajustes, la espera
    // ya iniciada sigue con el valor anterior y el margen debe cubrirla.
    public void MarkWaiting(int pollMinutes)
    {
        lock (_lock)
        {
            _waitingPollMinutes = pollMinutes;
        }
    }

    public static TimeSpan StaleAfter(int pollMinutes)
    {
        var polls = TimeSpan.FromMinutes(StalePolls * (double)Math.Max(1, pollMinutes));
        return polls > MinStaleAfter ? polls : MinStaleAfter;
    }

    // Margen efectivo: el mayor entre el intervalo guardado y el de la espera en curso.
    public TimeSpan CurrentStaleAfter(int savedPollMinutes)
    {
        lock (_lock)
        {
            return StaleAfter(Math.Max(savedPollMinutes, _waitingPollMinutes));
        }
    }

    public bool IsStale(int savedPollMinutes) =>
        _time.GetUtcNow() - (LastPollAt ?? StartedAt) > CurrentStaleAfter(savedPollMinutes);
}
