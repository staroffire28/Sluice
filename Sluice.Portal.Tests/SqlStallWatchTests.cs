using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Aviso de posible bloqueo en la etapa SQL: las sentencias corren sin límite de tiempo, así que si
// la etapa pasa más del umbral sin avanzar se avisa (una vez por tramo). El umbral real es 1 h;
// aquí se usan milisegundos.
public sealed class SqlStallWatchTests
{
    [Fact]
    public async Task Warns_once_when_a_step_exceeds_the_threshold()
    {
        var stalls = new List<(string Step, TimeSpan Elapsed)>();
        using var watch = new SqlStallWatch(TimeSpan.FromMilliseconds(200), (step, elapsed) =>
        {
            lock (stalls)
            {
                stalls.Add((step, elapsed));
            }
        });

        watch.Touch("account · orphans");
        await WaitUntilAsync(() => Count(stalls) > 0);
        await Task.Delay(400);

        var stall = Assert.Single(stalls);
        Assert.Equal("account · orphans", stall.Step);
        Assert.True(stall.Elapsed >= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task A_step_that_keeps_advancing_does_not_warn()
    {
        var stalls = 0;
        using var watch = new SqlStallWatch(TimeSpan.FromMilliseconds(400), (_, _) => Interlocked.Increment(ref stalls));

        for (var i = 0; i < 12; i++)
        {
            watch.Touch($"account · merge {i}");
            await Task.Delay(50);
        }

        Assert.Equal(0, Volatile.Read(ref stalls));
    }

    // Tras un aviso, avanzar a otro tramo vuelve a armarlo: un segundo atasco también se avisa.
    [Fact]
    public async Task Advancing_after_a_warning_rearms_it()
    {
        var stalls = new List<(string Step, TimeSpan Elapsed)>();
        using var watch = new SqlStallWatch(TimeSpan.FromMilliseconds(150), (step, elapsed) =>
        {
            lock (stalls)
            {
                stalls.Add((step, elapsed));
            }
        });

        watch.Touch("account · merge");
        await WaitUntilAsync(() => Count(stalls) == 1);
        watch.Touch("account · count");
        await WaitUntilAsync(() => Count(stalls) == 2);

        Assert.Equal(["account · merge", "account · count"], stalls.Select(s => s.Step));
    }

    private static int Count(List<(string, TimeSpan)> stalls)
    {
        lock (stalls)
        {
            return stalls.Count;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "El aviso no llegó a tiempo.");
            await Task.Delay(20);
        }
    }
}
