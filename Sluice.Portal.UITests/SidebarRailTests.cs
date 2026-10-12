using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// Menú lateral contraíble: expandido (250px) desde 1280px, riel de iconos (~56px) entre 769 y
// 1279px y hamburguesa en móvil. La elección manual (botón) se guarda en localStorage.
[Collection("blazor-ui")]
public sealed class SidebarRailTests
{
    private const string NavArea = ".fluent-layout-item[area=nav]";
    private const int RailPages = 13;

    private readonly BlazorAppFixture _fx;

    public SidebarRailTests(BlazorAppFixture fx) => _fx = fx;

    private async Task<IPage> OpenAsync(int width, int height, string url = "/")
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(width, height);
        await page.GotoInteractiveAsync(url);
        return page;
    }

    private static Task<double> WidthAsync(IPage page, string selector) =>
        page.EvaluateAsync<double>($"() => document.querySelector('{selector}').getBoundingClientRect().width");

    private static async Task AssertRailAsync(IPage page)
    {
        await Assertions.Expect(page.GetByTestId("nav-rail")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#main-menu")).ToBeHiddenAsync();
        // Cada página del menú sigue a un clic, con su icono a la vista.
        await Assertions.Expect(page.Locator("[data-testid=nav-rail] a svg:visible")).ToHaveCountAsync(RailPages);
        Assert.True(await WidthAsync(page, NavArea) <= 64, "El área de navegación no se estrecha al riel.");
    }

    [Fact]
    public async Task Wide_screen_starts_expanded_and_the_button_collapses_it_to_a_remembered_rail()
    {
        var page = await OpenAsync(1920, 1080);

        await Assertions.Expect(page.Locator("#main-menu")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("nav-rail")).ToBeHiddenAsync();
        Assert.Equal(250, await WidthAsync(page, NavArea), 1);
        var contentBefore = await WidthAsync(page, ".page-shell");

        await page.GetByTestId("nav-collapse").ClickAsync();

        await AssertRailAsync(page);
        var contentAfter = await WidthAsync(page, ".page-shell");
        Assert.True(contentAfter >= contentBefore + 150, $"El contenido pasa de {contentBefore}px a {contentAfter}px.");

        // La elección manual se mantiene al recargar.
        await page.ReloadAsync();
        await page.Locator("[data-blazor-interactive]").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30000 });
        await AssertRailAsync(page);

        await page.GetByTestId("nav-expand").ClickAsync();
        await Assertions.Expect(page.Locator("#main-menu")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("nav-rail")).ToBeHiddenAsync();
        await page.AssertNoBlazorErrorsAsync();
    }

    [Fact]
    public async Task Medium_screen_starts_as_rail_automatically()
    {
        var page = await OpenAsync(1100, 800);

        await AssertRailAsync(page);
        await Assertions.Expect(page.GetByTestId("nav-expand")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("nav-collapse")).ToBeHiddenAsync();
    }

    [Fact]
    public async Task Rail_icon_navigates_and_marks_its_page_active()
    {
        var page = await OpenAsync(1100, 800);
        var jobs = page.Locator("[data-testid=nav-rail] a[href='/jobs']");

        await Assertions.Expect(jobs).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex(".+"));
        await jobs.ClickAsync();

        await page.WaitForURLAsync("**/jobs");
        await Assertions.Expect(jobs).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        await Assertions.Expect(page.Locator("[data-testid=nav-rail] a[href='/']"))
            .Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
    }

    // En móvil manda la hamburguesa, aunque haya guardado un modo manual.
    [Theory]
    [InlineData(null)]
    [InlineData("rail")]
    [InlineData("expanded")]
    public async Task Phone_keeps_the_hamburger_without_horizontal_overflow(string? storedMode)
    {
        var page = await OpenAsync(375, 812);
        if (storedMode is not null)
        {
            await page.EvaluateAsync("m => localStorage.setItem('sluice-nav-mode', m)", storedMode);
            await page.GotoInteractiveAsync("/");
        }

        await Assertions.Expect(page.Locator(".fluent-layout-hamburger fluent-button").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#main-menu")).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByTestId("nav-rail")).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByTestId("nav-collapse")).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByTestId("nav-expand")).ToBeHiddenAsync();

        var pageWidth = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
        Assert.True(pageWidth <= 375, $"La página mide {pageWidth}px de ancho a 375px.");
    }
}
