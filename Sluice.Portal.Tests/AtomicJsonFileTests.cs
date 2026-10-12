using System.Text.Json;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Escritura atómica que comparten los stores. Antes usaba siempre el mismo "<archivo>.tmp" (dos
// escrituras simultáneas del mismo archivo chocaban al crearlo) y el rename fallaba sin reintento
// si un lector tenía el destino abierto (Windows).
public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _dir;

    public AtomicJsonFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sluice-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_writes_to_the_same_file_do_not_collide()
    {
        var path = Path.Combine(_dir, "state.json");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => AtomicJsonFile.Write(path, new Sample(i)))));

        Assert.NotNull(JsonSerializer.Deserialize<Sample>(File.ReadAllText(path), JsonDefaults.Options));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Write_waits_for_a_reader_that_holds_the_file_briefly()
    {
        var path = Path.Combine(_dir, "state.json");
        AtomicJsonFile.Write(path, new Sample(1));

        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var write = Task.Run(() => AtomicJsonFile.Write(path, new Sample(2)));
        await Task.Delay(80);
        reader.Dispose();
        await write;

        Assert.Equal(2, JsonSerializer.Deserialize<Sample>(File.ReadAllText(path), JsonDefaults.Options)!.Value);
    }

    private sealed record Sample(int Value);
}
