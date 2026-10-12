using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

[Trait("Area", "Settings")]
public sealed class SettingsScenarios(UatAppFixture fx) : UatTest(fx)
{
    [Fact(DisplayName = "UAT-CFG-01 Guardar ajustes")]
    public Task Save_settings() => RunAsync("UAT-CFG-01", async page =>
    {
        await page.GotoInteractiveAsync("/settings");

        await page.ClickTestIdAsync("settings-save", Ui.Long);

        await page.ExpectMessageAsync("Settings saved.");
    });

    [Fact(DisplayName = "UAT-CFG-02 Las acciones quedan en el registro")]
    public Task User_actions_are_logged() => RunAsync("UAT-CFG-02", async page =>
    {
        var name = Unique("Origen CFG02");
        await new SourcesPage(page).CreateLocalAsync(name, "carpeta-cfg02", "parquet-cfg02");

        await page.GotoInteractiveAsync("/logs");
        await page.FillFieldAsync("logs-search", name);

        var row = page.Row(name);
        await Assertions.Expect(row.First).ToContainTextAsync("source.save", new LocatorAssertionsToContainTextOptions { Timeout = Ui.Long });

        // La fecha cabe entera en su columna (antes "2026-10-09 2…" salía recortada).
        var date = row.First.GetByTestId("log-date");
        var fits = await date.EvaluateAsync<bool>("e => e.closest('td').scrollWidth <= e.closest('td').clientWidth");
        Assert.True(fits, $"La fecha '{await date.InnerTextAsync()}' no cabe en su columna.");
    });

    [Fact(DisplayName = "UAT-CFG-05 Filtrar el registro por fecha y exportarlo")]
    public Task Filter_logs_by_date_and_export() => RunAsync("UAT-CFG-05", async page =>
    {
        var name = Unique("Origen CFG05");
        await new SourcesPage(page).CreateLocalAsync(name, "carpeta-cfg05", "parquet-cfg05");

        await page.GotoInteractiveAsync("/logs");
        await page.FillFieldAsync("logs-search", name);
        var row = page.Row(name).First;
        var today = DateOnly.FromDateTime(DateTime.Now);

        // Desde hoy la entrada sigue; desde mañana desaparece; al limpiar las fechas, vuelve.
        await page.FillDateAsync("date-from", today);
        await row.ExpectPresentAsync();
        await page.FillDateAsync("date-from", today.AddDays(1));
        await row.ExpectGoneAsync();
        await page.ClickTestIdAsync("date-clear");
        await row.ExpectPresentAsync();

        // La exportación lleva los mismos filtros (búsqueda + fecha).
        await page.FillDateAsync("date-from", today);
        var download = await page.DownloadFromLinkAsync("logs-export");
        Assert.StartsWith("logs-", download.SuggestedFilename);
        var csv = await File.ReadAllTextAsync(await download.PathAsync());
        Assert.Contains(name, csv);
        Assert.Contains("source.save", csv);
        // La descarga no saca de la página y el circuito sigue vivo: los filtros responden.
        await page.FillDateAsync("date-from", today.AddDays(1));
        await row.ExpectGoneAsync();
    });

    [Fact(DisplayName = "UAT-CFG-06 Descargar el paquete de la Logic App")]
    public Task Download_logic_app_package() => RunAsync("UAT-CFG-06", async page =>
    {
        await page.GotoInteractiveAsync("/settings");
        await Assertions.Expect(page.GetByTestId("logicapp-card")).ToContainTextAsync("Get Sluice alerts in your inbox");

        var download = await page.DownloadFromLinkAsync("logicapp-download");

        Assert.Equal("sluice-alertas-logicapp.zip", download.SuggestedFilename);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(await download.PathAsync()))
        {
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("sluice-alertas-logicapp/azuredeploy.json", entries);
            Assert.Contains("sluice-alertas-logicapp/deploy.ps1", entries);
            Assert.Contains("sluice-alertas-logicapp/email-preview.html", entries);
        }

        // La descarga no saca de la página y el circuito sigue vivo.
        await page.ClickTestIdAsync("settings-save", Ui.Long);
        await page.ExpectMessageAsync("Settings saved.");
    });

    [Fact(DisplayName = "UAT-CFG-07 Activar la corrección automática")]
    public Task Enable_auto_fix() => RunAsync("UAT-CFG-07", async page =>
    {
        var settings = new SettingsPage(page);
        var reconcile = new ReconcilePage(page);
        try
        {
            await settings.SetAutoFixAsync(true);

            await settings.OpenAsync();
            Assert.True(await settings.IsAutoFixOnAsync(), "El ajuste no quedó guardado.");
            await page.GotoInteractiveAsync("/reconcile");
            await reconcile.AutoFixBanner.ExpectPresentAsync();
        }
        finally
        {
            // Ajuste compartido por toda la suite: se deja apagado, como estaba.
            await settings.SetAutoFixAsync(false);
        }

        await page.GotoInteractiveAsync("/reconcile");
        await reconcile.AutoFixBanner.ExpectGoneAsync();
    });

    [Fact(DisplayName = "UAT-CFG-03 Ayuda")]
    public Task Help_page() => RunAsync("UAT-CFG-03", async page =>
    {
        await page.GotoInteractiveAsync("/ayuda");

        await new Shell(page).Header.ExpectPresentAsync();
        await page.GetByTestId("page-error").ExpectGoneAsync();
        // Un solo h1 (la cabecera de la página): el título del documento se sirve como h2.
        await Assertions.Expect(page.Locator(".docs-content h2").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("h1")).ToHaveCountAsync(1);
    });
}

