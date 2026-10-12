using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

// Conciliación, tablas, parquet publicado y catálogo de snapshots.
[Trait("Area", "Quality")]
public sealed class QualityScenarios(UatAppFixture fx) : UatTest(fx)
{
    // Carga completa y luego alguien borra 2 de las 6 filas de la primera tabla directamente en
    // SQL (un cambio fuera del portal): la conciliación debe detectarlo.
    private async Task<Journey> DivergentAsync(IPage page, string tag)
    {
        var journey = await Journeys.CreateAsync(page, Fx, tag, Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        await Fx.RequireSql().ExecuteAsync($"DELETE TOP (2) FROM [dbo].[{journey.Entities[0]}]");
        await Journeys.RunAsync(page, journey, RunKind.Reconcile, expected: "Partial");
        return journey;
    }

    [UatSqlFact(DisplayName = "UAT-CAL-01 Divergencia detectada")]
    public Task Divergence_is_detected() => RunAsync("UAT-CAL-01", async page =>
    {
        var journey = await DivergentAsync(page, "cal01");

        var reconcile = new ReconcilePage(page);
        await reconcile.OpenAsync(journey.PipelineName);
        var row = reconcile.RowOf(journey.Entities[0]);
        await Assertions.Expect(row).ToContainTextAsync("33.33%");
        await page.AssertRowActionsFitAsync();
    });

    [UatSqlFact(DisplayName = "UAT-CAL-02 Aceptar una divergencia")]
    public Task Accept_divergence() => RunAsync("UAT-CAL-02", async page =>
    {
        var journey = await DivergentAsync(page, "cal02");
        var reconcile = new ReconcilePage(page);
        await reconcile.OpenAsync(journey.PipelineName);

        await reconcile.AcceptAsync(journey.Entities[0]);

        await reconcile.UnacceptButton(journey.Entities[0]).ExpectPresentAsync();
        await Assertions.Expect(reconcile.RowOf(journey.Entities[0])).ToContainTextAsync("Accepted");
    });

    [UatSqlFact(DisplayName = "UAT-CAL-03 Re-sincronizar una tabla")]
    public Task Resync_table() => RunAsync("UAT-CAL-03", async page =>
    {
        var journey = await DivergentAsync(page, "cal03");
        var reconcile = new ReconcilePage(page);
        await reconcile.OpenAsync(journey.PipelineName);

        var jobId = await reconcile.ResyncAsync(journey.Entities[0]);
        var detail = new JobDetailPage(page);
        await detail.OpenAsync(jobId);
        await detail.ExpectFinishedAsync();

        // Tras re-sincronizar ya no diverge: la conciliación queda en 0.00 % (la tabla solo lista
        // las entidades que divergen) y el historial registra la recarga.
        await reconcile.OpenAsync(journey.PipelineName);
        await page.ExpectMessageAsync(new System.Text.RegularExpressions.Regex(@"— 0\.00%"));
        await Assertions.Expect(page.Row(journey.Entities[0]).Last).ToContainTextAsync("reload");
    });

    // Comparar valores encuentra lo que el conteo no ve (un valor cambiado en SQL) y, con la
    // corrección automática activa, Sluice re-sincroniza la tabla solo y la deja igual al parquet.
    [UatSqlFact(DisplayName = "UAT-CAL-12 Comparar valores y corrección automática")]
    public Task Compare_values_and_auto_fix() => RunAsync("UAT-CAL-12", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal12", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        var table = journey.Entities[0];
        var reconcile = new ReconcilePage(page);
        var detail = new JobDetailPage(page);

        // Recién cargada: sin diferencias de valores.
        await CompareValuesAsync(page, journey, expected: "OK");
        await Assertions.Expect(reconcile.ValuesSummary).ToContainTextAsync("0 with differences");

        // Cambios fuera del portal: un nombre modificado y una fila que no existe en el origen.
        await Fx.RequireSql().ExecuteAsync($"""
            UPDATE TOP (1) [dbo].[{table}] SET [name] = N'cambiado fuera del portal';
            SELECT TOP (1) * INTO #extra FROM [dbo].[{table}];
            UPDATE #extra SET [Id] = NEWID();
            INSERT INTO [dbo].[{table}] SELECT * FROM #extra;
            """);

        await CompareValuesAsync(page, journey, expected: "Partial");
        await Assertions.Expect(reconcile.ValuesDifferent(table)).ToHaveTextAsync("1");
        await Assertions.Expect(reconcile.ValuesMissing(table)).ToHaveTextAsync("0");
        await Assertions.Expect(reconcile.ValuesExtra(table)).ToHaveTextAsync("1");

        var settings = new SettingsPage(page);
        try
        {
            // Con la corrección automática, la siguiente comparación lanza sola el re-sync (recarga).
            await settings.SetAutoFixAsync(true);
            await CompareValuesAsync(page, journey, expected: "Partial", entity: table);

            var fixId = await WaitForJobAsync(page, journey, "resync");
            await detail.OpenAsync(fixId);
            await detail.ExpectFinishedAsync();

            await reconcile.OpenAsync(journey.PipelineName);
            await reconcile.AutoFixInHistory(table).ExpectPresentAsync();
        }
        finally
        {
            await settings.SetAutoFixAsync(false);
        }

        // La recarga corrige el valor y borra la fila sobrante.
        await CompareValuesAsync(page, journey, expected: "OK");
        await Assertions.Expect(reconcile.ValuesSummary).ToContainTextAsync("0 with differences");
    });

