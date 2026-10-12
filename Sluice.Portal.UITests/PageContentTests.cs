using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// Verifica que cada página carga y BINDEA sus datos sembrados (no solo que renderiza el
// layout). Aserciones de lectura por texto; solo Tables interactúa (elige el pipeline en el
// FluentSelect), así que navega con GotoInteractiveAsync.
[Collection("blazor-ui")]
public sealed class PageContentTests
{
    private readonly BlazorAppFixture _fx;

    public PageContentTests(BlazorAppFixture fx) => _fx = fx;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        return await context.NewPageAsync();
    }

    private static LocatorAssertionsToBeVisibleOptions Slow => new() { Timeout = 30000 };

    [Fact]
    public async Task Jobs_ListsSeededJobs()
    {
        var page = await NewPageAsync();
        await page.GotoAsync("/jobs");
        await Assertions.Expect(page.GetByText(BlazorAppFixture.ErrorJobId).First).ToBeVisibleAsync(Slow);
        await Assertions.Expect(page.GetByText(BlazorAppFixture.OkJobId).First).ToBeVisibleAsync(Slow);
        await page.AssertNoBlazorErrorsAsync();
    }

    [Fact]
    public async Task Tables_ShowsSeededTables()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/tables");
        await page.SelectTablesPipelineAsync();
        await Assertions.Expect(page.GetByText(BlazorAppFixture.TableNames[0]).First).ToBeVisibleAsync(Slow);
        await Assertions.Expect(page.GetByText(BlazorAppFixture.TableNames[1]).First).ToBeVisibleAsync(Slow);
        await page.AssertNoBlazorErrorsAsync();
    }

    [Fact]
    public async Task Sources_ShowsSeededConnection()
    {
        var page = await NewPageAsync();
        await page.GotoAsync("/sources");
        await Assertions.Expect(page.GetByText("UI Source").First).ToBeVisibleAsync(Slow);
        await page.AssertNoBlazorErrorsAsync();
    }

    [Fact]
    public async Task Destinations_ShowsSeededConnection()
    {
        var page = await NewPageAsync();
        await page.GotoAsync("/destinations");
        await Assertions.Expect(page.GetByText("UI Dest").First).ToBeVisibleAsync(Slow);
        await Assertions.Expect(page.GetByText("SQL Server 2022").First).ToBeVisibleAsync(Slow);
        await page.AssertNoBlazorErrorsAsync();
    }

    [Fact]
    public async Task Pipelines_ShowsSeededPipeline()
    {
        var page = await NewPageAsync();
        await page.GotoAsync("/pipelines");
        await Assertions.Expect(page.GetByText("UI Pipeline").First).ToBeVisibleAsync(Slow);
        await page.AssertNoBlazorErrorsAsync();
    }
}
