using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

[Trait("Area", "Pipelines")]
public sealed class PipelineScenarios(UatAppFixture fx) : UatTest(fx)
{
    // Origen y destino propios del escenario (sin SQL real: no se ejecuta nada).
    private static async Task<(string Source, string Destination)> ConnectionsAsync(IPage page, string tag)
    {
        var source = Unique($"Origen {tag}");
        var destination = Unique($"Destino {tag}");
        await new SourcesPage(page).CreateLocalAsync(source, $"carpeta-{tag}", $"parquet-{tag}");
        await new DestinationsPage(page).CreateAsync(destination, "sql-inexistente.invalid", "SynapseLink", "sa", "x");
        return (source, destination);
    }

    [Fact(DisplayName = "UAT-PIP-01 Crear un pipeline")]
    public Task Create_pipeline() => RunAsync("UAT-PIP-01", async page =>
    {
        var (source, destination) = await ConnectionsAsync(page, "pip01");
        var name = Unique("Pipeline PIP01");

        var id = await new PipelinesPage(page).CreateAsync(name, source, destination);

        var row = page.Row(name);
        await Assertions.Expect(row).ToContainTextAsync(SourcesPage.IdFor(source));
        await Assertions.Expect(row).ToContainTextAsync(DestinationsPage.IdFor(destination));
        await new PipelinesPage(page).SchedulerBadge(id, on: false).ExpectPresentAsync();
    });

    // El caso del PR #31: el diálogo se reutiliza y mostraba el origen/destino del pipeline anterior.
    [Fact(DisplayName = "UAT-PIP-02 Editar A y luego B muestra el origen y destino de cada uno")]
    public Task Editing_two_pipelines_shows_each_ones_connections() => RunAsync("UAT-PIP-02", async page =>
    {
        var (sourceA, destA) = await ConnectionsAsync(page, "pip02a");
        var (sourceB, destB) = await ConnectionsAsync(page, "pip02b");
        var pipelines = new PipelinesPage(page);
        var idA = await pipelines.CreateAsync(Unique("Pipeline A"), sourceA, destA);
        var idB = await pipelines.CreateAsync(Unique("Pipeline B"), sourceB, destB);

        await pipelines.EditAsync(idA);
        await Assertions.Expect(pipelines.SelectedSource).ToHaveTextAsync(sourceA);
        await Assertions.Expect(pipelines.SelectedDestination).ToHaveTextAsync(destA);
        await pipelines.CancelAsync();

        await pipelines.EditAsync(idB);
        await Assertions.Expect(pipelines.SelectedSource).ToHaveTextAsync(sourceB);
        await Assertions.Expect(pipelines.SelectedDestination).ToHaveTextAsync(destB);
        await pipelines.CancelAsync();
    });

    [Fact(DisplayName = "UAT-PIP-03 Guardar sin cambios no reasigna")]
    public Task Saving_without_changes_keeps_connections() => RunAsync("UAT-PIP-03", async page =>
    {
        var (sourceA, destA) = await ConnectionsAsync(page, "pip03a");
        var (sourceB, destB) = await ConnectionsAsync(page, "pip03b");
        var pipelines = new PipelinesPage(page);
        var idA = await pipelines.CreateAsync(Unique("Pipeline A"), sourceA, destA);
        var nameB = Unique("Pipeline B");
        var idB = await pipelines.CreateAsync(nameB, sourceB, destB);

        await pipelines.EditAsync(idA);
        await pipelines.CancelAsync();
        await pipelines.EditAsync(idB);
        await pipelines.SaveAsync();

        var row = page.Row(nameB);
        await page.RevealAsync(row);
        await Assertions.Expect(row).ToContainTextAsync(SourcesPage.IdFor(sourceB));
        await Assertions.Expect(row).ToContainTextAsync(DestinationsPage.IdFor(destB));
    });

