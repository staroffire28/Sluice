using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Tests;

public class SnapshotSelectorTests
{
    private static readonly string[] Snapshots =
    [
        "2026-06-08T09.00.00Z",
        "2026-06-08T10.00.00Z",
        "2026-06-08T11.00.00Z",
    ];

    [Fact]
    public void FirstRun_ProcessesOnlyFoldersBeforeCurrent()
    {
        // changelog = 11 (en escritura) => seguras: 09 y 10; 11 queda unclosed.
        var result = SnapshotSelector.Select(Snapshots, null, "2026-06-08T11.00.00Z");
        Assert.False(result.Blocked);
        Assert.Equal(["2026-06-08T09.00.00Z", "2026-06-08T10.00.00Z"], result.ToProcess);
        Assert.Equal(["2026-06-08T11.00.00Z"], result.Unclosed);
        Assert.Equal("2026-06-08T10.00.00Z", result.LastSettled);
    }

    [Fact]
    public void CurrentChangelogFolder_IsNeverProcessed()
    {
        // Núcleo del fix: la carpeta nombrada en changelog.info está EN ESCRITURA
        // (Synapse Link sigue volcando CSVs) y no debe consumirse.
        var result = SnapshotSelector.Select(Snapshots, null, "2026-06-08T10.00.00Z");
        Assert.False(result.Blocked);
        Assert.Equal(["2026-06-08T09.00.00Z"], result.ToProcess);
        Assert.DoesNotContain("2026-06-08T10.00.00Z", result.ToProcess);
        Assert.Contains("2026-06-08T10.00.00Z", result.Unclosed);
        Assert.Equal("2026-06-08T09.00.00Z", result.LastSettled);
    }

    [Fact]
    public void Incremental_OnlyNewSettledSnapshots()
    {
        // changelog = 11; ya procesado 09 => solo queda 10 por procesar (11 sigue abierta).
        var result = SnapshotSelector.Select(Snapshots, "2026-06-08T09.00.00Z", "2026-06-08T11.00.00Z");
        Assert.Single(result.ToProcess);
        Assert.Equal("2026-06-08T10.00.00Z", result.ToProcess[0]);
    }

    [Fact]
    public void CurrentIsOldestFolder_NothingSafeYet()
    {
        // El export inicial aún escribe la primera carpeta: no hay nada cerrado todavía.
        var result = SnapshotSelector.Select(Snapshots, null, "2026-06-08T09.00.00Z");
        Assert.False(result.Blocked);
        Assert.Empty(result.ToProcess);
        Assert.Null(result.LastSettled);
        Assert.Equal(3, result.Unclosed.Count);
    }

    [Fact]
    public void MissingChangelog_Blocks()
    {
        var result = SnapshotSelector.Select(Snapshots, null, null);
        Assert.True(result.Blocked);
        Assert.Empty(result.ToProcess);
    }

    [Fact]
    public void UnknownChangelogFolder_Blocks()
    {
        var result = SnapshotSelector.Select(Snapshots, null, "2026-06-08T99.00.00Z");
        Assert.True(result.Blocked);
    }
}