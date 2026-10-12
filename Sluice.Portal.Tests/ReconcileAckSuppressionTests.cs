using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Cubre la supresión de alerta por aceptación de divergencias (opción B): una divergencia
// aceptada deja de marcar IsPartial mientras se mantenga cerca del valor aceptado, pero el
// MaxDivergencePct sigue reflejando el número real, y si la divergencia se dispara vuelve a alertar.
public sealed class ReconcileAckSuppressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "recon-ack-" + Guid.NewGuid().ToString("N"));
    private readonly ReportStore _reports;
    private readonly ReconcileService _service;

    public ReconcileAckSuppressionTests()
    {
        // SaveReconcile escribe en reports/reconcile sin crear el directorio: lo preparamos.
        Directory.CreateDirectory(Path.Combine(_dir, "reports", "reconcile"));
        var opts = Options.Create(new AppOptions { DataDir = _dir });
        var settings = new SettingsStore(opts);
        settings.Save(new AppSettings { ReconDivergencePct = 1.0 });
        _reports = new ReportStore(opts);
        _service = new ReconcileService(settings, _reports);
    }

    private static ReconcileEntityResult Entity(string name, long src, long dst) => new()
    {
        Entity = name,
        SourceRows = src,
        DestinationRows = dst,
        DivergencePct = ReconcileService.ComputeDivergencePct(src, dst),
    };

    [Fact]
    public void Divergence_AboveThreshold_IsPartial()
    {
        var report = _service.BuildReport("p1", "job1", [Entity("account", 100, 90)]); // 10% > 1%
        Assert.True(report.IsPartial);
    }

    [Fact]
    public void AcceptedDivergence_IsSuppressed_ButNumberStillReal()
    {
        var div = ReconcileService.ComputeDivergencePct(100, 90); // 10%
        _reports.SaveAck("p1", "account", new ReconAck { AckPct = div });

        var report = _service.BuildReport("p1", "job2", [Entity("account", 100, 90)]);

        Assert.False(report.IsPartial);            // aceptada → no alerta
        Assert.Equal(div, report.MaxDivergencePct, 3); // pero el % real sigue visible
    }

    [Fact]
    public void AcceptedThenDrifts_ReAlerts()
    {
        _reports.SaveAck("p1", "account", new ReconAck { AckPct = ReconcileService.ComputeDivergencePct(100, 90) }); // ~10%

        // La divergencia se dispara a 50%, fuera de la banda del umbral → la aceptación caduca.
        var report = _service.BuildReport("p1", "job3", [Entity("account", 100, 50)]);

        Assert.True(report.IsPartial);
    }

    [Fact]
    public void RemoveAck_RestoresAlert()
    {
        var div = ReconcileService.ComputeDivergencePct(100, 90);
        _reports.SaveAck("p1", "account", new ReconAck { AckPct = div });
        _reports.RemoveAck("p1", "account");

        var report = _service.BuildReport("p1", "job4", [Entity("account", 100, 90)]);

        Assert.True(report.IsPartial);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }
}
