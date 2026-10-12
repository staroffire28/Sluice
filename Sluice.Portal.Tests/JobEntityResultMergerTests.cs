using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

public sealed class JobEntityResultMergerTests
{
    [Fact]
    public void Merge_DuplicateExistingEntities_LastWinsWithoutThrowing()
    {
        var existing = new List<JobEntityResult>
        {
            new() { Entity = "accountingdistributiontemplate", CsvRows = 0, Status = "ok", Message = "Snapshot 1" },
            new() { Entity = "accountingdistributiontemplate", CsvRows = 0, Status = "ok", Message = "Snapshot 2" },
        };

        var updates = new List<JobEntityResult>
        {
            new()
            {
                Entity = "accountingdistributiontemplate",
                CsvRows = 120,
                SqlRows = 80,
                Status = "ok",
                Message = "Merged 40 row(s).",
            },
        };

        var merged = JobEntityResultMerger.Merge(existing, updates);

        Assert.Single(merged);
        Assert.Equal(80, merged[0].SqlRows);
        Assert.Equal("Merged 40 row(s).", merged[0].Message);
    }

    [Fact]
    public void Merge_PreservesUnrelatedEntities()
    {
        var existing = new List<JobEntityResult>
        {
            new() { Entity = "bankgroup", CsvRows = 10, Status = "ok" },
            new() { Entity = "bankgroup", CsvRows = 5, Status = "ok" },
        };

        var updates = new List<JobEntityResult>
        {
            new() { Entity = "securityrole", CsvRows = 3, SqlRows = 3, Status = "ok" },
        };

        var merged = JobEntityResultMerger.Merge(existing, updates);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, e => e.Entity == "bankgroup" && e.CsvRows == 5);
        Assert.Contains(merged, e => e.Entity == "securityrole" && e.SqlRows == 3);
    }
}