using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

// Detalle de un job terminado e informes descargables.
[Trait("Area", "Details")]
public sealed class DetailScenarios(UatAppFixture fx) : UatTest(fx)
{
    [UatSqlFact(DisplayName = "UAT-DET-01 Métricas del job")]
    public Task Job_metrics() => RunAsync("UAT-DET-01", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "det01", Journeys.TenRows);

        await Journeys.RunAsync(page, journey);

        var detail = new JobDetailPage(page);
        await Assertions.Expect(detail.Metric(0)).ToContainTextAsync("1");
        await Assertions.Expect(detail.Metric(1)).ToHaveTextAsync("10");
        await Assertions.Expect(detail.Metric(2)).ToHaveTextAsync("10");
        await Assertions.Expect(detail.Entities).ToContainTextAsync(journey.Entities[0]);
        await Assertions.Expect(detail.Entities).ToContainTextAsync(journey.Entities[1]);
    });

    [UatSqlFact(DisplayName = "UAT-DET-02 Tabla de entidades con búsqueda")]
    public Task Entity_search() => RunAsync("UAT-DET-02", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "det02", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        var detail = new JobDetailPage(page);
        await page.Input("entity-search").FillAsync("contact");

        await Assertions.Expect(detail.Entities).ToContainTextAsync(journey.Entities[1]);
        await Assertions.Expect(detail.Entities).Not.ToContainTextAsync(journey.Entities[0]);
    });

    [UatSqlFact(DisplayName = "UAT-DET-03 Línea de tiempo: solo incidencias")]
    public Task Timeline_only_issues() => RunAsync("UAT-DET-03", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "det03", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        var detail = new JobDetailPage(page);
        await detail.Timeline.ExpectPresentAsync();
        await detail.ShowOnlyIssuesAsync();

        await detail.Timeline.ExpectGoneAsync();
    });

    [UatSqlFact(DisplayName = "UAT-DET-04 Descargar el informe PDF")]
    public Task Download_pdf_report() => RunAsync("UAT-DET-04", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "det04", Journeys.TenRows);
        var jobId = await Journeys.RunAsync(page, journey);

        var jobs = new JobsPage(page);
        await jobs.OpenAsync();
        var download = await jobs.DownloadAsync(jobId, pdf: true);

        Assert.EndsWith(".pdf", download.SuggestedFilename);
        var header = new byte[4];
        await using (var file = File.OpenRead(await download.PathAsync()))
        {
            await file.ReadExactlyAsync(header);
        }

        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(header));
    });

    [UatSqlFact(DisplayName = "UAT-DET-05 Descargar el JSON")]
    public Task Download_json() => RunAsync("UAT-DET-05", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "det05", Journeys.TenRows);
        var jobId = await Journeys.RunAsync(page, journey);

        var jobs = new JobsPage(page);
        await jobs.OpenAsync();
        var download = await jobs.DownloadAsync(jobId, pdf: false);

        Assert.Equal($"{jobId}.json", download.SuggestedFilename);
        Assert.Contains(jobId, await File.ReadAllTextAsync(await download.PathAsync()));
    });

    [UatSqlFact(DisplayName = "UAT-DET-06 Filtrar el historial")]
    public Task Filter_job_history() => RunAsync("UAT-DET-06", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "det06", Journeys.TenRows);
        var jobId = await Journeys.RunAsync(page, journey);

        var jobs = new JobsPage(page);
        await jobs.OpenAsync();
        await jobs.FilterByPipelineAsync(journey.PipelineName);

        await jobs.ActionsOf(jobId).ExpectPresentAsync();

        // La columna Job muestra el id corto (el hash) en una sola línea; el completo, en el tooltip.
        var idCell = page.GetByTestId($"job-id-{jobId}");
        await Assertions.Expect(idCell).ToHaveTextAsync(jobId[(jobId.LastIndexOf('-') + 1)..]);
        await Assertions.Expect(idCell).ToHaveAttributeAsync("title", jobId);
        Assert.Equal(1, await idCell.EvaluateAsync<int>("e => e.getClientRects().length"));

        var shown = await page.Locator("[data-testid^='job-actions-']")
            .EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-testid'))");
        Assert.All(shown, id => Assert.StartsWith($"job-actions-job-{journey.PipelineId}-", id));

        await jobs.FilterByStatusAsync("Error");
        await jobs.ActionsOf(jobId).ExpectGoneAsync();
    });

    [UatSqlFact(DisplayName = "UAT-DET-07 Filtrar el historial por fecha y exportarlo")]
    public Task Filter_job_history_by_date_and_export() => RunAsync("UAT-DET-07", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "det07", Journeys.TenRows);
        var jobId = await Journeys.RunAsync(page, journey);

        var jobs = new JobsPage(page);
        await jobs.OpenAsync();
        await jobs.FilterByPipelineAsync(journey.PipelineName);
        var today = DateOnly.FromDateTime(DateTime.Now);

        // Desde mañana no hay jobs del pipeline; hasta hoy, el job vuelve.
        await page.FillDateAsync("date-from", today.AddDays(1));
        await jobs.ActionsOf(jobId).ExpectGoneAsync();
        await page.ClickTestIdAsync("date-clear");
        await page.FillDateAsync("date-to", today);
        await jobs.ActionsOf(jobId).ExpectPresentAsync();

        // La exportación lleva los mismos filtros (pipeline + fecha).
        var download = await page.DownloadFromLinkAsync("jobs-export");
        Assert.StartsWith("jobs-", download.SuggestedFilename);
        var lines = (await File.ReadAllLinesAsync(await download.PathAsync())).Skip(1).ToArray();
        Assert.Contains(lines, l => l.StartsWith(jobId + ",", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.Contains($",{journey.PipelineId},", l));
        // La descarga no saca de la página y el circuito sigue vivo: los filtros responden.
        await page.FillDateAsync("date-from", today.AddDays(1));
        await jobs.ActionsOf(jobId).ExpectGoneAsync();
    });
}