    // Lanza "Comparar valores" desde Conciliación, espera el job y vuelve a la página con el resultado.
    private static async Task CompareValuesAsync(IPage page, Journey journey, string expected, string? entity = null)
    {
        var reconcile = new ReconcilePage(page);
        await reconcile.OpenAsync(journey.PipelineName);
        var jobId = await reconcile.CompareValuesAsync(entity);
        var detail = new JobDetailPage(page);
        await detail.OpenAsync(jobId);
        await detail.ExpectFinishedAsync(expected);
        await reconcile.OpenAsync(journey.PipelineName);
    }

    // Espera a que aparezca en Jobs un job del pipeline de ese tipo (p. ej. el re-sync que lanza la
    // corrección automática al cerrar otro job) y devuelve su id.
    private static async Task<string> WaitForJobAsync(IPage page, Journey journey, string kind)
    {
        var jobs = new JobsPage(page);
        var row = page.Locator($"[data-testid^='job-actions-job-{journey.PipelineId}-{kind}-']");
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await jobs.OpenAsync();
            await jobs.FilterByPipelineAsync(journey.PipelineName);
            if (await row.CountAsync() > 0)
            {
                return (await row.First.GetAttributeAsync("data-testid"))!["job-actions-".Length..];
            }

            await Task.Delay(2_000);
        }

