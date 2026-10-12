using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Filtro por fecha de la página Jobs (por StartedAt), combinado con pipeline y estado; List y Count
// filtran igual (la paginación y la exportación usan el mismo criterio).
public sealed class JobServiceDateFilterTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-jobsdate-" + Guid.NewGuid().ToString("N"));
    private readonly JobService _jobs;

    public JobServiceDateFilterTests()
    {
        _jobs = new JobService(new JobStore(Options.Create(new AppOptions { DataDir = _dataDir })));
        // Mediodía UTC: el día local es el mismo en cualquier zona entre UTC-11 y UTC+11.
        Create("job-a", "pipe-lean", JobStatus.Ok, "2026-10-07T12:00:00Z");
        Create("job-b", "pipe-lean", JobStatus.Error, "2026-10-08T12:00:00Z");
        Create("job-c", "pipe-otro", JobStatus.Ok, "2026-10-09T12:00:00Z");
        Create("job-d", "pipe-lean", JobStatus.Ok, "2026-10-09T12:30:00Z");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void List_and_Count_filter_by_date_range()
    {
        var range = new DateRange(new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 9));

        Assert.Equal(["job-d", "job-c", "job-b"], _jobs.List(1, 50, range: range).Select(j => j.Id));
        Assert.Equal(3, _jobs.Count(range: range));
    }

    [Fact]
    public void Date_range_combines_with_pipeline_and_status()
    {
        var from9 = new DateRange(new DateOnly(2026, 10, 9), null);

        Assert.Equal(["job-d"], _jobs.List(1, 50, "pipe-lean", JobStatus.Ok, from9).Select(j => j.Id));
        Assert.Equal(1, _jobs.Count("pipe-lean", JobStatus.Ok, from9));
        Assert.Equal(4, _jobs.Count());
    }

    private void Create(string id, string pipelineId, JobStatus status, string startedAt) =>
        _jobs.Create(new Job
        {
            Id = id,
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = status,
            StartedAt = DateTimeOffset.Parse(startedAt, System.Globalization.CultureInfo.InvariantCulture),
        });
}
