using Microsoft.Playwright;

namespace Sluice.Portal.UITests;

// Smoke de navegación: cada ruta del portal carga, renderiza su PageHeader y NO muestra
// el error-UI de Blazor (#blazor-error-ui). Cubre todas las páginas navegables, incluidas
// las que tocan ADLS/SQL con credenciales bogus (deben degradar con gracia, no crashear).
[Collection("blazor-ui")]
public sealed class NavigationSmokeTests
{
    private readonly BlazorAppFixture _fx;

    public NavigationSmokeTests(BlazorAppFixture fx) => _fx = fx;

    public static IEnumerable<object[]> Routes() => new[]
    {
        new object[] { "/" },
        new object[] { "/jobs" },
        new object[] { "/tables" },
        new object[] { "/sources" },
        new object[] { "/destinations" },
        new object[] { "/pipelines" },
        new object[] { "/snapshots" },
        new object[] { "/reconcile" },
        new object[] { "/parquet" },
        new object[] { "/analytics" },
        new object[] { "/logs" },
        new object[] { "/settings" },
        new object[] { "/ayuda" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_RendersWithoutBlazorError(string route)
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(route);

        // La cabecera de página aparece cuando el contenido (no solo el layout) renderizó.
        await Assertions.Expect(page.GetByTestId("page-header").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });

        // Margen para que cualquier carga async (ADLS/SQL bogus) dispare su error-UI si va a fallar.
        await page.WaitForTimeoutAsync(500);
        await page.AssertNoBlazorErrorsAsync();
    }

    // El detalle de job (ruta parametrizada) también renderiza sin error.
    [Fact]
    public async Task JobDetailRoute_Renders()
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"/jobs/{BlazorAppFixture.ErrorJobId}");
        await Assertions.Expect(page.GetByText(BlazorAppFixture.ErrorJobId).First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await page.AssertNoBlazorErrorsAsync();
    }

    // El análisis de crecimiento por tabla (ruta con 2 parámetros) renderiza sin error, tanto
    // para una entidad conocida como para una inexistente (debe degradar con gracia, no
    // crashear — GetTableGrowth devuelve un reporte vacío, no lanza).
    [Theory]
    [InlineData("account")]
    [InlineData("entidad-inexistente")]
    public async Task TableGrowthRoute_Renders(string entity)
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"/tables/{BlazorAppFixture.PipelineId}/{entity}/analytics");
        await Assertions.Expect(page.GetByTestId("page-header").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
        await page.AssertNoBlazorErrorsAsync();
    }
}
