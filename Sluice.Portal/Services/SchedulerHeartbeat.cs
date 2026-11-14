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

    public static TimeSpan StaleAfter(int pollMinutes)
    {
        var polls = TimeSpan.FromMinutes(StalePolls * (double)Math.Max(1, pollMinutes));
        return polls > MinStaleAfter ? polls : MinStaleAfter;
    }

    public bool IsStale(int pollMinutes) =>
        _time.GetUtcNow() - (LastPollAt ?? StartedAt) > StaleAfter(pollMinutes);
}
