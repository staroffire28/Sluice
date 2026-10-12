using Microsoft.AspNetCore.Mvc.Testing;

namespace Sluice.Portal.Tests;

public sealed class AuthWebApplicationFactory : TestWebApplicationFactoryBase
{
    public const string UiKey = "ui-test-key";

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["UI_API_KEY"] = UiKey,
        ["DATA_DIR"] = Path.Combine(Path.GetTempPath(), "sluice-auth-tests", Guid.NewGuid().ToString("N")),
    };
}