using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Tests;

public sealed class AuthServiceTests : IDisposable
{
    private const string UiKey = "ui-secret";
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-authsvc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    // El portal va detrás de un proxy que termina TLS: la petición llega por HTTP al proceso, y
    // con Secure = Request.IsHttps la cookie de sesión salía sin "secure" en producción.
    [Fact]
    public void Outside_development_the_session_cookie_is_always_secure()
    {
        var context = new DefaultHttpContext();

        Assert.True(CreateService(Environments.Production).TryLogin(UiKey, context));

        Assert.Contains("secure", SetCookie(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void In_development_over_http_the_cookie_is_not_forced_secure()
    {
        var context = new DefaultHttpContext();

        Assert.True(CreateService(Environments.Development).TryLogin(UiKey, context));

        Assert.DoesNotContain("secure", SetCookie(context), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignOut_invalidates_the_existing_session()
    {
        var auth = CreateService(Environments.Production);
        var login = new DefaultHttpContext();
        auth.TryLogin(UiKey, login);
        var token = SetCookie(login).Split(';')[0].Split('=', 2)[1];

        auth.SignOut(new DefaultHttpContext());

        var reuse = new DefaultHttpContext();
        reuse.Request.Headers.Cookie = $"{AuthConstants.SessionCookieName}={token}";
        Assert.False(auth.HasValidUiSession(reuse));
    }

    // Acceso abierto en una red interna suele ir por http://10.x.x.x sin TLS: una cookie de
    // bienvenida "secure" no volvería nunca y la landing reaparecería en cada página (bucle).
    // No es una credencial, así que no se fuerza Secure; Lax para que un enlace de una alerta no
    // vuelva a pasar por la landing.
    [Fact]
    public void The_open_access_welcome_cookie_is_not_forced_secure_over_http()
    {
        var context = new DefaultHttpContext();

        CreateService(Environments.Production).Enter(context);

        var cookie = SetCookie(context);
        Assert.Contains(AuthConstants.WelcomeCookieName, cookie);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private AuthService CreateService(string environment)
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir, UiApiKey = UiKey });
        return new AuthService(options, new SessionTokenService(options), new FakeHostEnvironment(environment));
    }

    private static string SetCookie(HttpContext context) => context.Response.Headers.SetCookie.ToString();

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Sluice.Portal";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
