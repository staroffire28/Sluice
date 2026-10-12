using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sluice.Portal.Tests;

public class CultureEndpointTests : IClassFixture<AuthWebApplicationFactory>
{
    private readonly AuthWebApplicationFactory _factory;

    public CultureEndpointTests(AuthWebApplicationFactory factory) => _factory = factory;

    private HttpClient NoRedirectClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("https://evil.com")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    public async Task CultureSet_with_non_local_redirect_falls_back_to_root_without_error(string redirect)
    {
        var response = await NoRedirectClient().GetAsync(
            $"/culture/set?culture=en&redirectUri={Uri.EscapeDataString(redirect)}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task CultureSet_with_local_redirect_preserves_path()
    {
        var response = await NoRedirectClient().GetAsync("/culture/set?culture=en&redirectUri=%2Fjobs");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/jobs", response.Headers.Location?.OriginalString);
    }

    // Regresión: App.razor tenía <html lang="en"> fijo pese a que la cookie de cultura ya
    // cambiaba el idioma de la UI (viola WCAG 3.1.1 en español, el idioma por defecto).
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task LoginPage_HtmlLang_ReflectsCultureCookie(string culture)
    {
        var client = NoRedirectClient();
        var setResponse = await client.GetAsync($"/culture/set?culture={culture}&redirectUri=%2Flogin");
        var cultureCookie = setResponse.Headers.GetValues("Set-Cookie")
            .First(h => h.StartsWith(".AspNetCore.Culture", StringComparison.Ordinal))
            .Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
        request.Headers.Add("Cookie", cultureCookie);
        var page = await client.SendAsync(request);
        page.EnsureSuccessStatusCode();

        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains($"<html lang=\"{culture}\"", html, StringComparison.OrdinalIgnoreCase);
    }
}
