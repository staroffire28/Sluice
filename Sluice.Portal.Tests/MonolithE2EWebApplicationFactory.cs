using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sluice.Portal.Tests.Fakes;
using Sluice.Portal.Workers;

namespace Sluice.Portal.Tests;

public sealed class MonolithE2EWebApplicationFactory : TestWebApplicationFactoryBase
{
    public const string UiKey = "ui-monolith-e2e-key";

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["UI_API_KEY"] = UiKey,
        ["EXECUTION_MODE"] = "local",
        ["DATA_DIR"] = Path.Combine(Path.GetTempPath(), "synapse-monolith-e2e", Guid.NewGuid().ToString("N")),
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIngestExecutor>();
            services.RemoveAll<ISqlExecutor>();
            services.RemoveAll<IReconcileExecutor>();
            services.AddSingleton<IIngestExecutor, MonolithFakeIngestExecutor>();
            services.AddSingleton<ISqlExecutor, MonolithFakeSqlExecutor>();
            services.AddSingleton<IReconcileExecutor, MonolithFakeReconcileExecutor>();
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var login = client.PostAsJsonAsync("/auth/login", new { Key = UiKey }).GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
        return client;
    }
}