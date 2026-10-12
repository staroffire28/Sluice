using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

public class SnapshotRetentionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);
    private const int K = SnapshotRetentionService.KeepLastProcessedSnapshots;

    private static readonly string[] Snapshots =
    [
        "2026-05-01T08.00.00Z",
        "2026-05-15T08.00.00Z",
        "2026-06-01T08.00.00Z",
        "2026-06-10T08.00.00Z",
        "2026-06-20T08.00.00Z",
        "2026-06-23T08.00.00Z",
    ];

    private static AppSettings Settings(int days = 30) => new() { SourceSnapshotRetentionDays = days };

    private static SnapshotSelector.SelectionResult Selection(
        string? lastProcessed,
        string currentFolder = "2026-06-23T08.00.00Z") =>
        SnapshotSelector.Select(Snapshots, lastProcessed, currentFolder);

    [Fact]
    public void RetencionDeshabilitada_NoBorraNada()
    {
        var selection = Selection("2026-06-20T08.00.00Z");
        var plan = SnapshotRetentionService.Plan(
            Now,
            Settings(0),
            Snapshots,
            "2026-06-20T08.00.00Z",
            selection);

        Assert.Empty(plan.SnapshotFolders);
    }

    [Fact]
    public void SinLastProcessed_NoBorraNada()
    {
        var selection = Selection(null);
        var plan = SnapshotRetentionService.Plan(Now, Settings(), Snapshots, null, selection);

        Assert.Empty(plan.SnapshotFolders);
    }

    [Fact]
    public void ChangelogBloqueado_NoBorraNada()
    {
        var blocked = SnapshotSelector.Select(Snapshots, "2026-06-20T08.00.00Z", null);
        var plan = SnapshotRetentionService.Plan(
            Now,
            Settings(),
            Snapshots,
            "2026-06-20T08.00.00Z",
            blocked);

        Assert.Empty(plan.SnapshotFolders);
    }

    [Fact]
    public void PurgaProcesadosAntiguos_RespetaUmbral()
    {
        // 16 procesados + 1 en export: los 2 más viejos exceden 30d y quedan fuera del piso K.
        var snapshots = Enumerable.Range(1, K + 2)
            .Select(i => $"2026-05-{i:D2}T08.00.00Z")
            .ToList();
        snapshots.Add("2026-06-24T08.00.00Z");
        var lastProcessed = snapshots[^2];
        var selection = SnapshotSelector.Select(snapshots, lastProcessed, "2026-06-24T08.00.00Z");

        var plan = SnapshotRetentionService.Plan(
            Now,
            Settings(30),
            snapshots,
            lastProcessed,
            selection);

        Assert.Equal(["2026-05-01T08.00.00Z", "2026-05-02T08.00.00Z"], plan.SnapshotFolders);
    }

    [Fact]
    public void PisoK_ConservaUltimasK_AunqueSeanViejas()
    {
        // Muchos snapshots procesados: el piso K impide purgar los K más recientes.
        var many = Enumerable.Range(1, K + 5)
            .Select(i => $"2026-01-{i:D2}T08.00.00Z")
            .ToList();
        many.Add("2026-02-01T08.00.00Z");
        var lastProcessed = many[^2];
        var selection = SnapshotSelector.Select(many, lastProcessed, "2026-02-01T08.00.00Z");

        var plan = SnapshotRetentionService.Plan(
            new DateTimeOffset(2026, 2, 15, 0, 0, 0, TimeSpan.Zero),
            Settings(7),
            many,
            lastProcessed,
            selection);

        // Solo los 5 más viejos (fuera del piso K=14) y con >7 días de antigüedad.
        Assert.Equal(5, plan.SnapshotFolders.Count);
        Assert.DoesNotContain(lastProcessed, plan.SnapshotFolders);
    }

    [Fact]
    public void NoPurgaPendientesNiUnclosed()
    {
        var snapshots =
            new[] { "2026-06-08T09.00.00Z", "2026-06-08T10.00.00Z", "2026-06-08T11.00.00Z" };
        var selection = SnapshotSelector.Select(snapshots, "2026-06-08T09.00.00Z", "2026-06-08T11.00.00Z");
        var plan = SnapshotRetentionService.Plan(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            Settings(1),
            snapshots,
            "2026-06-08T09.00.00Z",
            selection);

        // 09 procesado pero en piso K; 10 pendiente — nunca se borra.
        Assert.Empty(plan.SnapshotFolders);
    }

    [Fact]
    public void SnapshotFolderTime_ParseaFormatoSynapse()
    {
        Assert.True(SnapshotFolderTime.TryParse("2026-06-08T10.00.00Z", out var ts));
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 10, 0, 0, TimeSpan.Zero), ts);
    }

    [Fact]
    public void Analyze_MarcaDisposiciones_ProtegidoRetenidoElegible()
    {
        var snapshots = Enumerable.Range(1, K + 2)
            .Select(i => $"2026-05-{i:D2}T08.00.00Z")
            .Append("2026-06-08T11.00.00Z")
            .ToArray();
        var lastProcessed = snapshots[^2];
        var selection = SnapshotSelector.Select(snapshots, lastProcessed, "2026-06-08T11.00.00Z");
        var analysis = SnapshotRetentionService.Analyze(
            new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero),
            Settings(7),
            snapshots,
            lastProcessed,
            selection);

        Assert.Equal(SnapshotRetentionService.DispositionProtected, analysis.DispositionBySnapshot["2026-06-08T11.00.00Z"]);
        Assert.Equal(SnapshotRetentionService.DispositionRetained, analysis.DispositionBySnapshot[lastProcessed]);
        Assert.Equal(SnapshotRetentionService.DispositionEligible, analysis.DispositionBySnapshot["2026-05-01T08.00.00Z"]);
    }

    [Fact]
    public void CarpetaSinTimestamp_FueraDelPiso_EsElegible()
    {
        var snapshots = new List<string> { "2026-01-01" };
        snapshots.AddRange(Enumerable.Range(1, K + 2).Select(i => $"2026-05-{i:D2}T08.00.00Z"));
        snapshots.Add("2026-06-24T08.00.00Z");
        var lastProcessed = snapshots[^2];
        var selection = SnapshotSelector.Select(snapshots, lastProcessed, "2026-06-24T08.00.00Z");

        var plan = SnapshotRetentionService.Plan(
            Now,
            Settings(30),
            snapshots,
            lastProcessed,
            selection);

        Assert.Contains("2026-01-01", plan.SnapshotFolders);
    }

    [Fact]
    public void Plan_UsaAnalyze_EligiblesCoinciden()
    {
        var selection = Selection("2026-06-20T08.00.00Z");
        var plan = SnapshotRetentionService.Plan(Now, Settings(30), Snapshots, "2026-06-20T08.00.00Z", selection);
        var analysis = SnapshotRetentionService.Analyze(Now, Settings(30), Snapshots, "2026-06-20T08.00.00Z", selection);
        Assert.Equal(analysis.EligibleForPurge, plan.SnapshotFolders);
    }
}