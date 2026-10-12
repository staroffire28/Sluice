using Microsoft.AspNetCore.DataProtection;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Tests;

public class ConnectionSecretProtectorTests
{
    private static IConnectionSecretProtector NewProtector() =>
        new ConnectionSecretProtector(new EphemeralDataProtectionProvider());

    [Fact]
    public void Protect_then_Unprotect_round_trips()
    {
        var p = NewProtector();
        var enc = p.Protect("s3cr3t");

        Assert.NotEqual("s3cr3t", enc);
        Assert.StartsWith("enc:v1:", enc);
        Assert.Equal("s3cr3t", p.Unprotect(enc));
    }

    [Fact]
    public void Protect_is_idempotent_on_already_encrypted()
    {
        var p = NewProtector();
        var once = p.Protect("abc");
        var twice = p.Protect(once);

        Assert.Equal(once, twice);            // no re-cifra
        Assert.Equal("abc", p.Unprotect(twice));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_is_noop_on_null_or_empty(string? value)
    {
        Assert.Equal(value, NewProtector().Protect(value));
    }

    [Fact]
    public void Unprotect_passthrough_on_legacy_plaintext()
    {
        Assert.Equal("plain-legacy", NewProtector().Unprotect("plain-legacy"));
    }

    [Fact]
    public void Unprotect_throws_clear_error_on_corrupt_payload()
    {
        var p = NewProtector();
        Assert.Throws<InvalidOperationException>(() => p.Unprotect("enc:v1:not-a-valid-payload"));
    }

    [Fact]
    public void IsProtected_detects_prefix()
    {
        var p = NewProtector();
        Assert.True(p.IsProtected("enc:v1:xyz"));
        Assert.False(p.IsProtected("plain"));
        Assert.False(p.IsProtected(null));
    }
}
