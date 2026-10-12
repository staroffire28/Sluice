namespace Sluice.Portal.Components.Shared;

public sealed class PageRefreshTimer : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _callback;
    private readonly TimeSpan _interval;
    private readonly Func<bool>? _shouldContinue;
    private readonly TimeSpan? _idleInterval;
    private readonly Action<Exception>? _onError;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // shouldContinue: si devuelve false no hay nada que vigilar. Sin idleInterval el bucle se
    // detiene (JobDetail: un job terminado no vuelve a arrancar); con idleInterval sigue
    // refrescando a ese ritmo lento hasta que vuelva a haber actividad (Dashboard: un job
    // lanzado por el scheduler u otro usuario debe aparecer sin recargar).
    // onError: un fallo de un refresco se reporta y el bucle continúa en el siguiente tick.
    public PageRefreshTimer(
        TimeSpan interval,
        Func<CancellationToken, Task> callback,
        Func<bool>? shouldContinue = null,
        TimeSpan? idleInterval = null,
        Action<Exception>? onError = null)
    {
        _interval = interval;
        _callback = callback;
        _shouldContinue = shouldContinue;
        _idleInterval = idleInterval;
        _onError = onError;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(_interval);
        // El bucle recibe SU timer: tras un Start() posterior no debe usar el nuevo.
        _loop = RunAsync(_timer, _cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
    }

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _timer?.Dispose();
        _timer = null;
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            if (_shouldContinue?.Invoke() == false)
            {
                if (_idleInterval is not { } idle)
                {
                    break;
                }

                await Task.Delay(idle, ct);
            }

            try
            {
                await _callback(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _onError?.Invoke(ex);
            }
        }
    }
}
