using System.Text.RegularExpressions;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.UatTests.Pages;

// Acciones de ejecución del menú de un pipeline (PipelineRunActions).
public enum RunKind
{
    Run,
    Force,
    Reconcile,
    Diff,
}

// Pipelines (/pipelines). El id de un pipeline creado desde la UI es pipe-{slug del nombre}.
public sealed partial class PipelinesPage(IPage page)
{
    public static string IdFor(string name) => $"pipe-{EntityIds.Slugify(name)}";

    public Task OpenAsync() => page.GotoInteractiveAsync("/pipelines");

    public ILocator NameInput => page.Input("pipe-name");
    public ILocator SelectedSource => page.Selected("pipe-source");
    public ILocator SelectedDestination => page.Selected("pipe-dest");
    public ILocator ActionsOf(string id) => page.GetByTestId($"actions-{id}");

    public async Task<string> CreateAsync(string name, string sourceName, string destinationName)
    {
        await OpenAsync();
        await page.ClickTestIdAsync("add-pipeline", Ui.Long);
        await page.FillFieldAsync("pipe-name", name);
        await page.ChooseAsync("pipe-source", sourceName);
        await page.ChooseAsync("pipe-dest", destinationName);
        await SaveAsync();
        var id = IdFor(name);
        await page.RevealAsync(ActionsOf(id));
        return id;
    }

    // Abre el diálogo de edición y espera a que muestre los datos cargados antes de tocar nada.
    public async Task EditAsync(string id)
    {
        await page.MenuAsync($"actions-{id}", $"edit-{id}");
        await page.WaitForLoadedAsync(NameInput, value => IdFor(value) == id);
    }

    public Task ChooseSourceAsync(string name) => page.ChooseAsync("pipe-source", name);

    public Task ChooseDestinationAsync(string name) => page.ChooseAsync("pipe-dest", name);

    public Task SetNameAsync(string name) => page.FillFieldAsync("pipe-name", name);

    public async Task SaveAsync()
    {
        await page.ClickTestIdAsync("pipe-save");
        await Assertions.Expect(page.GetByTestId("pipe-save")).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = Ui.Short });
    }

    public async Task CancelAsync()
    {
        await page.ClickTestIdAsync("pipe-cancel");
        await Assertions.Expect(page.GetByTestId("pipe-save")).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = Ui.Short });
    }

    public ILocator SchedulerBadge(string id, bool on) => page.GetByTestId($"sched-{id}-{(on ? "on" : "off")}");

    public async Task ToggleSchedulerAsync(string id)
    {
        await page.RevealAsync(page.GetByTestId($"toggle-{id}"));
        await page.ClickTestIdAsync($"toggle-{id}", Ui.Long);
    }

    // Lanza una ejecución desde el menú (con su diálogo de confirmación) y devuelve el id del job
    // que anuncia el aviso "Job {id} started (…)".
    public async Task<string> RunAsync(string id, RunKind kind = RunKind.Run, string confirm = "Run")
    {
        await page.MenuAsync($"actions-{id}", $"{kind.ToString().ToLowerInvariant()}-{id}");
        await page.DialogButton(confirm).ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });
        var toast = page.GetByText(StartedJob()).First;
        await toast.WaitForAsync(new LocatorWaitForOptions { Timeout = Ui.Long });
        return StartedJob().Match(await toast.InnerTextAsync()).Groups[1].Value;
    }

    // Abre el menú y el diálogo de confirmación, y lo cancela.
    public async Task OpenRunAndCancelAsync(string id, string cancel = "Cancel")
    {
        await page.MenuAsync($"actions-{id}", $"run-{id}");
        await page.DialogButton(cancel).ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });
    }

    public async Task DeleteAsync(string id)
    {
        await page.MenuAsync($"actions-{id}", $"delete-{id}");
        await page.ClickTestIdAsync("pipe-delete-confirm");
    }

    public async Task ResetAsync(string id)
    {
        await page.MenuAsync($"actions-{id}", $"reset-{id}");
        await page.ClickTestIdAsync("pipe-reset-confirm");
    }

    [GeneratedRegex(@"Job (job-[a-z0-9-]+)")]
    private static partial Regex StartedJob();
}
