using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sluice.Portal.Tests;

public class WorkerAuthStartupTests
{
    // Producción sin UI_API_KEY: fail-open sería inaceptable. El arranque debe fallar
    // cerrado (lanzar) en vez de solo advertir. WORKER_API_KEY ya no existe (se quitaron los
    // endpoints /internal/tasks/* que protegía), así que no se exige.
    private sealed class NoKeysProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DATA_DIR",
                Path.Combine(Path.GetTempPath(), "sluice-noauth-prod", Guid.NewGuid().ToString("N")));
        }
    }

    private sealed class NoWorkerKeyProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DATA_DIR",
                Path.Combine(Path.GetTempPath(), "sluice-no-worker-prod", Guid.NewGuid().ToString("N")));
            builder.UseSetting("UI_API_KEY", "ui-only-key");
        }
    }

    private sealed class NoUiKeyProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DATA_DIR",
                Path.Combine(Path.GetTempPath(), "sluice-no-ui-prod", Guid.NewGuid().ToString("N")));
        }
    }

    private sealed class PlaceholderKeysProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DATA_DIR",
                Path.Combine(Path.GetTempPath(), "sluice-placeholder-prod", Guid.NewGuid().ToString("N")));
            builder.UseSetting("UI_API_KEY", "changeme-ui");
        }
    }

    [Fact]
    public void Startup_fails_when_both_keys_missing_outside_development()
    {
        using var factory = new NoKeysProductionFactory();
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Startup_succeeds_with_only_ui_key_outside_development()
    {
        using var factory = new NoWorkerKeyProductionFactory();
        using var client = factory.CreateClient();
        Assert.True((await client.GetAsync("/health")).IsSuccessStatusCode);
    }

    [Fact]
    public void Startup_fails_when_ui_key_missing_outside_development()
    {
        using var factory = new NoUiKeyProductionFactory();
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Startup_fails_when_placeholder_keys_used_outside_development()
    {
        using var factory = new PlaceholderKeysProductionFactory();
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    // Acceso abierto: solo con AUTH_MODE=none explícito. Sin clave y sin esa variable el
    // arranque sigue fallando (test de arriba): nadie deja el portal abierto por descuido.
    private sealed class OpenModeProductionFactory(string? uiKey = null, string authMode = "none") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("DATA_DIR",
                Path.Combine(Path.GetTempPath(), "sluice-open-prod", Guid.NewGuid().ToString("N")));
            builder.UseSetting("AUTH_MODE", authMode);
            if (uiKey is not null)
            {
                builder.UseSetting("UI_API_KEY", uiKey);
            }
        }
    }

    [Fact]
    public async Task Open_mode_starts_without_a_key_outside_development()
    {
        using var factory = new OpenModeProductionFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.True((await client.GetAsync("/health")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/login")).IsSuccessStatusCode);
    }

    // Una clave junto a AUTH_MODE=none es contradictorio: no se adivina cuál de las dos quería.
    [Fact]
    public void Open_mode_with_a_key_fails_to_start()
    {
        using var factory = new OpenModeProductionFactory(uiKey: "una-clave-real");
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Unknown_auth_mode_fails_to_start()
    {
        using var factory = new OpenModeProductionFactory(uiKey: "una-clave-real", authMode: "abierto");
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}