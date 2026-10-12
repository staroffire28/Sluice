using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// TablePauseStore (F2): conjunto de tablas pausadas por pipeline, persistido en JSON.
// Ausencia = activa (default habilitada). Verifica round-trip en disco, default vacío,
// case-insensitivity y que reanudar elimina la entrada.
public class TablePauseStoreTests
{
    private static (TablePauseStore store, string dataDir) NewStore()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "syn-pause-test-" + Guid.NewGuid().ToString("N"));
        var options = Options.Create(new AppOptions { DataDir = dataDir });
        return (new TablePauseStore(options), dataDir);
    }

    [Fact]
    public void Default_is_empty_and_active()
    {
        var (store, dataDir) = NewStore();
        try
        {
            Assert.Empty(store.GetPaused("pipe-1"));
            Assert.False(store.IsPaused("pipe-1", "Account"));
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void SetPaused_true_marks_table_paused_and_persists()
    {
        var (store, dataDir) = NewStore();
        try
        {
            store.SetPaused("pipe-1", "Account", paused: true);

            Assert.True(store.IsPaused("pipe-1", "Account"));
            Assert.Contains("Account", store.GetPaused("pipe-1"));

            // Round-trip: una instancia nueva (otro worker en distribuido) lee el mismo estado.
            var reopened = new TablePauseStore(Options.Create(new AppOptions { DataDir = dataDir }));
            Assert.True(reopened.IsPaused("pipe-1", "Account"));
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void IsPaused_is_case_insensitive()
    {
        var (store, dataDir) = NewStore();
        try
        {
            store.SetPaused("pipe-1", "Account", paused: true);
            Assert.True(store.IsPaused("pipe-1", "account"));
            Assert.True(store.IsPaused("pipe-1", "ACCOUNT"));
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void SetPaused_false_resumes_table()
    {
        var (store, dataDir) = NewStore();
        try
        {
            store.SetPaused("pipe-1", "Account", paused: true);
            store.SetPaused("pipe-1", "Account", paused: false);

            Assert.False(store.IsPaused("pipe-1", "Account"));
            Assert.Empty(store.GetPaused("pipe-1"));
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Pause_is_scoped_per_pipeline()
    {
        var (store, dataDir) = NewStore();
        try
        {
            store.SetPaused("pipe-1", "Account", paused: true);
            Assert.False(store.IsPaused("pipe-2", "Account"));
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }
}
