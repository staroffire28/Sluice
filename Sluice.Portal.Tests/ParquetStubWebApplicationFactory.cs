using Microsoft.Extensions.DependencyInjection;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

public sealed class ParquetStubWebApplicationFactory : ApiWebApplicationFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton<IParquetArtifactService, StubParquetArtifactService>();
    }
}