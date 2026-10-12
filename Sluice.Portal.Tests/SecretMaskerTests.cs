using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

public class SecretMaskerTests
{
    [Fact]
    public void MaskDictionary_redacts_secret_keys()
    {
        var source = new Dictionary<string, string?>
        {
            ["account"] = "myaccount",
            ["password"] = "super-secret",
            ["client_secret"] = "abc123",
            ["connection_string"] = "Server=.;Password=x",
        };

        var masked = SecretMasker.MaskDictionary(source);

        Assert.Equal("myaccount", masked["account"]);
        Assert.Equal(SecretMasker.MaskValue, masked["password"]);
        Assert.Equal(SecretMasker.MaskValue, masked["client_secret"]);
        Assert.Equal(SecretMasker.MaskValue, masked["connection_string"]);
    }

    [Fact]
    public void MaskDictionary_leaves_empty_secrets_unchanged()
    {
        var source = new Dictionary<string, string?> { ["password"] = "" };
        var masked = SecretMasker.MaskDictionary(source);
        Assert.Equal("", masked["password"]);
    }

    [Fact]
    public void Mask_connection_returns_copy_with_masked_config()
    {
        var connection = new Connection
        {
            Id = "src-1",
            Name = "ADLS",
            Role = ConnectionRole.Source,
            Type = "adls",
            Config = new Dictionary<string, string?>
            {
                ["client_secret"] = "hidden",
                ["container"] = "exports",
            },
        };

        var masked = SecretMasker.Mask(connection);

        Assert.Equal("exports", masked.Config["container"]);
        Assert.Equal(SecretMasker.MaskValue, masked.Config["client_secret"]);
        Assert.NotSame(connection.Config, masked.Config);
    }

    [Theory]
    [InlineData("PASSWORD")]
    [InlineData("Client_Secret")]
    [InlineData("connection_string")]
    public void IsSecretKey_is_case_insensitive(string key) =>
        Assert.True(SecretMasker.IsSecretKey(key));

    // Regresión: antes de este fix, un campo sensible nuevo (no en la whitelist fija de 3
    // nombres) quedaba expuesto en claro en /api/connections hasta que alguien recordara
    // actualizar SecretMasker.SecretKeys a mano. Ahora se detecta por patrón de nombre.
    [Theory]
    [InlineData("sas_token")]
    [InlineData("account_key")]
    [InlineData("api_key")]
    [InlineData("refresh_token")]
    public void IsSecretKey_detects_new_connection_field_types_by_pattern(string key) =>
        Assert.True(SecretMasker.IsSecretKey(key));

    [Theory]
    [InlineData("account")]
    [InlineData("container")]
    [InlineData("tenant_id")]
    [InlineData("client_id")]
    [InlineData("host")]
    [InlineData("database")]
    [InlineData("user")]
    [InlineData("sql_version")]
    public void IsSecretKey_does_not_flag_non_sensitive_fields(string key) =>
        Assert.False(SecretMasker.IsSecretKey(key));
}