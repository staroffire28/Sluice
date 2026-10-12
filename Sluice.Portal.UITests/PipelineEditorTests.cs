using System.Text.Json;
using Microsoft.Playwright;
using Sluice.Portal.Models;

namespace Sluice.Portal.UITests;

// El diálogo de edición se reutiliza entre pipelines: los desplegables de origen y destino
// (fluent-dropdown) conservaban la selección del pipeline editado antes y mostraban sus
// conexiones; guardar así podía reasignar el pipeline al origen/destino de otro.
[Collection("blazor-ui")]
public sealed class PipelineEditorTests
{
    private const string AltSourceId = "conn-src-ui-alt";
    private const string AltSourceName = "Alt Source";
    private const string AltDestId = "conn-dst-ui-alt";
    private const string AltDestName = "Alt Dest";
    private const string AltPipelineId = "pipe-ui-alt";

    private readonly BlazorAppFixture _fx;

    public PipelineEditorTests(BlazorAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Editing_a_second_pipeline_shows_its_own_source_and_destination()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var api = context.APIRequest;
        await PostAsync(api, "/api/connections", new Connection
        {
            Id = AltSourceId, Name = AltSourceName, Role = ConnectionRole.Source, Type = "adls",
            Config = new Dictionary<string, string?> { ["account"] = "altbogus" },
        });
        await PostAsync(api, "/api/connections", new Connection
        {
            Id = AltDestId, Name = AltDestName, Role = ConnectionRole.Destination, Type = "sqlserver",
            Config = new Dictionary<string, string?> { ["host"] = "bogus" },
        });
        await PostAsync(api, "/api/pipelines", new Pipeline
        {
            Id = AltPipelineId, Name = "Alt Pipeline", SourceId = AltSourceId, DestinationId = AltDestId, Enabled = false,
        });

        try
        {
            var page = await context.NewPageAsync();
            await page.GotoInteractiveAsync("/pipelines");

            // Primero el pipeline del fixture (UI Source / UI Dest), y se cancela.
            await OpenEditorAsync(page, BlazorAppFixture.PipelineId);
            await Assertions.Expect(Selected(page, "pipe-source")).ToHaveTextAsync("UI Source");
            await page.Locator("fluent-button").Filter(new() { HasTextString = "Cancel" }).First.ClickAsync();

            // Después el otro: debe mostrar sus conexiones, no las del anterior.
            await OpenEditorAsync(page, AltPipelineId);
            await Assertions.Expect(Selected(page, "pipe-source")).ToHaveTextAsync(AltSourceName);
            await Assertions.Expect(Selected(page, "pipe-dest")).ToHaveTextAsync(AltDestName);

            // Guardar sin tocar nada conserva su origen y destino.
            await page.GetByTestId("pipe-save").ClickAsync();
            await Assertions.Expect(page.GetByTestId("pipe-save")).ToBeHiddenAsync();
            var saved = await api.GetAsync($"/api/pipelines/{AltPipelineId}");
            using var json = JsonDocument.Parse(await saved.TextAsync());
            Assert.Equal(AltSourceId, json.RootElement.GetProperty("sourceId").GetString());
            Assert.Equal(AltDestId, json.RootElement.GetProperty("destinationId").GetString());
            await page.AssertNoBlazorErrorsAsync();
        }
        finally
        {
            await api.DeleteAsync($"/api/pipelines/{AltPipelineId}?wipe=true");
            await api.DeleteAsync($"/api/connections/{AltSourceId}");
            await api.DeleteAsync($"/api/connections/{AltDestId}");
        }
    }

    private static async Task OpenEditorAsync(IPage page, string pipelineId)
    {
        await page.GetByTestId($"actions-{pipelineId}").ClickAsync(new LocatorClickOptions { Timeout = 30000 });
        await page.GetByTestId($"edit-{pipelineId}").ClickAsync(new LocatorClickOptions { Timeout = 15000 });
    }

    // Texto que muestra el desplegable (el botón combobox de fluent-dropdown).
    private static ILocator Selected(IPage page, string testId) =>
        page.GetByTestId(testId).Locator("button[role=combobox]");

    private static async Task PostAsync<T>(IAPIRequestContext api, string url, T body)
    {
        var response = await api.PostAsync(url, new APIRequestContextOptions
        {
            Data = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
        });
        Assert.True(response.Ok, $"{url}: HTTP {response.Status} {await response.TextAsync()}");
    }
}
