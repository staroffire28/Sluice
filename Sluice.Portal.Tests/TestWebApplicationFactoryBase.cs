using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Sluice.Portal.Tests;

public abstract class TestWebApplicationFactoryBase : WebApplicationFactory<Program>
{
    protected abstract IReadOnlyDictionary<string, string?> Settings { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(ConfigureTestServices);
    }

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }
}