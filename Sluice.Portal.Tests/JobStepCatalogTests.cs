using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

public sealed class JobStepCatalogTests
{
    [Fact]
    public void All_ListsTheEightStepsInScreenOrder()
    {
        Assert.Equal(
            ["discover", "snapshots", "consolidate", "register", "prepare", "merge", "compare", "report"],
            JobStepCatalog.All.Select(s => s.Key));
        Assert.Equal(JobStepCatalog.All.Count, JobStepCatalog.All.Select(s => s.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("ingest", 4)]
    [InlineData("sql", 2)]
    [InlineData("reconcile", 2)]
    public void ForStage_ReturnsItsSteps(string stage, int count)
    {
        Assert.Equal(count, JobStepCatalog.ForStage(stage).Count());
        Assert.All(JobStepCatalog.ForStage(stage), s => Assert.Equal(stage, s.Stage));
    }
}
