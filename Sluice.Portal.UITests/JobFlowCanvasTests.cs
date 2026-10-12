using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// Lienzo de etapas del detalle de job. Los jobs del fixture son "viejos": sin tiempos por
// paso ni progress.json, así que el flujo sale solo de StageStatus/eventos y debe verse igual.
[Collection("blazor-ui")]
public sealed class JobFlowCanvasTests
{
    private const int PhoneWidth = 375;

    // Incremental: 4 pasos de ingesta + 2 de SQL + 2 de conciliación (JobStepCatalog).
    private const int IncrementalSteps = 8;

    private readonly BlazorAppFixture _fx;

    public JobFlowCanvasTests(BlazorAppFixture fx) => _fx = fx;

    private async Task<IPage> OpenAsync(string jobId, int width = 1280)
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(width, 900);
        await page.GotoInteractiveAsync($"/jobs/{jobId}");
        await page.GetByTestId("job-flow").WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        return page;
    }

    [Fact]
    public async Task Error_job_shows_every_step_and_the_stages_it_never_reached()
    {
        var page = await OpenAsync(BlazorAppFixture.ErrorJobId);

        await Assertions.Expect(page.Locator("[data-testid^='flow-step-']")).ToHaveCountAsync(IncrementalSteps);
        await Assertions.Expect(page.GetByTestId("flow-stage-ingest")).ToHaveAttributeAsync("data-status", "error");
        await Assertions.Expect(page.GetByTestId("flow-stage-sql")).ToHaveAttributeAsync("data-status", "notreached");
        await Assertions.Expect(page.GetByTestId("flow-stage-reconcile")).ToHaveAttributeAsync("data-status", "notreached");

        // Un paso con error queda seleccionado por defecto y su detalle visible.
        await Assertions.Expect(page.Locator("[data-testid^='flow-step-'][data-status='error'][aria-pressed='true']")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("flow-detail")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("job-alert")).ToHaveCountAsync(1);
        await page.AssertNoBlazorErrorsAsync();
    }

    [Fact]
    public async Task Selecting_a_step_shows_its_detail()
    {
        var page = await OpenAsync(BlazorAppFixture.ErrorJobId);
        var step = page.GetByTestId("flow-step-report");

        await step.ClickAsync();

        await Assertions.Expect(step).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(page.Locator("[data-testid^='flow-step-'][aria-pressed='true']")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("flow-detail").Locator("h3"))
            .ToHaveTextAsync(await step.Locator(".jfc-node__label").InnerTextAsync());
    }

    // El usuario no sabía dónde se creaba el parquet que ve en la conciliación: el detalle de
    // "Consolidar y publicar en ADLS" y el de "Comparar" muestran la ruta y enlazan a Parquet.
    [Theory]
    [InlineData("consolidate")]
    [InlineData("compare")]
    public async Task Publish_and_compare_steps_show_where_the_parquet_lives(string stepKey)
    {
        var page = await OpenAsync(BlazorAppFixture.OkJobId);

        await page.GetByTestId($"flow-step-{stepKey}").ClickAsync();

        var location = page.GetByTestId("flow-detail-parquet");
        await Assertions.Expect(location).ToContainTextAsync($"uitestbogus/p/{BlazorAppFixture.PipelineId}/");
        await Assertions.Expect(location.Locator("a"))
            .ToHaveAttributeAsync("href", $"/parquet?pipeline={BlazorAppFixture.PipelineId}");
    }

    // El enlace abre Parquet con ese pipeline ya elegido (no el primero de la lista).
    [Fact]
    public async Task Parquet_page_preselects_the_pipeline_from_the_query()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoInteractiveAsync($"/parquet?pipeline={BlazorAppFixture.EditablePipelineId}");

        await Assertions.Expect(page.GetByTestId("parquet-page"))
            .ToHaveAttributeAsync("data-pipeline", BlazorAppFixture.EditablePipelineId);
    }

    [Fact]
    public async Task Ok_job_has_every_stage_done_and_no_alert()
    {
        var page = await OpenAsync(BlazorAppFixture.OkJobId);

        foreach (var stage in new[] { "ingest", "sql", "reconcile" })
        {
            await Assertions.Expect(page.GetByTestId($"flow-stage-{stage}")).ToHaveAttributeAsync("data-status", "done");
        }

        await Assertions.Expect(page.GetByTestId("job-alert")).ToHaveCountAsync(0);
    }

    // Una lectura fallida de progress.json (IOException, escritura a medias) no debe hacer que el
    // lienzo parpadee con los pasos heredando el estado de la etapa: mientras el job sigue en
    // curso se conserva el último progreso válido.
    [Fact]
    public async Task Transient_progress_read_failure_keeps_the_last_valid_steps()
    {
        var page = await OpenAsync(BlazorAppFixture.ProgressJobId);
        var prepare = page.GetByTestId("flow-step-prepare");
        await Assertions.Expect(prepare).ToHaveAttributeAsync("data-status", "done");
        await Assertions.Expect(page.GetByTestId("flow-step-merge")).ToHaveAttributeAsync("data-status", "running");

        await File.WriteAllTextAsync(_fx.ProgressPath(BlazorAppFixture.ProgressJobId), "{ not json");
        // El detalle refresca cada 2 s: se dejan pasar varios refrescos.
        await page.WaitForTimeoutAsync(5000);

        await Assertions.Expect(prepare).ToHaveAttributeAsync("data-status", "done");
        await page.AssertNoBlazorErrorsAsync();
    }

    // En móvil los carriles se apilan: ni el lienzo ni la página desbordan en horizontal.
    [Fact]
    public async Task Flow_canvas_fits_a_phone_width()
    {
        var page = await OpenAsync(BlazorAppFixture.ErrorJobId, PhoneWidth);

        var pageWidth = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
        Assert.True(pageWidth <= PhoneWidth, $"La página mide {pageWidth}px de ancho a {PhoneWidth}px.");

        var box = await page.GetByTestId("job-flow").BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.X + box.Width <= PhoneWidth, $"El lienzo acaba en {box.X + box.Width}px.");

        var reconcile = await page.GetByTestId("flow-stage-reconcile").BoundingBoxAsync();
        var ingest = await page.GetByTestId("flow-stage-ingest").BoundingBoxAsync();
        Assert.NotNull(reconcile);
        Assert.NotNull(ingest);
        Assert.True(reconcile.Y > ingest.Y + ingest.Height, "En móvil los carriles deben ir uno debajo de otro.");
    }
}