        throw new TimeoutException($"No apareció ningún job '{kind}' del pipeline {journey.PipelineName}.");
    }

    // Una tabla con el mismo número de filas pero datos desactualizados (alguien cambió filas en
    // SQL): Conciliación no la lista, pero desde Tablas se puede re-sincronizar igualmente.
    [UatSqlFact(DisplayName = "UAT-CAL-10 Re-sincronizar desde Tablas una tabla sin divergencia")]
    public Task Resync_from_tables() => RunAsync("UAT-CAL-10", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal10", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        await Fx.RequireSql().ExecuteAsync($"UPDATE [dbo].[{journey.Entities[0]}] SET [name] = N'desactualizado'");

        var tables = new TablesPage(page);
        await tables.OpenAsync(journey.PipelineName);
        var jobId = await tables.ResyncAsync(journey.Entities[0]);

        var detail = new JobDetailPage(page);
        await detail.OpenAsync(jobId);
        await detail.ExpectFinishedAsync();
        await detail.ExpandTimelineAsync();
        await Assertions.Expect(detail.Timeline).ToContainTextAsync($"Re-sync de tabla {journey.Entities[0]} (Reload)");
        await Assertions.Expect(detail.Timeline).ToContainTextAsync($"{journey.Entities[0]}: merged 6, table count 6");
    });

    [UatSqlFact(DisplayName = "UAT-CAL-04 Tablas del pipeline")]
    public Task Tables_of_the_pipeline() => RunAsync("UAT-CAL-04", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal04", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        var tables = new TablesPage(page);
        await tables.OpenAsync(journey.PipelineName);

        await Assertions.Expect(tables.RowCount(journey.Entities[0])).ToHaveTextAsync("6");
        await Assertions.Expect(tables.RowCount(journey.Entities[1])).ToHaveTextAsync("4");
        await page.AssertRowActionsFitAsync();
    });

    [UatSqlFact(DisplayName = "UAT-CAL-05 Pausar la sincronización de una tabla")]
    public Task Pause_table_sync() => RunAsync("UAT-CAL-05", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal05", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        var tables = new TablesPage(page);
        await tables.OpenAsync(journey.PipelineName);
        await tables.PausedBadge(journey.Entities[0]).ExpectGoneAsync();

        await tables.SyncSwitch(journey.Entities[0]).ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });

        await tables.PausedBadge(journey.Entities[0]).ExpectPresentAsync();
    });

    [UatSqlFact(DisplayName = "UAT-CAL-06 Parquet publicado")]
    public Task Published_parquet() => RunAsync("UAT-CAL-06", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal06", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        var parquet = new ParquetPage(page);
        await parquet.OpenAsync(journey.PipelineName);

        foreach (var entity in journey.Entities)
        {
            await parquet.RowOf($"{journey.PipelineId}/{entity}.parquet").ExpectPresentAsync();
        }

        // Descargar baja el fichero sin salir de Parquet, y el circuito sigue vivo: el buscador responde.
        var download = await parquet.DownloadAsync($"{journey.PipelineId}/{journey.Entities[0]}.parquet");
        Assert.Equal($"{journey.Entities[0]}.parquet", download.SuggestedFilename);
        Assert.True(new FileInfo(await download.PathAsync()).Length > 0);
        Assert.Equal("/parquet", new Uri(page.Url).AbsolutePath);
        await parquet.SearchAsync(journey.Entities[1]);
        await parquet.RowOf($"{journey.PipelineId}/{journey.Entities[0]}.parquet").ExpectGoneAsync();
    });

    // Con muchas tablas, el buscador deja solo los parquet de la entidad escrita; uno que no casa
    // con nada muestra un aviso propio, distinto de "no hay parquet".
    [UatSqlFact(DisplayName = "UAT-CAL-11 Buscar una entidad en Parquet")]
    public Task Search_parquet_by_entity() => RunAsync("UAT-CAL-11", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal11", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        var parquet = new ParquetPage(page);
        await parquet.OpenAsync(journey.PipelineName);
        await parquet.RowOf($"{journey.PipelineId}/{journey.Entities[1]}.parquet").ExpectPresentAsync();

        await parquet.SearchAsync(journey.Entities[0]);

        await parquet.RowOf($"{journey.PipelineId}/{journey.Entities[0]}.parquet").ExpectPresentAsync();
        await parquet.RowOf($"{journey.PipelineId}/{journey.Entities[1]}.parquet").ExpectGoneAsync();

        await parquet.SearchAsync("entidad-que-no-existe");

        await page.ExpectMessageAsync("No parquet file matches the search.");
    });

    [UatSqlFact(DisplayName = "UAT-CAL-07 Del lienzo a Parquet")]
    public Task From_canvas_to_parquet() => RunAsync("UAT-CAL-07", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal07", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        var detail = new JobDetailPage(page);
        await detail.SelectStepAsync("consolidate");

        await detail.ParquetDetail.Locator("a").ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });

        var parquet = new ParquetPage(page);
        await Assertions.Expect(parquet.Toolbar).ToHaveAttributeAsync("data-pipeline", journey.PipelineId, new LocatorAssertionsToHaveAttributeOptions { Timeout = Ui.Long });
        await parquet.RowOf($"{journey.PipelineId}/{journey.Entities[0]}.parquet").ExpectPresentAsync();
    });

    [UatSqlFact(DisplayName = "UAT-CAL-08 Catálogo de snapshots")]
    public Task Snapshot_catalog() => RunAsync("UAT-CAL-08", async page =>
    {
        string? published = null;
        var journey = await Journeys.CreateAsync(page, Fx, "cal08", (folder, entities) =>
            published = folder.PublishSnapshot(new EntityRows(entities[0], 0, 6), new EntityRows(entities[1], 0, 4)));
        await Journeys.RunAsync(page, journey);

        var snapshots = new SnapshotsPage(page);
        await snapshots.OpenAsync(journey.PipelineName);

        await snapshots.RowOf(published!).ExpectPresentAsync();
    });

    [UatSqlFact(DisplayName = "UAT-CAL-09 Analítica de una tabla")]
    public Task Table_analytics() => RunAsync("UAT-CAL-09", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal09", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        var tables = new TablesPage(page);
        await tables.OpenAsync(journey.PipelineName);

        await tables.OpenAnalyticsAsync(journey.Entities[0]);

        // La navegación de Blazor es interna (sin evento load): se comprueba la URL, no se espera una carga.
        await Assertions.Expect(page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex($"/{journey.Entities[0]}/analytics$"),
            new PageAssertionsToHaveURLOptions { Timeout = Ui.Long });
        await new Shell(page).Header.ExpectPresentAsync();
        await page.GetByTestId("page-error").ExpectGoneAsync();
    });

    // Con un pipeline elegido, Analítica solo cuenta sus jobs (aquí, uno) y avisa de que la cola y
    // los workers son compartidos.
    [UatSqlFact(DisplayName = "UAT-CAL-13 Filtrar Analítica por pipeline")]
    public Task Filter_analytics_by_pipeline() => RunAsync("UAT-CAL-13", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "cal13", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        await page.GotoInteractiveAsync("/analytics");
        var total = page.GetByTestId("analytics-total-jobs");
        await total.ExpectPresentAsync();
        await page.GetByTestId("analytics-shared-note").ExpectGoneAsync();

        await page.ChooseAsync("analytics-pipeline", journey.PipelineName);

        await Assertions.Expect(total).ToHaveTextAsync("1", new LocatorAssertionsToHaveTextOptions { Timeout = Ui.Long });
        await page.GetByTestId("analytics-shared-note").ExpectPresentAsync();
    });
}
