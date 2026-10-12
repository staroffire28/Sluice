using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// Ayudas de Playwright para Blazor Server + Fluent UI. Las comparten los UI tests y la suite UAT
// (Sluice.Portal.UatTests enlaza este archivo).
public static class PageExtensions
{
    // Blazor muestra #blazor-error-ui ante una excepción no controlada.
    public static async Task AssertNoBlazorErrorsAsync(this IPage page)
    {
        var error = page.Locator("#blazor-error-ui");
        if (await error.IsVisibleAsync())
        {
            Assert.Fail("Blazor error UI visible: " + await error.InnerTextAsync());
        }
    }

    // El prerender ya trae datos y controles, pero sin handlers hasta que el circuito conecta:
    // un click antes se pierde, o se deshace cuando el render interactivo reemplaza el DOM
    // (un FluentSelect abierto vuelve cerrado). MainLayout pone data-blazor-interactive en su
    // primer render interactivo; esperarlo antes de interactuar cierra esa carrera.
    public static async Task GotoInteractiveAsync(this IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.Locator("[data-blazor-interactive]").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30000 });
    }

    // Opción visible de un FluentSelect por su texto exacto. En Fluent UI v5 el rol "option" de
    // fluent-option va por ElementInternals (no como atributo), así que GetByRole no la encuentra.
    public static ILocator FluentOption(this IPage page, string name) =>
        page.Locator("fluent-option:visible").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new System.Text.RegularExpressions.Regex($@"^\s*{System.Text.RegularExpressions.Regex.Escape(name)}\s*$"),
        });

    // Botón visible de un diálogo (p. ej. la confirmación de DialogService) por su texto exacto:
    // los fluent-button de esos diálogos tampoco llevan role como atributo.
    public static ILocator DialogButton(this IPage page, string name) =>
        page.Locator("fluent-dialog fluent-button:visible").Filter(new LocatorFilterOptions
        {
            HasTextRegex = new System.Text.RegularExpressions.Regex($@"^\s*{System.Text.RegularExpressions.Regex.Escape(name)}\s*$"),
        });

    // /tables abre el primer pipeline por defecto; como otros tests crean pipelines en el
    // estado compartido, seleccionamos pipe-ui explícitamente para no depender del orden.
    public static async Task SelectTablesPipelineAsync(this IPage page, string pipelineName = "UI Pipeline")
    {
        var select = page.GetByTestId("tables-pipeline");
        await select.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        await select.ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.FluentOption(pipelineName)
            .ClickAsync(new LocatorClickOptions { Timeout = 15000 });
    }
}
