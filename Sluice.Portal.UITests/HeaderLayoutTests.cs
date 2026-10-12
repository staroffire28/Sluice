using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// Cabecera en un móvil (375px) y textos propios de Fluent UI en la cultura del usuario.
[Collection("blazor-ui")]
public sealed class HeaderLayoutTests
{
    private const int PhoneWidth = 375;

    private readonly BlazorAppFixture _fx;

    public HeaderLayoutTests(BlazorAppFixture fx) => _fx = fx;

    // Con el icono como contenido, los botones de tema y de cerrar sesión ocupaban el ancho
    // mínimo de 96px y el de cerrar sesión quedaba fuera de la pantalla.
    [Fact]
    public async Task Logout_button_fits_a_phone_width()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(PhoneWidth, 812);

        await page.GotoInteractiveAsync("/");

        var box = await page.GetByTestId("logout-button").BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.X + box.Width <= PhoneWidth, $"El botón de cerrar sesión acaba en {box.X + box.Width}px.");
    }

    // Fluent UI solo trae inglés: en español la hamburguesa decía "Menu" (SharedFluentLocalizer).
    [Fact]
    public async Task Fluent_hamburger_label_is_translated_to_spanish()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(PhoneWidth, 812);
        await page.GotoAsync("/culture/set?culture=es&redirectUri=%2F");

        await page.GotoInteractiveAsync("/");

        await Assertions.Expect(page.Locator(".fluent-layout-hamburger fluent-button").First)
            .ToHaveAttributeAsync("aria-label", "Menú");
    }

    // El JS de FluentLayout solo avisa a .NET al cruzar los 768px y no espera la llamada: si el
    // aviso de vuelta a escritorio se pierde (circuito caído tras un rato abierta), .NET se queda
    // en "móvil". Con NavigationDeferredLoading eso quitaba el menú lateral del DOM en escritorio,
    // con la hamburguesa oculta por CSS: la app quedaba sin navegación.
    [Fact]
    public async Task Side_menu_stays_visible_on_desktop_if_dotnet_believes_it_is_mobile()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1920, 1080);
        await page.GotoInteractiveAsync("/");

        await page.EvaluateAsync("""
            () => document.querySelector('.fluent-layout')._fluentDotNetHelper
                .invokeMethodAsync('FluentLayout_MediaChangedAsync', 'mobile')
            """);

        await Assertions.Expect(page.Locator("#main-menu")).ToBeVisibleAsync();
    }

    // El panel de la hamburguesa lleva su propio menú (id distinto): sin carga diferida, el
    // contenido de la navegación ya no se duplica con el mismo id.
    [Fact]
    public async Task Hamburger_panel_has_its_own_menu_on_a_phone()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(PhoneWidth, 812);
        await page.GotoInteractiveAsync("/");

        await page.Locator(".fluent-layout-hamburger fluent-button").First.ClickAsync();

        await Assertions.Expect(page.Locator("#mobile-menu")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#main-menu")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("#main-menu")).ToBeHiddenAsync();
    }
}
