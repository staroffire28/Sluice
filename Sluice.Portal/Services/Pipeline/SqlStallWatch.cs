using Microsoft.Extensions.Logging;

namespace Sluice.Portal.Services.Pipeline;

// Aviso de posible bloqueo en la etapa SQL. Las sentencias corren sin CommandTimeout (un MERGE, un
// borrado de huérfanas o un índice sobre una tabla grande pueden tardar mucho), así que en vez de
// cortarlas se avisa: si la etapa pasa más del umbral sin avanzar (sin un Touch), se llama a onStall
// una vez por tramo. Un Touch posterior vuelve a armarlo.
public sealed class SqlStallWatch : IDisposable
{
    private static readonly TimeSpan MaxCheckPeriod = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _threshold;
    private readonly Action<string, TimeSpan> _onStall;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _lock = new();
    private string _step = "";
    private DateTimeOffset _since;
    private bool _warned;

    // timeProvider: el reloj y el temporizador; los tests pasan uno falso para no depender de esperas reales.
    public SqlStallWatch(TimeSpan threshold, Action<string, TimeSpan> onStall, ILogger logger, TimeProvider? timeProvider = null)
    {
        _threshold = threshold;
        _onStall = onStall;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _since = _time.GetUtcNow();
        var period = threshold / 4 < MaxCheckPeriod ? threshold / 4 : MaxCheckPeriod;
        _timer = _time.CreateTimer(_ => Check(), null, period, period);
    }

    // Marca avance: empieza un tramo nuevo (otra entidad, fase o lote).
    public void Touch(string step)
    {
        lock (_lock)
        {
            _step = step;
            _since = _time.GetUtcNow();
            _warned = false;
        }
    }

    private void Check()
    {
        string step;
        TimeSpan elapsed;
        lock (_lock)
        {
            elapsed = _time.GetUtcNow() - _since;
            if (_warned || elapsed < _threshold)
            {
                return;
            }

            _warned = true;
            step = _step;
        }

        // Avisar escribe en el job (lock con timeout, IO) y puede lanzar. Una excepción sin capturar en
        // el callback de un Timer termina el proceso: el worker caería en mitad del MERGE vigilado.
        try
        {
            _onStall(step, elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo avisar del posible bloqueo en {Step}.", step);
        }
    }

    public void Dispose() => _timer.Dispose();
}
