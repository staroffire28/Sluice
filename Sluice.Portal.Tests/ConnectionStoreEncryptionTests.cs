using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Round-trip de cifrado at-rest: Save cifra en disco, Resolver descifra al leer.
// Un solo EphemeralDataProtectionProvider compartido por store y resolver (igual
// que en runtime, donde ambos resuelven el mismo singleton).
public class ConnectionStoreEncryptionTests
{
    private static (ConnectionStore store, ConnectionResolver resolver, string dataDir) NewStack()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "syn-enc-test-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new AppOptions { DataDir = dataDir });
        var protector = new ConnectionSecretProtector(new EphemeralDataProtectionProvider());
        var store = new ConnectionStore(options, protector);
        var resolver = new ConnectionResolver(store, protector);
        return (store, resolver, dataDir);
    }

    private static Connection SampleConnection() => new()
    {
        Id = "c1",
        Name = "ADLS",
        Role = ConnectionRole.Source,
        Type = "adls",
        Config = new Dictionary<string, string?>
        {
            ["account"] = "acct",          // no secreto → claro
            ["client_secret"] = "topsecret", // secreto → cifrado
        },
    };

    [Fact]
    public void Save_encrypts_secret_on_disk_but_leaves_plain_keys()
    {
        var (store, _, dataDir) = NewStack();
        try
        {
            store.Save(SampleConnection());

            var json = File.ReadAllText(Path.Combine(dataDir, "connections", "c1.json"));
            Assert.DoesNotContain("topsecret", json); // el secreto NO está en claro
            Assert.Contains("enc:v1:", json);          // está cifrado
            Assert.Contains("acct", json);             // el no-secreto sí en claro
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Resolver_decrypts_secret_and_leaves_plain_keys()
    {
        var (store, resolver, dataDir) = NewStack();
        try
        {
            store.Save(SampleConnection());

            var resolved = resolver.GetUnmasked("c1");
            Assert.NotNull(resolved);
            Assert.Equal("topsecret", resolved!.Config["client_secret"]);
            Assert.Equal("acct", resolved.Config["account"]);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Save_does_not_double_encrypt_on_resave()
    {
        var (store, resolver, dataDir) = NewStack();
        try
        {
            store.Save(SampleConnection());

            // Edición sin cambiar el secreto: GetById devuelve cifrado, re-guardar
            // no debe re-cifrar (idempotencia) — el resolver sigue obteniendo el claro.
            var stored = store.GetById("c1")!;
            store.Save(stored);

            var resolved = resolver.GetUnmasked("c1");
            Assert.Equal("topsecret", resolved!.Config["client_secret"]);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
