using System.Text.Json;
using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

// Job.Summary es Dictionary<string, object?>: en memoria (recién escrito por un executor) trae
// int/long/double, pero tras pasar por disco cada valor llega como JsonElement. JobSummary lee
// ambos casos igual para que la UI no dependa de dónde vino el job.
public sealed class JobSummaryTests
{
    private static Job MakeJob(Dictionary<string, object?> summary) => new()
    {
        Id = "job-1",
        PipelineId = "pipe-1",
        Summary = summary,
    };

    [Fact]
    public void InMemoryValues_AreRead()
    {
        var job = MakeJob(new()
        {
            ["csvRows"] = 12,
            ["sqlMergedRows"] = 5_000_000_000L,
            ["maxDivergencePct"] = 1.5,
            ["stoppedAtIncompleteSnapshot"] = "missing csv",
        });

        Assert.Equal(12, JobSummary.GetInt(job, "csvRows"));
        Assert.Equal(12L, JobSummary.GetLong(job, "csvRows"));
        Assert.Equal(5_000_000_000L, JobSummary.GetLong(job, "sqlMergedRows"));
        Assert.Equal(1.5, JobSummary.GetDouble(job, "maxDivergencePct"));
        Assert.Equal(12d, JobSummary.GetDouble(job, "csvRows"));
        Assert.Equal("missing csv", JobSummary.GetString(job, "stoppedAtIncompleteSnapshot"));
    }

    [Fact]
    public void JsonRoundTripValues_AreRead()
    {
        var original = MakeJob(new()
        {
            ["csvRows"] = 12,
            ["sqlMergedRows"] = 5_000_000_000L,
            ["maxDivergencePct"] = 1.5,
            ["stoppedAtIncompleteSnapshot"] = "missing csv",
        });
        var job = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(original, JsonDefaults.Options), JsonDefaults.Options)!;

        Assert.IsType<JsonElement>(job.Summary["csvRows"]);
        Assert.Equal(12, JobSummary.GetInt(job, "csvRows"));
        Assert.Equal(5_000_000_000L, JobSummary.GetLong(job, "sqlMergedRows"));
        Assert.Equal(1.5, JobSummary.GetDouble(job, "maxDivergencePct"));
        Assert.Equal("missing csv", JobSummary.GetString(job, "stoppedAtIncompleteSnapshot"));
    }

    [Fact]
    public void MissingNullOrWrongType_ReturnsNull()
    {
        var job = MakeJob(new()
        {
            ["nothing"] = null,
            ["text"] = "abc",
        });
        var roundTripped = JsonSerializer.Deserialize<Job>(
            JsonSerializer.Serialize(job with { Summary = new() { ["text"] = "abc", ["number"] = 3 } }, JsonDefaults.Options),
            JsonDefaults.Options)!;

        Assert.Null(JobSummary.GetInt(job, "absent"));
        Assert.Null(JobSummary.GetInt(job, "nothing"));
        Assert.Null(JobSummary.GetInt(job, "text"));
        Assert.Null(JobSummary.GetString(job, "nothing"));
        Assert.Null(JobSummary.GetInt(roundTripped, "text"));
        Assert.Null(JobSummary.GetString(roundTripped, "number"));
    }
}
