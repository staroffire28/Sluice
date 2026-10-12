namespace Sluice.Portal.UatTests.Pages;

// Pantalla de login (formulario HTML plano, sin Blazor interactivo).
public sealed class LoginPage(IPage page)
{
    public ILocator Form => page.GetByTestId("login-form");

    public Task OpenAsync() => page.GotoAsync("/login");

    public async Task SignInAsync(string key)
    {
        await page.GetByTestId("login-api-key").FillAsync(key, new LocatorFillOptions { Timeout = Ui.Long });
        await page.GetByTestId("login-submit").ClickAsync();
    }
}

// Lo que rodea a todas las páginas: cabecera, menú lateral, idioma, tema y salida.
public sealed class Shell(IPage page)
{
    public ILocator Menu => page.Locator("#main-menu");
    public ILocator MobileMenu => page.Locator("#mobile-menu");
    public ILocator Rail => page.GetByTestId("nav-rail");
    public ILocator Header => page.GetByTestId("page-header");

    // Enlace del menú lateral por su ruta (independiente del idioma).
    public ILocator MenuLink(string href) => Menu.Locator($"a[href='{href}']");

    public async Task NavigateAsync(string href)
    {
        await MenuLink(href).ClickAsync(new LocatorClickOptions { Timeout = Ui.Long });
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == href, new PageWaitForURLOptions { Timeout = Ui.Long });
    }

    public Task CollapseMenuAsync() => page.ClickTestIdAsync("nav-collapse");

    public Task ExpandMenuAsync() => page.ClickTestIdAsync("nav-expand");

    public Task LogoutAsync() => page.ClickTestIdAsync("logout-button", Ui.Long);

    // Cambia el idioma: navega a /culture/set y recarga la página.
    public async Task ToggleLanguageAsync()
    {
        await page.ClickTestIdAsync("lang-toggle", Ui.Long);
        await page.WaitForURLAsync(url => !url.Contains("/culture/set"), new PageWaitForURLOptions { Timeout = Ui.Long });
        await page.Locator("[data-blazor-interactive]").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = Ui.Long });
    }

    public Task ToggleThemeAsync() => page.ClickTestIdAsync("theme-toggle", Ui.Long);

    // En móvil el menú lateral se pliega en la hamburguesa de la cabecera.
    public async Task OpenMobileMenuAsync()
    {
        await page.Locator(".fluent-layout-hamburger fluent-button").First.ClickAsync(new LocatorClickOptions { Timeout = Ui.Long });
        await Assertions.Expect(MobileMenu).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = Ui.Short });
    }
}