[Trait("Area", "Variants")]
public sealed class VariantScenarios(UatAppFixture fx) : UatTest(fx)
{
    private static readonly UatBrowser Phone = new() { Mobile = true };

    // Brillo (0-255) del fondo de la página: distingue tema claro y oscuro sin depender del CSS.
    private static Task<double> BackgroundBrightnessAsync(IPage page) =>
        page.EvaluateAsync<double>(@"() => {
            const [r, g, b] = getComputedStyle(document.body).backgroundColor.match(/\d+/g).map(Number);
            return 0.299 * r + 0.587 * g + 0.114 * b;
        }");

    [Fact(DisplayName = "UAT-VAR-01 Cambiar a español")]
    public Task Switch_to_spanish() => RunAsync("UAT-VAR-01", async page =>
    {
        var shell = new Shell(page);
        await page.GotoInteractiveAsync("/sources");
        await Assertions.Expect(shell.MenuLink("/sources")).ToContainTextAsync("Sources");

        await shell.ToggleLanguageAsync();

        await Assertions.Expect(shell.MenuLink("/sources")).ToContainTextAsync("Orígenes");
        await Assertions.Expect(shell.Header).ToContainTextAsync("Orígenes");
    });

    [Fact(DisplayName = "UAT-VAR-02 Flujo en español")]
    public Task Spanish_flow() => RunAsync("UAT-VAR-02", async page =>
    {
        await page.GotoInteractiveAsync("/sources");
        await new Shell(page).ToggleLanguageAsync();
        var name = Unique("Origen VAR02");

        var sources = new SourcesPage(page);
        await sources.StartCreateAsync();
        await sources.ChooseTypeAsync(SourcesPage.LocalType);
        await sources.SetNameAsync(name);
        await sources.SetFolderAsync("carpeta-var02");
        await sources.SetParquetFolderAsync("parquet-var02");
        await sources.SaveAsync();

        await page.ExpectMessageAsync("Guardado");
        await page.RevealAsync(sources.ActionsOf(SourcesPage.IdFor(name)));
    });

    [Fact(DisplayName = "UAT-VAR-03 Tema oscuro")]
    public Task Dark_theme() => RunAsync("UAT-VAR-03", async page =>
    {
        await page.GotoInteractiveAsync("/");
        Assert.True(await BackgroundBrightnessAsync(page) > 128, "La página debería empezar en tema claro.");

        await new Shell(page).ToggleThemeAsync();
        await page.WaitForFunctionAsync(@"() => {
            const [r, g, b] = getComputedStyle(document.body).backgroundColor.match(/\d+/g).map(Number);
            return 0.299 * r + 0.587 * g + 0.114 * b < 128;
        }", null, new PageWaitForFunctionOptions { Timeout = Ui.Short });

        await page.GotoInteractiveAsync("/");
        Assert.True(await BackgroundBrightnessAsync(page) < 128, "El tema oscuro debería mantenerse al recargar.");
    });

    [Fact(DisplayName = "UAT-VAR-04 Menú en móvil")]
    public Task Mobile_menu() => RunAsync("UAT-VAR-04", Phone, async page =>
    {
        var shell = new Shell(page);
        await page.GotoInteractiveAsync("/");
        await Assertions.Expect(shell.Menu).ToBeHiddenAsync();

        await shell.OpenMobileMenuAsync();
        await shell.MobileMenu.Locator("a[href='/pipelines']").ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });

        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/pipelines", new PageWaitForURLOptions { Timeout = Ui.Long });
        await page.GetByTestId("add-pipeline").ExpectPresentAsync();
    });

    [Fact(DisplayName = "UAT-VAR-05 Editar un pipeline en móvil")]
    public Task Edit_pipeline_on_phone() => RunAsync("UAT-VAR-05", Phone, async page =>
    {
        var source = Unique("Origen VAR05");
        var destination = Unique("Destino VAR05");
        await new SourcesPage(page).CreateLocalAsync(source, "carpeta-var05", "parquet-var05");
        await new DestinationsPage(page).CreateAsync(destination, "sql-inexistente.invalid", "SynapseLink", "sa", "x");
        var pipelines = new PipelinesPage(page);
        var id = await pipelines.CreateAsync(Unique("Pipeline VAR05"), source, destination);
        var renamed = Unique("Pipeline móvil");

        await pipelines.EditAsync(id);
        var save = await page.GetByTestId("pipe-save").BoundingBoxAsync();
        Assert.NotNull(save);
        Assert.True(save.X >= 0 && save.X + save.Width <= 375, $"El botón Guardar queda fuera de la pantalla (x={save.X}, ancho={save.Width}).");
        await pipelines.SetNameAsync(renamed);
        await pipelines.SaveAsync();

        await page.RevealAsync(page.Row(renamed));
    });

    [Fact(DisplayName = "UAT-VAR-06 Sin desbordes horizontales en móvil")]
    public Task No_horizontal_overflow_on_phone() => RunAsync("UAT-VAR-06", Phone, async page =>
    {
        var overflowing = new List<string>();
        foreach (var href in NavigationScenarios.MenuPages)
        {
            await page.GotoInteractiveAsync(href);
            await new Shell(page).Header.ExpectPresentAsync();
            var width = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
            if (width > 375)
            {
                overflowing.Add($"{href} ({width}px)");
            }
        }

        Assert.True(overflowing.Count == 0, "Páginas más anchas que 375px: " + string.Join(", ", overflowing));
    });
}
