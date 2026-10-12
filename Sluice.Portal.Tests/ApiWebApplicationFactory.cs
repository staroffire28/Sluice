using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sluice.Portal.Tests;

public class ApiWebApplicationFactory : TestWebApplicationFactoryBase
{
    public const string UiKey = "ui-api-test-key";

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["UI_API_KEY"] = UiKey,
        ["EXECUTION_MODE"] = "local",
        ["DATA_DIR"] = Path.Combine(Path.GetTempPath(), "sluice-api-tests", Guid.NewGuid().ToString("N")),
    };

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = true });
        var login = client.PostAsJsonAsync("/auth/login", new { Key = UiKey }).GetAwaiter().GetResult();
        login.EnsureSuccessStatusCode();
        return client;
    }
}