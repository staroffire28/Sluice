using Sluice.Portal.Components.Shared;

namespace Sluice.Portal.Tests;

// Timer de refresco de las páginas. Antes: con shouldContinue=false el bucle terminaba para
// siempre (el Dashboard abierto sin jobs dejaba de refrescarse a los 3 s y nunca veía los
// nuevos), y una excepción del callback mataba el bucle sin avisar.
public sealed class PageRefreshTimerTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task Idle_page_keeps_polling_slowly_instead_of_stopping()
    {
        var calls = 0;
        await using var timer = new PageRefreshTimer(
            Fast,
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            shouldContinue: () => false,
            idleInterval: TimeSpan.FromMilliseconds(20));

        timer.Start();

        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 2);
    }

    // JobDetail: un job terminado no vuelve a arrancar, así que sin idleInterval se para.
    [Fact]
    public async Task Without_idle_interval_it_stops_when_there_is_nothing_to_watch()
    {
        var calls = 0;
        await using var timer = new PageRefreshTimer(
            Fast,
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            shouldContinue: () => false);

        timer.Start();
        await Task.Delay(200);

        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task A_failing_refresh_is_reported_and_does_not_stop_the_loop()
    {
        var calls = 0;
        var errors = 0;
        await using var timer = new PageRefreshTimer(
            Fast,
            _ => Interlocked.Increment(ref calls) == 1
                ? throw new IOException("job.json bloqueado")
                : Task.CompletedTask,
            onError: _ => Interlocked.Increment(ref errors));

        timer.Start();

        await WaitUntilAsync(() => Volatile.Read(ref calls) >= 3);
        Assert.Equal(1, Volatile.Read(ref errors));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "El timer dejó de refrescar.");
            await Task.Delay(10);
        }
    }
}
