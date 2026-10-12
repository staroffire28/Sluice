using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Middleware;
using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Tests;

public class DualAuthMiddlewareTests : IDisposable
{
    private const string UiKey = "ui-secret";

    // La clave de firma de sesión vive en DATA_DIR: el servicio que emite el token y el que lo
    // valida (dentro de AuthService) deben compartirlo.
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-dualauth-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private IOptions<AppOptions> TestOptions() => Options.Create(new AppOptions
    {
        DataDir = _dataDir,
        UiApiKey = UiKey,
        SessionExpiryHours = 12,
    });

    [Fact]
    public async Task Api_endpoint_accepts_valid_ui_session()
    {
        var auth = CreateAuthService();
        var token = new SessionTokenService(TestOptions()).CreateToken();

        var context = CreateContext("/api/status", cookies: (AuthConstants.SessionCookieName, token));

        var status = await InvokeMiddleware(context, auth);

        Assert.Equal(200, status);
    }

    [Fact]
    public async Task Health_endpoints_are_exempt()
    {
        var auth = CreateAuthService();

        var health = await InvokeMiddleware(CreateContext("/health"), auth);
        var internalHealth = await InvokeMiddleware(CreateContext("/internal/health"), auth);

        Assert.Equal(200, health);
        Assert.Equal(200, internalHealth);
    }

    // Una ruta de API cuyo último segmento acabe en .js/.css no es un estático: los estáticos
    // los sirve UseStaticFiles antes de este middleware. Antes se eximían por sufijo y
    // GET/PUT/DELETE /api/connections/{id}.js funcionaba sin sesión.
    [Theory]
    [InlineData("/api/connections/probe.js")]
    [InlineData("/api/pipelines/x.css")]
    public async Task Paths_ending_in_js_or_css_are_not_exempt(string path)
    {
        var auth = CreateAuthService();

        var status = await InvokeMiddleware(CreateContext(path), auth);

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Page_request_redirects_to_login_without_session()
    {
        var auth = CreateAuthService();
        var context = CreateContext("/");

        var status = await InvokeMiddleware(context, auth);

        Assert.Equal(302, status);
        Assert.Equal("/login", context.Response.Headers.Location.ToString());
    }

    private AuthService CreateAuthService() =>
        new(TestOptions(), new SessionTokenService(TestOptions()));

    // AUTH_MODE=none: no hay clave; la primera página de cada sesión del navegador muestra la
    // landing (/login, con "Entrar"), que deja una cookie de bienvenida y lleva al portal. La API
    // queda abierta, como en Development sin clave.
    private AuthService CreateOpenAuthService()
    {
        var options = Options.Create(new AppOptions { DataDir = _dataDir, AuthMode = AppOptions.OpenAuthMode });
        return new AuthService(options, new SessionTokenService(options));
    }

    [Fact]
    public async Task Open_mode_sends_the_first_page_of_a_session_to_the_landing()
    {
        var context = CreateContext("/pipelines");

        var status = await InvokeMiddleware(context, CreateOpenAuthService());

        Assert.Equal(302, status);
        Assert.Equal("/login", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Open_mode_lets_pages_through_after_entering()
    {
        var context = CreateContext("/pipelines", cookies: (AuthConstants.WelcomeCookieName, "1"));

        Assert.Equal(200, await InvokeMiddleware(context, CreateOpenAuthService()));
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/auth/enter")]
    [InlineData("/api/status")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_blazor/initializers")]
    public async Task Open_mode_does_not_redirect_the_landing_the_entry_or_the_api(string path)
    {
        Assert.Equal(200, await InvokeMiddleware(CreateContext(path), CreateOpenAuthService()));
    }

    private static HttpContext CreateContext(
        string path,
        (string Name, string Value)? cookies = null)
    {
        var context = new DefaultHttpContext
        {
            Request = { Path = path, Method = "GET" },
            Response = { Body = new MemoryStream() },
        };

        if (cookies is { } cookie)
        {
            context.Request.Headers.Cookie = $"{cookie.Name}={cookie.Value}";
        }

        return context;
    }

    private static async Task<int> InvokeMiddleware(HttpContext context, IAuthService auth)
    {
        var middleware = new DualAuthMiddleware(_ =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, auth);
        return context.Response.StatusCode;
    }
}