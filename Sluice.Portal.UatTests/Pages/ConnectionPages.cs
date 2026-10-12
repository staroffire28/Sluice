using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.UatTests.Pages;

// Orígenes (/sources). El id de una conexión creada desde la UI es conn-src-{slug del nombre}.
public sealed class SourcesPage(IPage page)
{
    public const string LocalType = "Local (UAT)";

    public static string IdFor(string name) => $"conn-src-{EntityIds.Slugify(name)}";

    public Task OpenAsync() => page.GotoInteractiveAsync("/sources");

    public Task StartCreateAsync() => page.ClickTestIdAsync("add-source", Ui.Long);

    public Task ChooseTypeAsync(string label) => page.ChooseAsync("src-type", label);

    public Task SetNameAsync(string value) => page.FillFieldAsync("src-name", value);

    public Task SetFolderAsync(string value) => page.FillFieldAsync("src-container", value);

    public Task SetParquetFolderAsync(string value) => page.FillFieldAsync("src-parquet-container", value);

    public Task SaveAsync() => page.ClickTestIdAsync("src-save");

    public Task CancelAsync() => page.ClickTestIdAsync("src-cancel");

    public ILocator NameInput => page.Input("src-name");
    public ILocator FolderInput => page.Input("src-container");
    public ILocator ParquetFolderInput => page.Input("src-parquet-container");
    public ILocator AccountField => page.GetByTestId("src-account");
    public ILocator AccountInput => page.Input("src-account");
    public ILocator SecretInput => page.Input("src-secret");

    public Task SetAccountAsync(string value) => page.FillFieldAsync("src-account", value);

    public Task SetSecretAsync(string value) => page.FillFieldAsync("src-secret", value);
    public ILocator ActionsOf(string id) => page.GetByTestId($"src-actions-{id}");

    // Crea un origen local que lee {LOCAL_SOURCE_ROOT}/{folder} y publica el parquet en {parquetFolder}.
    public async Task<string> CreateLocalAsync(string name, string folder, string parquetFolder)
    {
        await OpenAsync();
        await StartCreateAsync();
        await ChooseTypeAsync(LocalType);
        await SetNameAsync(name);
        await SetFolderAsync(folder);
        await SetParquetFolderAsync(parquetFolder);
        await SaveAsync();
        var id = IdFor(name);
        await page.RevealAsync(ActionsOf(id));
        return id;
    }

    // Abre el diálogo de edición y espera a que muestre los datos cargados antes de tocar nada.
    public async Task EditAsync(string id)
    {
        await page.MenuAsync($"src-actions-{id}", $"src-edit-{id}");
        await page.WaitForLoadedAsync(NameInput, value => IdFor(value) == id);
    }

    public Task TestAsync(string id) => page.MenuAsync($"src-actions-{id}", $"src-test-{id}");

    public async Task DeleteAsync(string id, string confirm = "Delete")
    {
        await page.MenuAsync($"src-actions-{id}", $"src-delete-{id}");
        await page.DialogButton(confirm).ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });
    }
}

// Destinos (/destinations). El id es conn-dst-{slug del nombre}.
public sealed class DestinationsPage(IPage page)
{
    public static string IdFor(string name) => $"conn-dst-{EntityIds.Slugify(name)}";

    public Task OpenAsync() => page.GotoInteractiveAsync("/destinations");

    public Task StartCreateAsync() => page.ClickTestIdAsync("add-destination", Ui.Long);

    public ILocator NameInput => page.Input("dst-name");
    public ILocator HostInput => page.Input("dst-host");
    public ILocator DatabaseInput => page.Input("dst-database");
    public ILocator PasswordInput => page.Input("dst-password");
    public ILocator ActionsOf(string id) => page.GetByTestId($"dst-actions-{id}");

    public async Task FillAsync(string name, string host, string database, string user, string password)
    {
        await page.FillFieldAsync("dst-name", name);
        await page.FillFieldAsync("dst-host", host);
        await page.FillFieldAsync("dst-database", database);
        await page.FillFieldAsync("dst-user", user);
        await page.FillFieldAsync("dst-password", password);
    }

    public Task SaveAsync() => page.ClickTestIdAsync("dst-save");

    public Task CancelAsync() => page.ClickTestIdAsync("dst-cancel");

    public async Task<string> CreateAsync(string name, string host, string database, string user, string password)
    {
        await OpenAsync();
        await StartCreateAsync();
        await FillAsync(name, host, database, user, password);
        await SaveAsync();
        var id = IdFor(name);
        await page.RevealAsync(ActionsOf(id));
        return id;
    }

    public async Task EditAsync(string id)
    {
        await page.MenuAsync($"dst-actions-{id}", $"dst-edit-{id}");
        await page.WaitForLoadedAsync(NameInput, value => IdFor(value) == id);
    }

    public Task TestAsync(string id) => page.MenuAsync($"dst-actions-{id}", $"dst-test-{id}");

    // Versión de SQL Server del formulario y prueba de conexión desde el diálogo (sin guardar).
    public Task ChooseVersionAsync(string label) => page.ChooseAsync("dst-sql-version", label);
    public ILocator SelectedVersion => page.Selected("dst-sql-version");
    public ILocator DetectResult => page.GetByTestId("dst-detect-result");
    public Task DetectAsync() => page.ClickTestIdAsync("dst-detect");

    public async Task DeleteAsync(string id, string confirm = "Delete")
    {
        await page.MenuAsync($"dst-actions-{id}", $"dst-delete-{id}");
        await page.DialogButton(confirm).ClickAsync(new LocatorClickOptions { Timeout = Ui.Short });
    }
}
