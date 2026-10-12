using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Sluice.Portal.Endpoints;
using Sluice.Portal.Models;

namespace Sluice.Portal.Tests;

// Paquete de la Logic App de alertas (Ajustes → Descargar paquete): el .zip trae la plantilla, el
// script, la guía y la vista previa, y la plantilla entiende los eventos que el portal envía.
public sealed class LogicAppPackageTests : IClassFixture<ApiWebApplicationFactory>
{
    private static readonly string[] Files =
        ["README.md", "azuredeploy.json", "azuredeploy.parameters.json", "deploy.ps1", "email-preview.html"];

    private readonly ApiWebApplicationFactory _factory;

    public LogicAppPackageTests(ApiWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Descarga_un_zip_con_el_paquete_en_su_carpeta()
    {
        var response = await _factory.CreateAuthenticatedClient().GetAsync("/api/settings/logic-app-package");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{ManagementEndpoints.LogicAppPackageName}.zip", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Equal(
            Files.Select(f => $"{ManagementEndpoints.LogicAppPackageName}/{f}").Order(StringComparer.Ordinal),
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.All(zip.Entries, e => Assert.True(e.Length > 0, $"{e.FullName} vacío"));
    }

    [Fact]
    public async Task Sin_sesion_no_se_descarga()
    {
        var response = await _factory.CreateClient().GetAsync("/api/settings/logic-app-package");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // La plantilla es JSON válido, crea la Logic App y la conexión de Office 365, devuelve la URL del
    // disparador y distingue los eventType que emite AlertService (si se renombra uno, falla aquí).
    [Fact]
    public void La_plantilla_crea_la_Logic_App_y_reconoce_los_eventos_del_portal()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "logic-app", "azuredeploy.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var types = root.GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("type").GetString()).ToList();
        Assert.Contains("Microsoft.Logic/workflows", types);
        Assert.Contains("Microsoft.Web/connections", types);
        Assert.Contains("listCallbackUrl", root.GetProperty("outputs").GetProperty("triggerUrl").GetProperty("value").GetString());
        Assert.True(root.GetProperty("parameters").TryGetProperty("recipients", out _));

        var template = root.GetRawText();
        foreach (var eventType in new[] { "pipeline.failure", ReconcileAlert.Divergence, ReconcileAlert.Values, "test.ping" })
        {
            Assert.Contains($"'{eventType}'", template);
        }

        Assert.Contains("/v2/Mail", template);
    }
}
