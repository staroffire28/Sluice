using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dataDir;

    public SettingsStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-portal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private SettingsStore CreateStore() =>
        new(Options.Create(new AppOptions { DataDir = _dataDir }));

    [Fact]
    public void Get_SinArchivo_DevuelveDefaults()
    {
        var settings = CreateStore().Get();

        Assert.Equal(30, settings.SourceSnapshotRetentionDays);
        Assert.True(settings.SourceSnapshotRetentionDryRun);
        Assert.True(settings.SourceSnapshotRetentionRequireParquet);
        Assert.False(settings.AlertEnabled);
        Assert.Null(settings.AlertWebhookUrl);
    }

    // La retención no debe purgar con los valores por defecto si settings.json existe pero no
    // se puede leer: TryGet distingue "no hay archivo" (defaults, válido) de "ilegible".
    [Fact]
    public void TryGet_SinArchivo_DevuelveTrueConDefaults()
    {
        Assert.True(CreateStore().TryGet(out var settings));
        Assert.Equal(new AppSettings().RetentionDays, settings.RetentionDays);
    }

    [Fact]
    public void TryGet_ArchivoCorrupto_DevuelveFalse()
    {
        File.WriteAllText(Path.Combine(_dataDir, "settings.json"), "{ corrupto");

        Assert.False(CreateStore().TryGet(out _));
    }

    [Fact]
    public void TryGet_ArchivoValido_DevuelveSusValores()
    {
        File.WriteAllText(Path.Combine(_dataDir, "settings.json"), """{"retentionDays":365}""");

        Assert.True(CreateStore().TryGet(out var settings));
        Assert.Equal(365, settings.RetentionDays);
    }

    [Fact]
    public void Get_JsonLegacySinRetencion_AplicaDefaultsDeRetencion()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "settings.json"),
            """{"schedulerPollMinutes":5,"taskLeaseSeconds":120,"reconDivergencePct":1,"retentionDays":90}""");

        var settings = CreateStore().Get();

        Assert.Equal(30, settings.SourceSnapshotRetentionDays);
        Assert.True(settings.SourceSnapshotRetentionDryRun);
        Assert.True(settings.SourceSnapshotRetentionRequireParquet);
    }

    [Fact]
    public void Get_JsonConDryRunExplicitoFalse_NoSobreescribe()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "settings.json"),
            """
            {
              "schedulerPollMinutes": 5,
              "sourceSnapshotRetentionDays": 30,
              "sourceSnapshotRetentionDryRun": false,
              "sourceSnapshotRetentionRequireParquet": true
            }
            """);

        var settings = CreateStore().Get();

        Assert.False(settings.SourceSnapshotRetentionDryRun);
    }

    [Fact]
    public void Get_JsonLegacySinAlertas_AplicaAlertEnabledFalse()
    {
        File.WriteAllText(
            Path.Combine(_dataDir, "settings.json"),
            """{"schedulerPollMinutes":5,"taskLeaseSeconds":120}""");

        var settings = CreateStore().Get();

        Assert.False(settings.AlertEnabled);
    }

    // Settings sin validar: con TaskLeaseSeconds = 0 los leases nacían vencidos y otra réplica
    // reclamaba tareas en curso; también se aceptaba un webhook http:// o que no era una URL.
    [Theory]
    [InlineData(0, 120, 1.0, null)]
    [InlineData(5, 10, 1.0, null)]
    [InlineData(5, 120, -1.0, null)]
    [InlineData(5, 120, 101.0, null)]
    [InlineData(5, 120, 1.0, "http://logic.example/hook")]
    [InlineData(5, 120, 1.0, "no es una url")]
    public void Save_RechazaValoresFueraDeRango(int poll, int lease, double divergence, string? webhook)
    {
        var store = CreateStore();

        Assert.Throws<InvalidOperationException>(() => store.Save(new AppSettings
        {
            SchedulerPollMinutes = poll,
            TaskLeaseSeconds = lease,
            ReconDivergencePct = divergence,
            AlertWebhookUrl = webhook,
        }));
        Assert.False(File.Exists(Path.Combine(_dataDir, "settings.json"))); // no se guardó nada
    }

    [Fact]
    public void Validate_AceptaLosValoresPorDefectoYUnWebhookHttps()
    {
        Assert.Null(SettingsStore.Validate(new AppSettings()));
        Assert.Null(SettingsStore.Validate(new AppSettings { AlertWebhookUrl = "https://logic.example/hook?sig=x" }));
    }
}