using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Escritura atómica de JsonFileStore (T4): serializa a .tmp y renombra. Verifica
// que no quedan .tmp huérfanos tras un Save y que un .tmp residual (de un crash
// a mitad de escritura) no rompe las lecturas — el patrón "*.json" no lo capta.
public class JsonFileStoreTests
{
    private static (ConnectionStore store, string connectionsDir, string dataDir) NewStore()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "syn-jsonstore-test-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new AppOptions { DataDir = dataDir });
        var protector = new ConnectionSecretProtector(new EphemeralDataProtectionProvider());
        var store = new ConnectionStore(options, protector);
        return (store, Path.Combine(dataDir, "connections"), dataDir);
    }

    private static Connection Conn(string id) => new()
    {
        Id = id,
        Name = id,
        Role = ConnectionRole.Source,
        Type = "adls",
        Config = new Dictionary<string, string?> { ["account"] = "acct" },
    };

    // Un JSON corrupto (o a medio escribir por otro proceso) hacía lanzar ListAll entero:
    // la lista de jobs, el scheduler o la de conexiones dejaban de funcionar por un archivo.
    [Fact]
    public void ListAll_skips_corrupt_file_and_returns_the_rest()
    {
        var (store, connectionsDir, dataDir) = NewStore();
        try
        {
            store.Save(Conn("c1"));
            store.Save(Conn("c2"));
            File.WriteAllText(Path.Combine(connectionsDir, "broken.json"), "{ esto no es json");

            var ids = store.ListAll().Select(c => c.Id).OrderBy(id => id).ToList();

            Assert.Equal(["c1", "c2"], ids);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Write_leaves_no_orphan_tmp_file()
    {
        var (store, connectionsDir, dataDir) = NewStore();
        try
        {
            store.Save(Conn("c1"));

            Assert.Empty(Directory.GetFiles(connectionsDir, "*.tmp"));
            Assert.True(File.Exists(Path.Combine(connectionsDir, "c1.json")));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Stale_tmp_file_does_not_break_reads()
    {
        var (store, connectionsDir, dataDir) = NewStore();
        try
        {
            store.Save(Conn("c1"));

            // Simular un .tmp residual de un crash a mitad de escritura.
            File.WriteAllText(Path.Combine(connectionsDir, "c2.json.tmp"), "{ truncated json");

            var all = store.ListAll();
            Assert.Single(all);                       // el .tmp no se deserializa como conexión
            Assert.Equal("c1", all[0].Id);
            Assert.NotNull(store.GetById("c1"));      // lectura por id intacta
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
