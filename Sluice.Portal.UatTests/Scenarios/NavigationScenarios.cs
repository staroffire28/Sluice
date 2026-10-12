using System.Text.RegularExpressions;
using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

[Trait("Area", "Navigation")]
public sealed class NavigationScenarios(UatAppFixture fx) : UatTest(fx)
{
    // Todas las entradas del menú lateral (NavCatalog).
    public static readonly string[] MenuPages =
        ["/", "/jobs", "/pipelines", "/tables", "/sources", "/destinations", "/snapshots", "/reconcile", "/parquet", "/analytics", "/logs", "/settings", "/ayuda"];

    [Fact(DisplayName = "UAT-NAV-01 Todas las páginas abren")]
    public Task Every_menu_page_opens() => RunAsync("UAT-NAV-01", async page =>
    {
        var shell = new Shell(page);
        await page.GotoInteractiveAsync("/");
        foreach (var href in MenuPages.Reverse())
        {
            await shell.NavigateAsync(href);
            await shell.Header.ExpectPresentAsync();
            await page.GetByTestId("page-error").ExpectGoneAsync();
            await page.AssertNoBlazorErrorsAsync();
        }
    });

    [UatSqlFact(DisplayName = "UAT-NAV-02 Última ejecución en el Panel")]
    public Task Dashboard_links_the_last_run() => RunAsync("UAT-NAV-02", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "nav02", Journeys.TenRows);
        var jobId = await Journeys.RunAsync(page, journey);

        await page.GotoInteractiveAsync("/");

        await Assertions.Expect(page.GetByTestId("dashboard-last-run")).ToHaveAttributeAsync("href", $"/jobs/{jobId}", new LocatorAssertionsToHaveAttributeOptions { Timeout = Ui.Long });
        await page.GetByTestId("dashboard-last-run").ClickAsync();
        await Assertions.Expect(new JobDetailPage(page).Header).ToContainTextAsync(journey.PipelineName, new LocatorAssertionsToContainTextOptions { Timeout = Ui.Long });
    });

    [Fact(DisplayName = "UAT-NAV-03 Probar conexiones desde el Panel")]
    public Task Dashboard_tests_the_connections() => RunAsync("UAT-NAV-03", async page =>
    {
        var folder = Fx.NewSourceFolder("nav03");
        folder.DefineEntities("account");
        folder.PublishSnapshot(new EntityRows("account", 0, 2));
        var source = Unique("Origen NAV03");
        var destination = Unique("Destino NAV03");
        var pipeline = Unique("Pipeline NAV03");
        await new SourcesPage(page).CreateLocalAsync(source, folder.Container, "parquet-nav03");
        await new DestinationsPage(page).CreateAsync(destination, "sql-inexistente.invalid", "SynapseLink", "sa", "x");
        await new PipelinesPage(page).CreateAsync(pipeline, source, destination);

        await page.GotoInteractiveAsync("/");
        await page.ChooseAsync("dashboard-pipeline", pipeline);
        await page.ClickTestIdAsync("test-connections", Ui.Long);

        // Origen local correcto y destino inalcanzable: el aviso resume los dos resultados.
        await page.ExpectMessageAsync(new Regex(@"Source: OK \| Destination: "), 60_000);
    });

    [Fact(DisplayName = "UAT-NAV-04 Plegar el menú a iconos")]
    public Task Collapse_menu_to_rail() => RunAsync("UAT-NAV-04", async page =>
    {
        var shell = new Shell(page);
        await page.GotoInteractiveAsync("/pipelines");
        await Assertions.Expect(shell.Menu).ToBeVisibleAsync();

        await shell.CollapseMenuAsync();
        await Assertions.Expect(shell.Rail).ToBeVisibleAsync();
        await Assertions.Expect(shell.Menu).ToBeHiddenAsync();

        await page.GotoInteractiveAsync("/jobs");
        await Assertions.Expect(shell.Rail).ToBeVisibleAsync();

        await shell.ExpandMenuAsync();
        await Assertions.Expect(shell.Menu).ToBeVisibleAsync();
    });

    [Fact(DisplayName = "UAT-NAV-05 El menú no desaparece")]
    public Task Menu_stays_visible_while_navigating() => RunAsync("UAT-NAV-05", async page =>
    {
        var shell = new Shell(page);
        await page.GotoInteractiveAsync("/");
        foreach (var href in new[] { "/jobs", "/pipelines", "/tables", "/sources", "/", "/settings", "/" })
        {
            await shell.NavigateAsync(href);
            await Assertions.Expect(shell.Menu).ToBeVisibleAsync();
        }
    });
}
