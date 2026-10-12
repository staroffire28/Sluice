namespace Sluice.Portal.UatTests.Infrastructure;

// Base de los escenarios: abre un navegador limpio por escenario, graba traza y vídeo y, si el
// escenario falla, deja en TestResults/uat/<ID>/ la traza (npx playwright show-trace), una captura
// y el vídeo. Si pasa, borra el vídeo. Al final comprueba que Blazor no mostró ningún error.
[Collection(UatCollection.Name)]
public abstract class UatTest
{
    protected UatTest(UatAppFixture fixture) => Fx = fixture;

    protected UatAppFixture Fx { get; }

    protected Task RunAsync(string id, Func<IPage, Task> scenario) => RunAsync(id, new UatBrowser(), scenario);

    protected async Task RunAsync(string id, UatBrowser browser, Func<IPage, Task> scenario)
    {
        var dir = Path.Combine(Fx.ArtifactsRoot, id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        var context = await Fx.NewContextAsync(browser, dir);
        await context.Tracing.StartAsync(new TracingStartOptions { Title = id, Screenshots = true, Snapshots = true });
        var page = await context.NewPageAsync();
        var passed = false;
        try
        {
            await scenario(page);
            await page.AssertNoBlazorErrorsAsync();
            passed = true;
        }
        catch
        {
            // Cada artefacto por separado: que falle uno no debe impedir los demás ni tapar el fallo real.
            Directory.CreateDirectory(dir);
            try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, "failure.png"), Timeout = 10_000 }); } catch { }
            try { await context.Tracing.StopAsync(new TracingStopOptions { Path = Path.Combine(dir, "trace.zip") }); } catch { }
            try { File.WriteAllText(Path.Combine(dir, "portal-tail.log"), Fx.LogTail(200)); } catch { }

            throw;
        }
        finally
        {
            if (passed)
            {
                await context.Tracing.StopAsync();
            }

            await context.CloseAsync();
            if (passed)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* vídeo aún abierto */ }
            }
        }
    }

    // Nombre único por ejecución: los escenarios comparten portal y base de datos.
    protected static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..6]}";
}
