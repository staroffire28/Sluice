using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Tests;

// El origen simulado de las pruebas UAT: misma semántica que AdlsClient sobre carpetas, sin salir
// nunca de LOCAL_SOURCE_ROOT, y solo disponible cuando esa variable está definida.
public sealed class LocalFolderClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sluice-local-" + Guid.NewGuid().ToString("N"));

    public LocalFolderClientTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteSource(string relative, string content)
    {
        var path = Path.Combine(_root, "synapse", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private LocalFolderClient Client() => new(_root, "synapse", "parquet");

    [Fact]
    public async Task Lists_snapshots_and_entities_like_synapse_link_skipping_system_folders()
    {
        WriteSource("Changelog/changelog.info", "2026-01-02T00.00.00Z\n");
        WriteSource("2026-01-01T00.00.00Z/account/1.csv", "a");
        WriteSource("2026-01-02T00.00.00Z/contact/1.csv", "c");
        WriteSource("2026-01-02T00.00.00Z/OptionsetMetadata/GlobalOptionsetMetadata.csv", "x");
        WriteSource("2026-01-02T00.00.00Z/_temp/1.csv", "x");
        WriteSource("Microsoft.Athena.TrickleFeedService/contact-model.json", "{}");

        var client = Client();

        Assert.Equal("2026-01-02T00.00.00Z", await client.GetSettledSnapshotAsync());
        Assert.Equal(["2026-01-01T00.00.00Z", "2026-01-02T00.00.00Z"], await client.ListSnapshotFoldersAsync());
        Assert.Equal(["contact"], await client.ListEntitiesAsync("2026-01-02T00.00.00Z"));
    }

    [Fact]
    public async Task Streams_entity_csv_lines_in_ordinal_file_order()
    {
        WriteSource("2026-01-01T00.00.00Z/account/b.csv", "3\n4");
        WriteSource("2026-01-01T00.00.00Z/account/a.csv", "1\n2\n");

        var lines = new List<string>();
        await foreach (var line in Client().StreamEntityCsvLinesAsync("2026-01-01T00.00.00Z", "account"))
        {
            lines.Add(line);
        }

        Assert.Equal(["1", "2", "3", "4"], lines);
    }

    [Fact]
    public async Task A_snapshot_with_only_empty_csvs_is_not_complete()
    {
        WriteSource("2026-01-01T00.00.00Z/account/1.csv", "");

        var integrity = await Client().ValidateSnapshotAsync("2026-01-01T00.00.00Z");

        Assert.False(integrity.IsComplete);
    }

    [Fact]
    public async Task Parquet_round_trip_lists_paths_relative_to_the_parquet_folder()
    {
        var client = Client();
        await client.UploadParquetAsync("pipe-a/consolidated/account.parquet", new MemoryStream([1, 2, 3]));

        var artifacts = await client.ListParquetAsync("pipe-a");

        var artifact = Assert.Single(artifacts);
        Assert.Equal("pipe-a/consolidated/account.parquet", artifact.Path);
        Assert.Equal(3, artifact.SizeBytes);
        Assert.True(await client.ParquetExistsAsync("pipe-a/consolidated/account.parquet"));
        Assert.Empty(await client.ListParquetAsync("pipe-missing"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("../../etc")]
    public async Task Paths_cannot_escape_the_root(string path)
    {
        var client = Client();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ParquetExistsAsync(path + "/x.parquet"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListEntitiesAsync(path));
        Assert.Throws<InvalidOperationException>(() => new LocalFolderClient(_root, path, null));
    }

    [Fact]
    public async Task Deleting_a_snapshot_only_accepts_snapshot_folders()
    {
        WriteSource("2026-01-01T00.00.00Z/account/1.csv", "a");
        var client = Client();

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteSnapshotFolderAsync("Changelog"));
        await client.DeleteSnapshotFolderAsync("2026-01-01T00.00.00Z");

        Assert.Empty(await client.ListSnapshotFoldersAsync());
    }

    [Fact]
    public async Task Factory_builds_the_local_client_only_when_the_root_is_configured()
    {
        WriteSource("Changelog/changelog.info", "2026-01-01T00.00.00Z");
        var resolver = new SingleConnection(new Connection
        {
            Id = "conn-src-local",
            Name = "Local",
            Role = ConnectionRole.Source,
            Type = AdlsClientFactory.LocalType,
            Config = new Dictionary<string, string?> { ["container"] = "synapse", ["parquet_container"] = "parquet" },
        });

        var enabled = new AdlsClientFactory(resolver, Options.Create(new AppOptions { LocalSourceRoot = _root }));
        var disabled = new AdlsClientFactory(resolver, Options.Create(new AppOptions()));

        Assert.IsType<LocalFolderClient>(await enabled.CreateAsync("conn-src-local"));
        Assert.IsType<LocalFolderClient>(await enabled.CreateParquetStoreAsync("conn-src-local"));
        var (ok, message) = await enabled.TestAsync("conn-src-local");
        Assert.True(ok, message);
        Assert.Contains("2026-01-01T00.00.00Z", message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.CreateAsync("conn-src-local"));
        Assert.False((await disabled.TestAsync("conn-src-local")).Ok);
    }

    private sealed class SingleConnection(Connection connection) : IConnectionResolver
    {
        public Connection? GetUnmasked(string id) => id == connection.Id ? connection : null;
    }
}
