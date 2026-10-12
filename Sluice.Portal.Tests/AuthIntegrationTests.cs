using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Tests;

public class AuthIntegrationTests : IClassFixture<AuthWebApplicationFactory>
{
    private readonly AuthWebApplicationFactory _factory;

    public AuthIntegrationTests(AuthWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_with_valid_ui_key_sets_session_cookie_and_redirects()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsJsonAsync("/auth/login", new { Key = AuthWebApplicationFactory.UiKey });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("sluice_session=", response.Headers.GetValues("Set-Cookie").First());
    }

    [Fact]
    public async Task Login_with_invalid_key_returns_unauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new { Key = "wrong-key" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Los estáticos de la pantalla de login deben servirse sin sesión: UseStaticFiles corre
    // antes del middleware de auth, así que no dependen de ninguna exención por sufijo.
    [Theory]
    [InlineData("/app.css")]
    [InlineData("/app-ui.js")]
    public async Task Static_assets_are_served_without_session(string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Instancia propia de la app: en TestServer todas las peticiones llegan sin IP remota y
    // comparten el mismo cliente del limitador (el fixture compartido solo hace 1 intento fallido).
    [Fact]
    public async Task Too_many_failed_logins_are_throttled_with_429()
    {
        using var factory = new AuthWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            var failed = await client.PostAsJsonAsync("/auth/login", new { Key = "wrong-key" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var blocked = await client.PostAsJsonAsync("/auth/login", new { Key = AuthWebApplicationFactory.UiKey });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode); // incluso con la clave correcta
    }

    [Fact]
    public async Task A_session_cookie_stops_working_after_sign_out()
    {
        using var factory = new AuthWebApplicationFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var login = await client.PostAsJsonAsync("/auth/login", new { Key = AuthWebApplicationFactory.UiKey });
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        factory.Services.GetRequiredService<IAuthService>().SignOut(new DefaultHttpContext());

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Api_status_requires_session()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_status_works_after_login()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
        });

        await client.PostAsJsonAsync("/auth/login", new { Key = AuthWebApplicationFactory.UiKey });
        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Form_login_without_antiforgery_token_is_rejected()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsync(
            "/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = AuthWebApplicationFactory.UiKey }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login?error=csrf", response.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Internal_health_is_anonymous()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/health");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}