using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// Flujos CRUD por la UI (formularios FluentUI). Con el JS de FluentUI cargado, los
// fluent-text-input exponen su <input> en shadow DOM abierto, que Playwright atraviesa.
[Collection("blazor-ui")]
public sealed class CrudFlowTests
{
    private readonly BlazorAppFixture _fx;

    public CrudFlowTests(BlazorAppFixture fx) => _fx = fx;

    private async Task<IPage> NewPageAsync()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        return await context.NewPageAsync();
    }

    [Fact]
    public async Task Sources_CreateConnection_AppearsInGrid()
    {
        var name = "E2E New Source";
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/sources");

        var add = page.GetByTestId("add-source");
        await add.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        await add.ClickAsync();

        // El <input> vive en el shadow DOM abierto del fluent-text-input.
        var nameInput = page.GetByTestId("src-name").Locator("input");
        await nameInput.FillAsync(name, new LocatorFillOptions { Timeout = 10000 });

        await page.GetByTestId("src-save").ClickAsync();

        await Assertions.Expect(page.GetByText(name).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Filtro por estado (FluentSelect): al elegir "Error" solo queda el job en error.
    [Fact]
    public async Task Jobs_StatusFilter_NarrowsToError()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/jobs");

        // Ambos jobs visibles sin filtro.
        await Assertions.Expect(page.GetByText(BlazorAppFixture.OkJobId).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // Abrir el select y elegir "Error".
        await page.GetByTestId("jobs-status").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.FluentOption("Error").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // Queda el de error; el Ok desaparece.
        await Assertions.Expect(page.GetByText(BlazorAppFixture.ErrorJobId).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await Assertions.Expect(page.GetByText(BlazorAppFixture.OkJobId)).ToHaveCountAsync(0);
        await page.AssertNoBlazorErrorsAsync();
    }

    // Búsqueda en /tables (FluentTextInput): filtra por nombre de entidad.
    [Fact]
    public async Task Tables_Search_FiltersByEntity()
    {
        var keep = BlazorAppFixture.TableNames[0];   // account
        var gone = BlazorAppFixture.TableNames[1];   // contact
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/tables");
        await page.SelectTablesPipelineAsync();

        await Assertions.Expect(page.GetByText(gone).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        var searchInput = page.GetByTestId("tables-search").Locator("input");
        await searchInput.FillAsync(keep, new LocatorFillOptions { Timeout = 10000 });
        // FluentTextInput bindea en 'change' (blur), no por tecla; sin blur, @bind-Value:after no corre.
        await searchInput.BlurAsync();

        await Assertions.Expect(page.GetByText(keep).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await Assertions.Expect(page.GetByText(gone)).ToHaveCountAsync(0);
        await page.AssertNoBlazorErrorsAsync();
    }

    // Crear pipeline: nombre + dos FluentSelect (origen/destino con las conexiones sembradas).
    [Fact]
    public async Task Pipelines_CreatePipeline_AppearsInGrid()
    {
        var name = "E2E New Pipeline";
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/pipelines");

        var add = page.GetByTestId("add-pipeline").First;
        await add.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        await add.ClickAsync();

        await page.GetByTestId("pipe-name").Locator("input").FillAsync(name, new LocatorFillOptions { Timeout = 10000 });

        await page.GetByTestId("pipe-source").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.FluentOption("UI Source").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.GetByTestId("pipe-dest").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.FluentOption("UI Dest").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.GetByTestId("pipe-save").ClickAsync();

        await Assertions.Expect(page.GetByText(name).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Guardar ajustes en /settings muestra la barra de éxito.
    [Fact]
    public async Task Settings_Save_ShowsSuccess()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/settings");

        var save = page.GetByTestId("settings-save");
        await save.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        await save.ClickAsync();

        // El data-testid no siempre se propaga en FluentMessageBar; afirmamos por el texto
        // de éxito (cultura por defecto en).
        await Assertions.Expect(page.GetByText("Settings saved").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Toggle del scheduler de un pipeline (botón plano por fila). pipe-ui nace deshabilitado.
    [Fact]
    public async Task Pipelines_ToggleScheduler_FlipsState()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/pipelines");

        await Assertions.Expect(page.GetByTestId("sched-pipe-ui-off"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30000 });

        await page.GetByTestId("toggle-pipe-ui").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await Assertions.Expect(page.GetByTestId("sched-pipe-ui-on"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });

        // Restaura el estado original (estado compartido entre tests).
        await page.GetByTestId("toggle-pipe-ui").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await Assertions.Expect(page.GetByTestId("sched-pipe-ui-off"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Borrado por UI: menú de acciones de la fila → Delete → diálogo de confirmación → Delete.
    [Fact]
    public async Task Pipelines_Delete_RemovesFromGrid()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/pipelines");

        await Assertions.Expect(page.GetByText(BlazorAppFixture.DeletablePipelineName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        await page.GetByTestId($"actions-{BlazorAppFixture.DeletablePipelineId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId($"delete-{BlazorAppFixture.DeletablePipelineId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId("pipe-delete-confirm").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // La fila desaparece: su menú de acciones ya no existe (el nombre aún vive en el
        // diálogo de confirmación oculto, así que no sirve contar por texto).
        await Assertions.Expect(page.GetByTestId($"actions-{BlazorAppFixture.DeletablePipelineId}"))
            .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Editar: menú → Edit → diálogo prellenado → renombrar → Save → el nuevo nombre aparece.
    [Fact]
    public async Task Pipelines_Edit_RenamesPipeline()
    {
        var newName = "Edited Pipeline Name";
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/pipelines");

        await Assertions.Expect(page.GetByText(BlazorAppFixture.EditablePipelineName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        await page.GetByTestId($"actions-{BlazorAppFixture.EditablePipelineId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId($"edit-{BlazorAppFixture.EditablePipelineId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // El diálogo abre prellenado; FillAsync reemplaza el valor.
        await page.GetByTestId("pipe-name").Locator("input").FillAsync(newName, new LocatorFillOptions { Timeout = 10000 });
        await page.GetByTestId("pipe-save").ClickAsync();

        await Assertions.Expect(page.GetByText(newName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Ejecutar pipeline: menú → Run → diálogo de confirmación → Run → toast "Job ... started".
    [Fact]
    public async Task Pipelines_Run_StartsJob()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/pipelines");

        await page.GetByTestId("actions-pipe-ui").ClickAsync(new LocatorClickOptions { Timeout = 30000 });
        await page.GetByTestId("run-pipe-ui").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // Diálogo de confirmación de DialogService → botón primario "Run".
        await page.DialogButton("Run")
            .ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // Toast de éxito "Job {id} started ({type})." (cultura por defecto en).
        await Assertions.Expect(page.GetByText("started").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Crear Destination (form distinto al de Source: host/database/user/password).
    [Fact]
    public async Task Destinations_CreateConnection_AppearsInGrid()
    {
        var name = "E2E New Destination";
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/destinations");

        var add = page.GetByTestId("add-destination");
        await add.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        await add.ClickAsync();

        await page.GetByTestId("dst-name").Locator("input").FillAsync(name, new LocatorFillOptions { Timeout = 10000 });
        await page.GetByTestId("dst-save").ClickAsync();

        await Assertions.Expect(page.GetByText(name).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Crear destino con versión SQL 2017: el selector persiste en la grilla.
    [Fact]
    public async Task Destinations_CreateWithSqlVersion2017_PersistsInGrid()
    {
        var name = "E2E Dest SQL 2017";
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/destinations");

        await page.GetByTestId("add-destination").ClickAsync(new LocatorClickOptions { Timeout = 30000 });
        await page.GetByTestId("dst-name").Locator("input").FillAsync(name, new LocatorFillOptions { Timeout = 10000 });

        await page.GetByTestId("dst-sql-version").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.FluentOption("SQL Server 2017")
            .ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.GetByTestId("dst-save").ClickAsync();

        var row = page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = name });
        await Assertions.Expect(row.GetByText("SQL Server 2017").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Editar destino: cambiar versión SQL a 2017 y verificar en la grilla.
    [Fact]
    public async Task Destinations_Edit_ChangesSqlVersionTo2017()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/destinations");

        await Assertions.Expect(page.GetByText(BlazorAppFixture.EditableDestName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        await page.GetByTestId($"dst-actions-{BlazorAppFixture.EditableDestId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId($"dst-edit-{BlazorAppFixture.EditableDestId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.GetByTestId("dst-sql-version").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.FluentOption("SQL Server 2017")
            .ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.GetByTestId("dst-save").ClickAsync();

        var row = page.GetByRole(AriaRole.Row).Filter(new LocatorFilterOptions { HasText = BlazorAppFixture.EditableDestName });
        await Assertions.Expect(row.GetByText("SQL Server 2017").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Editar una conexión de origen (menú → Edit → diálogo prellenado → renombrar → Save).
    [Fact]
    public async Task Sources_Edit_RenamesConnection()
    {
        var newName = "Edited Source Name";
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/sources");

        await Assertions.Expect(page.GetByText(BlazorAppFixture.EditableSourceName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        await page.GetByTestId($"src-actions-{BlazorAppFixture.EditableSourceId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId($"src-edit-{BlazorAppFixture.EditableSourceId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.GetByTestId("src-name").Locator("input").FillAsync(newName, new LocatorFillOptions { Timeout = 10000 });
        await page.GetByTestId("src-save").ClickAsync();

        await Assertions.Expect(page.GetByText(newName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Borrar una conexión: menú → Delete → diálogo de confirmación de SERVICIO
    // (DialogService.ShowConfirmationAsync, distinto del diálogo embebido de Pipelines).
    [Fact]
    public async Task Sources_Delete_RemovesConnection()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/sources");

        await Assertions.Expect(page.GetByText(BlazorAppFixture.DeletableSourceName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        await page.GetByTestId($"src-actions-{BlazorAppFixture.DeletableSourceId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId($"src-delete-{BlazorAppFixture.DeletableSourceId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // Botón primario "Delete" del diálogo de confirmación de servicio.
        await page.DialogButton("Delete")
            .ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        // La fila desaparece (su menú de acciones ya no existe).
        await Assertions.Expect(page.GetByTestId($"src-actions-{BlazorAppFixture.DeletableSourceId}"))
            .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Editar un destino (menú → Edit → diálogo prellenado → renombrar → Save).
    [Fact]
    public async Task Destinations_Edit_RenamesConnection()
    {
        var newName = "Edited Dest Name";
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/destinations");

        await Assertions.Expect(page.GetByText(BlazorAppFixture.EditableDestName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        await page.GetByTestId($"dst-actions-{BlazorAppFixture.EditableDestId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId($"dst-edit-{BlazorAppFixture.EditableDestId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.GetByTestId("dst-name").Locator("input").FillAsync(newName, new LocatorFillOptions { Timeout = 10000 });
        await page.GetByTestId("dst-save").ClickAsync();

        await Assertions.Expect(page.GetByText(newName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // Borrar un destino (menú → Delete → diálogo de confirmación de servicio → Delete).
    [Fact]
    public async Task Destinations_Delete_RemovesConnection()
    {
        var page = await NewPageAsync();
        await page.GotoInteractiveAsync("/destinations");

        await Assertions.Expect(page.GetByText(BlazorAppFixture.DeletableDestName).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        await page.GetByTestId($"dst-actions-{BlazorAppFixture.DeletableDestId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
        await page.GetByTestId($"dst-delete-{BlazorAppFixture.DeletableDestId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await page.DialogButton("Delete")
            .ClickAsync(new LocatorClickOptions { Timeout = 15000 });

        await Assertions.Expect(page.GetByTestId($"dst-actions-{BlazorAppFixture.DeletableDestId}"))
            .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 10000 });
        await page.AssertNoBlazorErrorsAsync();
    }
}
