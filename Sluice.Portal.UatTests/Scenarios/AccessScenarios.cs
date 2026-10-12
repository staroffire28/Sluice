using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

[Trait("Area", "Access")]
public sealed class AccessScenarios(UatAppFixture fx) : UatTest(fx)
{
    private static readonly UatBrowser Anonymous = new() { Authenticated = false };

    [Fact(DisplayName = "UAT-ACC-01 Entrar con la clave correcta")]
    public Task Sign_in_with_the_right_key() => RunAsync("UAT-ACC-01", Anonymous, async page =>
    {
        var login = new LoginPage(page);
        await login.OpenAsync();
        await login.SignInAsync(Fx.ApiKey);

        await new Shell(page).Menu.ExpectPresentAsync();
        Assert.Equal("/", new Uri(page.Url).AbsolutePath);
    });

    [Fact(DisplayName = "UAT-ACC-02 Clave incorrecta")]
    public Task Wrong_key_stays_on_login() => RunAsync("UAT-ACC-02", Anonymous, async page =>
    {
        var login = new LoginPage(page);
        await login.OpenAsync();
        await login.SignInAsync("clave-incorrecta");

        await page.ExpectMessageAsync("Invalid access key.");
        await login.Form.ExpectPresentAsync();
    });

    [Fact(DisplayName = "UAT-ACC-03 Página protegida sin sesión")]
    public Task Protected_page_redirects_to_login() => RunAsync("UAT-ACC-03", Anonymous, async page =>
    {
        await page.GotoAsync("/pipelines");

        await new LoginPage(page).Form.ExpectPresentAsync();
        Assert.Equal("/login", new Uri(page.Url).AbsolutePath);
    });

    // La landing con clave es HTML estático (sin sesión no hay circuito de Blazor): el ojo lo
    // atiende app-ui.js y tiene que funcionar igual.
    [Fact(DisplayName = "UAT-ACC-05 Mostrar y ocultar la clave")]
    public Task Show_and_hide_the_key() => RunAsync("UAT-ACC-05", Anonymous, async page =>
    {
        var login = new LoginPage(page);
        await login.OpenAsync();
        var key = page.GetByTestId("login-api-key");
        await key.FillAsync("una-clave");

        await page.ClickTestIdAsync("login-show-key");
        await Assertions.Expect(key).ToHaveAttributeAsync("type", "text");

        await page.ClickTestIdAsync("login-show-key");
        await Assertions.Expect(key).ToHaveAttributeAsync("type", "password");
        await Assertions.Expect(key).ToHaveValueAsync("una-clave");
    });

    // Idioma y tema en la landing sin sesión: sin circuito de Blazor los botones del portal no
    // hacían nada. Idioma = enlace a /culture/set; tema = el ajuste de Fluent en localStorage.
    [Fact(DisplayName = "UAT-ACC-06 Idioma y tema en la pantalla de acceso")]
    public Task Language_and_theme_work_on_the_landing() => RunAsync("UAT-ACC-06", Anonymous, async page =>
    {
        var login = new LoginPage(page);
        await login.OpenAsync();
        var heading = page.Locator(".landing__card h2");
        var before = await heading.InnerTextAsync();

        await page.ClickTestIdAsync("lang-toggle");
        await Assertions.Expect(heading).Not.ToHaveTextAsync(before, new LocatorAssertionsToHaveTextOptions { Timeout = Ui.Long });
        await login.Form.ExpectPresentAsync();

        // data-dark lo fija app-ui.js al cargar: sin él, la comparación pasaría en falso.
        var theme = page.GetByTestId("theme-toggle");
        await Assertions.Expect(theme).ToHaveAttributeAsync("data-dark", new System.Text.RegularExpressions.Regex("^(true|false)$"));
        var dark = await theme.GetAttributeAsync("data-dark");
        await theme.ClickAsync();
        await Assertions.Expect(page.GetByTestId("theme-toggle"))
            .Not.ToHaveAttributeAsync("data-dark", dark!, new LocatorAssertionsToHaveAttributeOptions { Timeout = Ui.Long });
    });

    // Sesión propia (login por la pantalla): cerrar la compartida dejaría fuera al resto.
    [Fact(DisplayName = "UAT-ACC-04 Cerrar sesión")]
    public Task Logout_ends_the_session() => RunAsync("UAT-ACC-04", Anonymous, async page =>
    {
        var login = new LoginPage(page);
        await login.OpenAsync();
        await login.SignInAsync(Fx.ApiKey);
        var shell = new Shell(page);
        await shell.Menu.ExpectPresentAsync();
        await page.Locator("[data-blazor-interactive]").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

        await shell.LogoutAsync();
        await login.Form.ExpectPresentAsync();

        await page.GotoAsync("/pipelines");
        await login.Form.ExpectPresentAsync();
    });
}
