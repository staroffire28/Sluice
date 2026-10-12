namespace Sluice.Portal.UatTests.Pages;

// Ajustes (/settings).
public sealed class SettingsPage(IPage page)
{
    public Task OpenAsync() => page.GotoInteractiveAsync("/settings");

    public ILocator AutoFix => page.GetByTestId("settings-autofix");

    // Estado del interruptor (propiedad checked del fluent-switch).
    public Task<bool> IsAutoFixOnAsync() => AutoFix.EvaluateAsync<bool>("e => !!e.checked");

    // Activa o desactiva la corrección automática y guarda. Los ajustes los comparte toda la suite:
    // quien la active debe dejarla como estaba.
    public async Task SetAutoFixAsync(bool on)
    {
        await OpenAsync();
        if (await IsAutoFixOnAsync() != on)
        {
            await AutoFix.ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });
        }

        await page.ClickTestIdAsync("settings-save", Ui.Long);
        await page.ExpectMessageAsync("Settings saved.");
    }
}