    [Fact(DisplayName = "UAT-PIP-04 Cambiar el destino")]
    public Task Change_destination() => RunAsync("UAT-PIP-04", async page =>
    {
        var (source, destination) = await ConnectionsAsync(page, "pip04");
        var other = Unique("Destino nuevo");
        await new DestinationsPage(page).CreateAsync(other, "otro-host.invalid", "Otra", "sa", "x");
        var pipelines = new PipelinesPage(page);
        var name = Unique("Pipeline PIP04");
        var id = await pipelines.CreateAsync(name, source, destination);

        await pipelines.EditAsync(id);
        await pipelines.ChooseDestinationAsync(other);
        await pipelines.SaveAsync();

        await page.RevealAsync(page.Row(name));
        await Assertions.Expect(page.Row(name)).ToContainTextAsync(DestinationsPage.IdFor(other));
        await pipelines.EditAsync(id);
        await Assertions.Expect(pipelines.SelectedDestination).ToHaveTextAsync(other);
        await pipelines.CancelAsync();
    });

    [Fact(DisplayName = "UAT-PIP-05 Renombrar")]
    public Task Rename_pipeline() => RunAsync("UAT-PIP-05", async page =>
    {
        var (source, destination) = await ConnectionsAsync(page, "pip05");
        var pipelines = new PipelinesPage(page);
        var id = await pipelines.CreateAsync(Unique("Pipeline PIP05"), source, destination);
        var renamed = Unique("Pipeline renombrado");

        await pipelines.EditAsync(id);
        await pipelines.SetNameAsync(renamed);
        await pipelines.SaveAsync();

        await page.RevealAsync(page.Row(renamed));
    });

    [Fact(DisplayName = "UAT-PIP-06 Activar y desactivar el scheduler")]
    public Task Toggle_scheduler() => RunAsync("UAT-PIP-06", async page =>
    {
        var (source, destination) = await ConnectionsAsync(page, "pip06");
        var pipelines = new PipelinesPage(page);
        var id = await pipelines.CreateAsync(Unique("Pipeline PIP06"), source, destination);

        await pipelines.ToggleSchedulerAsync(id);
        await pipelines.SchedulerBadge(id, on: true).ExpectPresentAsync();

        // Se apaga al terminar: el scheduler lanzaría jobs de este pipeline durante el resto de la suite.
        await pipelines.ToggleSchedulerAsync(id);
        await pipelines.SchedulerBadge(id, on: false).ExpectPresentAsync();
    });

    [Fact(DisplayName = "UAT-PIP-07 Confirmar antes de ejecutar")]
    public Task Cancelling_the_confirmation_starts_nothing() => RunAsync("UAT-PIP-07", async page =>
    {
        var (source, destination) = await ConnectionsAsync(page, "pip07");
        var pipelines = new PipelinesPage(page);
        var id = await pipelines.CreateAsync(Unique("Pipeline PIP07"), source, destination);

        await pipelines.OpenRunAndCancelAsync(id);

        await new JobsPage(page).OpenAsync();
        await page.Locator($"[data-testid^='job-actions-job-{id}-']").ExpectGoneAsync();
    });

    [Fact(DisplayName = "UAT-PIP-08 Borrar un pipeline")]
    public Task Delete_pipeline() => RunAsync("UAT-PIP-08", async page =>
    {
        var (source, destination) = await ConnectionsAsync(page, "pip08");
        var pipelines = new PipelinesPage(page);
        var id = await pipelines.CreateAsync(Unique("Pipeline PIP08"), source, destination);

        await pipelines.DeleteAsync(id);

        await pipelines.ActionsOf(id).ExpectGoneAsync();
    });

    [UatSqlFact(DisplayName = "UAT-PIP-09 Restablecer un pipeline")]
    public Task Reset_pipeline() => RunAsync("UAT-PIP-09", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "pip09", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);
        var tables = new TablesPage(page);
        await tables.OpenAsync(journey.PipelineName);
        await tables.RowOf(journey.Entities[0]).ExpectPresentAsync();

        var pipelines = new PipelinesPage(page);
        await pipelines.OpenAsync();
        await pipelines.ResetAsync(journey.PipelineId);
        await Assertions.Expect(page.GetByTestId("pipe-reset-confirm")).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = Ui.Long });

        await tables.OpenAsync(journey.PipelineName);
        await tables.RowOf(journey.Entities[0]).ExpectGoneAsync();
    });
}
