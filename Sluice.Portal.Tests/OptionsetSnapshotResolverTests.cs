using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

public sealed class OptionsetSnapshotResolverTests
{
    [Fact]
    public void Resolve_PrefersManifestEntitySnapshot()
    {
        var manifest = new IngestManifest
        {
            JobId = "j1",
            PipelineId = "p1",
            Entities =
            [
                new IngestEntityManifest
                {
                    Entity = "account",
                    ParquetPath = "p/a.parquet",
                    Snapshot = "2026-06-20T08.00.00Z",
                    CsvRows = 1,
                },
            ],
        };

        var snapshot = OptionsetSnapshotResolver.Resolve(manifest, "2026-06-01T08.00.00Z", "2026-06-24T08.00.00Z");

        Assert.Equal("2026-06-20T08.00.00Z", snapshot);
    }

    [Fact]
    public void Resolve_FallsBackToManifestLastProcessed_WhenEntitySnapshotsEmpty()
    {
        var manifest = new IngestManifest
        {
            JobId = "j1",
            PipelineId = "p1",
            LastProcessedSnapshot = "2026-06-10T08.00.00Z",
            Entities =
            [
                new IngestEntityManifest
                {
                    Entity = "account",
                    ParquetPath = "p/a.parquet",
                    Snapshot = "",
                    CsvRows = 0,
                },
            ],
        };

        var snapshot = OptionsetSnapshotResolver.Resolve(manifest, null, "2026-06-24T08.00.00Z");

        Assert.Equal("2026-06-10T08.00.00Z", snapshot);
    }

    [Fact]
    public void Resolve_FallsBackToPipelineState_ThenSettled()
    {
        var manifest = new IngestManifest
        {
            JobId = "j1",
            PipelineId = "p1",
            Entities = [],
        };

        Assert.Equal(
            "2026-06-15T08.00.00Z",
            OptionsetSnapshotResolver.Resolve(manifest, "2026-06-15T08.00.00Z", "2026-06-24T08.00.00Z"));

        Assert.Equal(
            "2026-06-24T08.00.00Z",
            OptionsetSnapshotResolver.Resolve(manifest, null, "2026-06-24T08.00.00Z"));

        Assert.Null(OptionsetSnapshotResolver.Resolve(manifest, null, null));
    }
}