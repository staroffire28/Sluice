using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sluice.Portal.Models;

namespace Sluice.Portal.Tests;

// /health y /health/ready ya no devuelven siempre {status:"ok"} a ciegas: /health valida que
// DATA_DIR sea escribible, y /health/ready además prueba conectividad ADLS/SQL por pipeline
// habilitado. Estos tests cubren el camino feliz end-to-end contra la app real (DI real).
public sealed class HealthEndpointsTests : IClassFixture<ApiWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointsTests(ApiWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_DataDirWritable_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_NoEnabledPipelines_ReturnsOkWithEmptyPipelineList()
    {
        var response = await _client.GetAsync("/health/ready");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("ok", json.GetProperty("dataDir").GetString());
        Assert.Empty(json.GetProperty("pipelines").EnumerateArray());
    }

    // /health/ready es anónimo y devolvía los mensajes de excepción de SQL/ADLS (llegó a mostrar un
    // fragmento de contraseña). Ahora solo booleanos; el detalle queda en el log del servidor.
    [Fact]
    public async Task Ready_does_not_expose_error_messages()
    {
        using var factory = new ApiWebApplicationFactory();
        var api = factory.CreateAuthenticatedClient();
        await api.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-src-ready", Name = "Src", Role = ConnectionRole.Source, Type = "adls",
        });
        await api.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-dst-ready", Name = "Dst", Role = ConnectionRole.Destination, Type = "sqlserver",
        });
        await api.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "pipe-ready", Name = "Ready", SourceId = "conn-src-ready", DestinationId = "conn-dst-ready", Enabled = true,
        });

        var response = await factory.CreateClient().GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("message", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not configured", body, StringComparison.OrdinalIgnoreCase);
        var pipeline = Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("pipelines").EnumerateArray());
        Assert.False(pipeline.GetProperty("sqlOk").GetBoolean());
    }
}
