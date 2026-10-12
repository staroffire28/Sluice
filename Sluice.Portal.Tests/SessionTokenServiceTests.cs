using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Tests;

public class SessionTokenServiceTests : IDisposable
{
    private readonly string _dataDir;

    public SessionTokenServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void CreateToken_and_TryValidate_roundtrip_succeeds()
    {
        var time = new FixedTimeProvider();
        var service = CreateService(time, sessionHours: 12);

        var token = service.CreateToken();

        Assert.True(service.TryValidate(token));
    }

    [Fact]
    public void TryValidate_rejects_expired_token()
    {
        var time = new FixedTimeProvider();
        var service = CreateService(time, sessionHours: 1);
        var token = service.CreateToken();

        time.Advance(TimeSpan.FromHours(2));

        Assert.False(service.TryValidate(token));
    }

    [Fact]
    public void TryValidate_rejects_tampered_signature()
    {
        var service = CreateService(new FixedTimeProvider(), sessionHours: 12);
        var token = service.CreateToken();
        var tampered = token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

        Assert.False(service.TryValidate(tampered));
    }

    // La sesión se firmaba con la propia UI_API_KEY: con una cookie capturada se podía atacar la
    // clave por fuerza bruta offline. Ahora la clave de firma es aleatoria y vive en DATA_DIR,
    // compartida por cualquier instancia que use el mismo DATA_DIR.
    [Fact]
    public void Signing_key_is_independent_of_the_ui_key_and_shared_through_data_dir()
    {
        var time = new FixedTimeProvider();
        var issuer = CreateService(time, sessionHours: 12, uiKey: "ui-key-a");
        var validator = CreateService(time, sessionHours: 12, uiKey: "ui-key-b");

        Assert.True(validator.TryValidate(issuer.CreateToken()));
    }

    // Cerrar sesión solo borraba la cookie: una cookie robada seguía valiendo hasta expirar.
    [Fact]
    public void RevokeAll_invalidates_previously_issued_tokens_but_new_ones_work()
    {
        var time = new FixedTimeProvider();
        var service = CreateService(time, sessionHours: 12);
        var before = service.CreateToken();

        service.RevokeAll();

        Assert.False(service.TryValidate(before));
        Assert.False(CreateService(time, sessionHours: 12).TryValidate(before)); // también en otra instancia
        Assert.True(service.TryValidate(service.CreateToken()));
    }

    [Fact]
    public void Legacy_token_signed_with_the_ui_key_is_rejected()
    {
        var time = new FixedTimeProvider();
        var service = CreateService(time, sessionHours: 12, uiKey: "ui-signing-key");
        var expiry = time.GetUtcNow().AddHours(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var legacySignature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("ui-signing-key"), Encoding.UTF8.GetBytes(expiry))).ToLowerInvariant();

        Assert.False(service.TryValidate($"{expiry}.{legacySignature}"));
    }

    private SessionTokenService CreateService(FixedTimeProvider time, int sessionHours, string uiKey = "ui-signing-key") =>
        new(Options.Create(new AppOptions
        {
            DataDir = _dataDir,
            UiApiKey = uiKey,
            SessionExpiryHours = sessionHours,
        }), time);
}
