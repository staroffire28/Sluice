using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// E2E de las 3 features de UI contra el portal Blazor real. Selectores por data-testid
// (estables, independientes de idioma) sobre controles planos, evitando los menús
// desplegables de FluentUI.
//
// Dos cuidados específicos de Blazor Server + componentes web FluentUI:
//  - El HTML prerenderizado no tiene handlers hasta que conecta el circuito: se navega con
//    GotoInteractiveAsync, que espera la marca data-blazor-interactive de MainLayout.
//  - Componentes como fluent-switch/fluent-spinner pintan su visual en shadow DOM,
//    así que Playwright reporta el host como "hidden". Se afirma por presencia en el DOM
//    (ToHaveCount) en vez de por visibilidad, y se hace click forzado.
[Collection("blazor-ui")]
public sealed class UiTests
{
    private readonly BlazorAppFixture _fx;

    public UiTests(BlazorAppFixture fx) => _fx = fx;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        return await context.NewPageAsync();
    }

    // F3: al probar la conexión del pipeline aparece el FluentSpinner mientras corre.
    // El test contra ADLS/SQL inalcanzables tarda (SQL tiene Connect Timeout 5s), así que el
    // spinner está presente varios segundos.
    [Fact]
    public async Task ConnectionTest_ShowsProgressSpinner()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/");

        var testButton = page.GetByTestId("test-connections");
        await testButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30000 });
        await Assertions.Expect(testButton).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 30000 });

        await testButton.ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // Spinner en el DOM mientras corren los tests de conexión (SQL timeout ~5s).
        // No usar ToBeDisabledAsync: fluent-button pone disabled en el host y Playwright lo reporta como enabled.
        await Assertions.Expect(page.GetByTestId("test-connections-spinner"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15000 });

        await page.AssertNoBlazorErrorsAsync();
    }

    // F2: en /tables, el switch de sincronización pausa la tabla y aparece el badge "Pausada".
    [Fact]
    public async Task TablesPage_CanPauseTableSync()
    {
        var entity = BlazorAppFixture.TableNames[0];
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/tables");
        await page.SelectTablesPipelineAsync();

        var sync = page.GetByTestId($"sync-{entity}");
        await sync.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

        // Por defecto activa: el badge "Pausada" no existe aún.
        await Assertions.Expect(page.GetByTestId($"paused-{entity}")).ToHaveCountAsync(0);

        // Con el JS de FluentUI cargado, el fluent-switch responde a un click real (togglea
        // y dispara ValueChanged). Playwright auto-espera visible/estable/enabled.
        await sync.ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // Tras pausar, el badge aparece en el DOM.
        await Assertions.Expect(page.GetByTestId($"paused-{entity}"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });

        await page.AssertNoBlazorErrorsAsync();
    }

    // F1: el detalle de un job en Error muestra "Reintentar"; el de un job Ok no.
    [Fact]
    public async Task JobDetail_ShowsRetryForErrorJob_NotForOkJob()
    {
        var page = await NewPageAsync();

        await page.GotoAsync($"/jobs/{BlazorAppFixture.ErrorJobId}");
        await Assertions.Expect(page.GetByText(BlazorAppFixture.ErrorJobId).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await Assertions.Expect(page.GetByTestId("retry-job"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });

        await page.GotoAsync($"/jobs/{BlazorAppFixture.OkJobId}");
        await Assertions.Expect(page.GetByText(BlazorAppFixture.OkJobId).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await Assertions.Expect(page.GetByTestId("retry-job")).ToHaveCountAsync(0);

        await page.AssertNoBlazorErrorsAsync();
    }

    // Reconcile y Diff no rellenan Job.Entities: la comparación por entidad sale de su reporte.
    // Y un job terminado sin entidades no dice que "puede seguir en curso".
    [Fact]
    public async Task JobDetail_ReconcileAndDiff_ShowTheComparisonByEntity()
    {
        var page = await NewPageAsync();

        await page.GotoAsync($"/jobs/{BlazorAppFixture.ReconcileJobId}");
        var grid = page.GetByTestId("entity-compare");
        await Assertions.Expect(grid).ToContainTextAsync("custtable", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });
        await Assertions.Expect(grid).ToContainTextAsync("120");
        await Assertions.Expect(grid).ToContainTextAsync("118");
        await Assertions.Expect(grid).ToContainTextAsync("1.67");

        await page.GotoAsync($"/jobs/{BlazorAppFixture.DiffJobId}");
        grid = page.GetByTestId("entity-compare");
        await Assertions.Expect(grid).ToContainTextAsync("vendtable", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });
        await Assertions.Expect(grid).ToContainTextAsync("50");
        await Assertions.Expect(grid).ToContainTextAsync("47");

        await page.GotoAsync($"/jobs/{BlazorAppFixture.OkJobId}");
        await Assertions.Expect(page.GetByTestId("job-header"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await Assertions.Expect(page.GetByText("may still be in progress")).ToHaveCountAsync(0);

        await page.AssertNoBlazorErrorsAsync();
    }

    // Las métricas de arriba se ven en vivo desde progress.json mientras su etapa corre (antes
    // salían "—" hasta que la etapa escribía su Summary al final). La divergencia solo existe
    // al terminar.
    [Fact]
    public async Task JobDetail_RunningJob_ShowsLiveMetricsFromProgress()
    {
        var page = await NewPageAsync();

        await page.GotoAsync($"/jobs/{BlazorAppFixture.LiveIngestJobId}");
        var metrics = page.GetByTestId("job-metrics");
        await Assertions.Expect(metrics).ToContainTextAsync("46 / 201", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });
        await Assertions.Expect(metrics).ToContainTextAsync("987");

        await page.GotoAsync($"/jobs/{BlazorAppFixture.LiveSqlJobId}");
        metrics = page.GetByTestId("job-metrics");
        await Assertions.Expect(metrics).ToContainTextAsync("3 / 3", new LocatorAssertionsToContainTextOptions { Timeout = 30000 });
        await Assertions.Expect(metrics).ToContainTextAsync("321");
        await Assertions.Expect(metrics).ToContainTextAsync("654");

        await page.AssertNoBlazorErrorsAsync();
    }

    // Un job Partial sin FinishedAt sigue en curso (ingest parcial, el pipeline sigue con SQL):
    // muestra % y Cancelar, y no ofrece Reintentar mientras corre.
    [Fact]
    public async Task JobDetail_PartialJobStillRunning_ShowsPercentAndCancel_NotRetry()
    {
        var page = await NewPageAsync();

        await page.GotoAsync($"/jobs/{BlazorAppFixture.PartialRunningJobId}");
        await Assertions.Expect(page.GetByText(BlazorAppFixture.PartialRunningJobId).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await Assertions.Expect(page.GetByTestId("job-percent"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });
        await Assertions.Expect(page.GetByTestId("cancel-job")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("retry-job")).ToHaveCountAsync(0);

        await page.AssertNoBlazorErrorsAsync();
    }
}
