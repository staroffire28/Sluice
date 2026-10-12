using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Tests;

// /auth/login no limitaba los intentos: la UI_API_KEY se podía probar por fuerza bruta.
// Solo cuentan los intentos fallidos, por cliente, dentro de una ventana.
public sealed class LoginThrottleTests
{
    private const string Client = "10.0.0.7";

    [Fact]
    public void Blocks_a_client_after_too_many_failures()
    {
        var throttle = new LoginThrottle(new FixedTimeProvider());

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.False(throttle.IsBlocked(Client));
            throttle.RecordFailure(Client);
        }

        Assert.True(throttle.IsBlocked(Client));
        Assert.False(throttle.IsBlocked("10.0.0.8")); // otro cliente no se ve afectado
    }

    [Fact]
    public void A_successful_login_resets_the_count()
    {
        var throttle = new LoginThrottle(new FixedTimeProvider());
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure(Client);
        }

        throttle.Reset(Client);
        throttle.RecordFailure(Client);

        Assert.False(throttle.IsBlocked(Client));
    }

    [Fact]
    public void The_block_expires_with_the_window()
    {
        var time = new FixedTimeProvider();
        var throttle = new LoginThrottle(time);
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure(Client);
        }

        time.Advance(LoginThrottle.Window);

        Assert.False(throttle.IsBlocked(Client));
    }
}
