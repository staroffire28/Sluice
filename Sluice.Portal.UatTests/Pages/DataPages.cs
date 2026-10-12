using System.Text.RegularExpressions;

namespace Sluice.Portal.UatTests.Pages;

// Re-sincronizar una tabla desde Tablas o Conciliación: botón de la fila, confirmación ("Run") y el
// id del job que anuncia el aviso.
internal static partial class ResyncFlow
{
    // openAction pulsa la acción (un botón de la fila o una opción de su menú).
    public static async Task<string> RunAsync(IPage page, Func<Task> openAction, string confirm)
    {
        var dialogButton = page.DialogButton(confirm);
        await ClickUntilAsync(openAction, dialogButton);
        await dialogButton.ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });
        return await JobIdFromToastAsync(page);
    }

    // Acción sin confirmación (p. ej. Comparar valores): devuelve el id del job que anuncia el aviso.
    // Repetirla es seguro: si el primer clic sí llegó, la guarda anti doble run rechaza el segundo.
    public static async Task<string> StartAsync(IPage page, Func<Task> action)
    {
        await ClickUntilAsync(action, page.GetByText(JobId()).First);
        return await JobIdFromToastAsync(page);
    }

    // Un clic justo después de elegir el pipeline puede caer en el re-render de la tabla: Blazor
    // descarta el evento del botón sustituido sin error. Si no se ve el efecto, se vuelve a pulsar,
    // como haría el usuario.
    public static async Task ClickUntilAsync(Func<Task> click, ILocator effect)
    {
        await click();
        try
        {
            await effect.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            await click();
        }
    }

    private static async Task<string> JobIdFromToastAsync(IPage page)
    {
        var toast = page.GetByText(JobId()).First;
        await toast.WaitForAsync(new LocatorWaitForOptions { Timeout = Ui.Long });
        return JobId().Match(await toast.InnerTextAsync()).Value;
    }

    [GeneratedRegex(@"job-[a-z0-9-]+[a-z0-9]")]
    private static partial Regex JobId();
}

// Tablas (/tables): filas, marca de agua y pausa de sincronización por entidad.
public sealed class TablesPage(IPage page)
{
    public async Task OpenAsync(string pipelineName)
    {
        await page.GotoInteractiveAsync("/tables");
        await page.ChooseAsync("tables-pipeline", pipelineName);
    }

    public ILocator RowOf(string entity) => page.Row(entity);

    // Celda "Filas" de la entidad (segunda columna).
    public ILocator RowCount(string entity) => RowOf(entity).Locator("td").Nth(1);

    public ILocator SyncSwitch(string entity) => page.GetByTestId($"sync-{entity}");
    public ILocator PausedBadge(string entity) => page.GetByTestId($"paused-{entity}");

    // Las acciones de cada tabla están en su menú de tres puntos.
    public Task OpenAnalyticsAsync(string entity) => page.MenuAsync($"table-actions-{entity}", $"analytics-{entity}");

    // Re-sincroniza la tabla (confirmación "Run") y devuelve el id del job que anuncia el aviso.
    public Task<string> ResyncAsync(string entity, string confirm = "Run") =>
        ResyncFlow.RunAsync(page, () => page.MenuAsync($"table-actions-{entity}", $"resync-{entity}"), confirm);
}

// Conciliación (/reconcile): última conciliación por entidad, aceptar y re-sincronizar.
public sealed class ReconcilePage(IPage page)
{
    public async Task OpenAsync(string pipelineName)
    {
        await page.GotoInteractiveAsync("/reconcile");
        await page.ChooseAsync("recon-pipeline", pipelineName);
    }

    // Fila de la última conciliación (primera tabla); debajo está el historial de remediaciones,
    // que también nombra la entidad.
    public ILocator RowOf(string entity) =>
        page.Locator("table").First.Locator("tr").Filter(new LocatorFilterOptions { HasTextString = entity });

    // Aceptar es idempotente: si el clic se pierde en un re-render, se repite.
    public Task AcceptAsync(string entity) =>
        ResyncFlow.ClickUntilAsync(() => page.ClickTestIdAsync($"recon-accept-{entity}", Ui.Long), UnacceptButton(entity));

    public ILocator UnacceptButton(string entity) => page.GetByTestId($"recon-unaccept-{entity}");

    // Re-sincroniza la tabla (confirmación "Run") y devuelve el id del job que anuncia el aviso.
    public Task<string> ResyncAsync(string entity, string confirm = "Run") =>
        ResyncFlow.RunAsync(page, () => page.ClickTestIdAsync($"recon-resync-{entity}", Ui.Long), confirm);

    // Comparar valores de todo el pipeline (barra) o de una tabla ya listada; devuelve el id del job.
    public Task<string> CompareValuesAsync(string? entity = null) =>
        ResyncFlow.StartAsync(page, () => page.ClickTestIdAsync(entity is null ? "recon-compare-values" : $"values-compare-{entity}", Ui.Long));

    public ILocator ValuesSummary => page.GetByTestId("values-summary");

    // Contadores de la última comparación de valores para una tabla con diferencias.
    public ILocator ValuesDifferent(string entity) => page.GetByTestId($"values-different-{entity}");
    public ILocator ValuesMissing(string entity) => page.GetByTestId($"values-missing-{entity}");
    public ILocator ValuesExtra(string entity) => page.GetByTestId($"values-extra-{entity}");

    // Insignia "Automática" del historial de remediación.
    public ILocator AutoFixInHistory(string entity) => page.GetByTestId($"history-auto-{entity}");

    public ILocator AutoFixBanner => page.GetByTestId("recon-autofix-on");
}

// Parquet (/parquet): ficheros publicados por el ingest.
public sealed class ParquetPage(IPage page)
{
    public ILocator Toolbar => page.GetByTestId("parquet-page");

    public async Task OpenAsync(string pipelineName)
    {
        await page.GotoInteractiveAsync("/parquet");
        await page.ChooseAsync("parquet-pipeline", pipelineName);
    }

    public ILocator RowOf(string file) => page.Row(file);

    public Task SearchAsync(string text) => page.FillFieldAsync("parquet-search", text);

    public Task<IDownload> DownloadAsync(string file) => page.DownloadFromLinkAsync($"parquet-download-{file}");
}

// Snapshots (/snapshots): catálogo del origen.
public sealed class SnapshotsPage(IPage page)
{
    public async Task OpenAsync(string pipelineName)
    {
        await page.GotoInteractiveAsync("/snapshots");
        await page.ChooseAsync("snapshots-pipeline", pipelineName);
    }

    public ILocator RowOf(string snapshot) => page.Row(snapshot);
}
