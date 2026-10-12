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
    private readonly Timer _timer;
    private readonly object _lock = new();
    private string _step = "";
    private DateTimeOffset _since = DateTimeOffset.UtcNow;
    private bool _warned;

    public SqlStallWatch(TimeSpan threshold, Action<string, TimeSpan> onStall)
    {
        _threshold = threshold;
        _onStall = onStall;
        var period = threshold / 4 < MaxCheckPeriod ? threshold / 4 : MaxCheckPeriod;
        _timer = new Timer(_ => Check(), null, period, period);
    }

    // Marca avance: empieza un tramo nuevo (otra entidad, fase o lote).
    public void Touch(string step)
    {
        lock (_lock)
        {
            _step = step;
            _since = DateTimeOffset.UtcNow;
            _warned = false;
        }
    }

    private void Check()
    {
        string step;
        TimeSpan elapsed;
        lock (_lock)
        {
            elapsed = DateTimeOffset.UtcNow - _since;
            if (_warned || elapsed < _threshold)
            {
                return;
            }

            _warned = true;
            step = _step;
        }

        _onStall(step, elapsed);
    }

    public void Dispose() => _timer.Dispose();
}
