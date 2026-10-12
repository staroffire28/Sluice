using System.Text.RegularExpressions;

namespace Sluice.Portal.UatTests.Pages;

// Historial de jobs (/jobs).
public sealed class JobsPage(IPage page)
{
    public Task OpenAsync() => page.GotoInteractiveAsync("/jobs");

    public ILocator ActionsOf(string jobId) => page.GetByTestId($"job-actions-{jobId}");

    public Task FilterByPipelineAsync(string pipelineName) => page.ChooseAsync("jobs-pipeline", pipelineName);

    public Task FilterByStatusAsync(string statusLabel) => page.ChooseAsync("jobs-status", statusLabel);

    // Descarga el informe PDF o el JSON de un job desde su menú de acciones.
    public async Task<IDownload> DownloadAsync(string jobId, bool pdf)
    {
        return await page.RunAndWaitForDownloadAsync(
            () => page.MenuAsync($"job-actions-{jobId}", pdf ? $"job-report-{jobId}" : $"job-json-{jobId}"),
            new PageRunAndWaitForDownloadOptions { Timeout = 60_000 });
    }
}

// Detalle de un job (/jobs/{id}): cabecera con estado, métricas, lienzo, entidades y eventos.
public sealed partial class JobDetailPage(IPage page)
{
    public const float JobTimeout = 180_000;

    public ILocator Header => page.GetByTestId("job-header");
    public ILocator Status => Header.Locator("fluent-badge").First;
    public ILocator Metrics => page.GetByTestId("job-metrics");
    public ILocator Alert => page.GetByTestId("job-alert");
    public ILocator Entities => page.GetByTestId("entity-grid");
    public ILocator Comparison => page.GetByTestId("entity-compare");
    public ILocator Timeline => page.GetByTestId("timeline");
    public ILocator Stage(string key) => page.GetByTestId($"flow-stage-{key}");
    public ILocator Step(string key) => page.GetByTestId($"flow-step-{key}").First;
    public ILocator ParquetDetail => page.GetByTestId("flow-detail-parquet");

    public Task OpenAsync(string jobId) => page.GotoInteractiveAsync($"/jobs/{jobId}");

    // Espera a que el job termine (la página se refresca sola cada 2 s mientras está activo) y
    // comprueba el estado final que ve el usuario.
    public async Task ExpectFinishedAsync(string expectedStatus = "OK")
    {
        await Assertions.Expect(Status).ToHaveTextAsync(Terminal(), new LocatorAssertionsToHaveTextOptions { Timeout = JobTimeout });
        var status = (await Status.InnerTextAsync()).Trim();
        if (!string.Equals(status, expectedStatus, StringComparison.Ordinal))
        {
            var alert = await Alert.CountAsync() > 0 ? await Alert.InnerTextAsync() : "(sin aviso)";
            Assert.Fail($"El job terminó en '{status}' y se esperaba '{expectedStatus}'. Aviso: {alert}");
        }
    }

    // Valor de una métrica de la cabecera por su posición (0 snapshots, 1 filas CSV, 2 filas SQL, 3 divergencia).
    public ILocator Metric(int index) => Metrics.Locator(".jd-metric__value").Nth(index);

    public Task ShowOnlyIssuesAsync() => page.ClickTestIdAsync("timeline-issues");

    // Despliega todos los grupos de la línea de tiempo para leer sus eventos.
    public async Task ExpandTimelineAsync()
    {
        // La línea de tiempo se vuelve a pintar entre clics: si un botón desaparece, se reintenta.
        var collapsed = Timeline.Locator("button[aria-expanded='false']");
        for (var attempt = 0; attempt < 20 && await collapsed.CountAsync() > 0; attempt++)
        {
            try
            {
                await collapsed.First.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
            }
            catch (TimeoutException)
            {
            }
        }
    }

    public async Task SelectStepAsync(string key) => await Step(key).ClickAsync(new LocatorClickOptions { Timeout = Ui.Long });

    public async Task RetryAsync(string confirm = "Retry")
    {
        await page.ClickTestIdAsync("retry-job", Ui.Long);
        await page.DialogButton(confirm).ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });
    }

    [GeneratedRegex(@"^\s*(OK|Partial|Error|Cancelled|Stalled|Parcial|Cancelado|Detenido)\s*$")]
    private static partial Regex Terminal();
}
