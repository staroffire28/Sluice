using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Editar una conexión: un secreto que llega vacío o enmascarado se conserva (la UI no lo vuelve a
// mostrar), salvo que la edición cambie el tipo: la configuración anterior es de otro tipo y no se
// arrastra (p. ej. un origen ADLS pasado a local no guarda el client_secret de Azure).
public sealed class ConnectionServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-connsvc-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionService _service;

    public ConnectionServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        var store = new ConnectionStore(options, new ConnectionSecretProtector(new EphemeralDataProtectionProvider()));
        _service = new ConnectionService(store, new PipelineStore(options), null!, null!);
        _service.Create(new Connection
        {
            Id = "conn-src-a",
            Name = "A",
            Role = ConnectionRole.Source,
            Type = "adls",
            Config = new Dictionary<string, string?>
            {
                ["account"] = "cuenta",
                ["container"] = "datos",
                ["client_secret"] = "secreto",
            },
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void Editing_without_retyping_the_secret_keeps_it()
    {
        _service.Update("conn-src-a", _service.Get("conn-src-a")! with
        {
            Name = "A renombrado",
            Config = new Dictionary<string, string?>
            {
                ["account"] = "cuenta",
                ["container"] = "datos",
                ["client_secret"] = "",
            },
        });

        Assert.Equal(SecretMasker.MaskValue, _service.Get("conn-src-a")!.Config["client_secret"]);
    }

    [Fact]
    public void Changing_the_type_does_not_carry_over_the_previous_secret()
    {
        _service.Update("conn-src-a", _service.Get("conn-src-a")! with
        {
            Type = "local",
            Config = new Dictionary<string, string?>
            {
                ["container"] = "carpeta",
                ["client_secret"] = "",
            },
        });

        var config = _service.Get("conn-src-a")!.Config;
        Assert.True(string.IsNullOrEmpty(config.GetValueOrDefault("client_secret")));
        Assert.False(config.ContainsKey("account"));
        Assert.Equal("carpeta", config["container"]);
    }
}
