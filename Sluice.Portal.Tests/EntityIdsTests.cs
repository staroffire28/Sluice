using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Los ids de conexiones, pipelines y jobs son nombres de archivo bajo DATA_DIR: un id con
// separadores o ".." escribía fuera del directorio (path traversal) y Create sobrescribía
// en silencio un id existente.
public class EntityIdsTests : IDisposable
{
    private readonly string _root;
    private readonly string _dataDir;

    public EntityIdsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sluice-portal-tests", Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("conn-src-prod-db")]
    [InlineData("pipe-default")]
    [InlineData("job-pipe-default-20260612154324")]
    [InlineData("conn-src-producción")] // ids ya guardados con acentos siguen siendo legibles
    [InlineData("conn_x.v2")]
    public void IsValid_accepts_single_segment_file_names(string id) => Assert.True(EntityIds.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:\\x")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("x.")]
    [InlineData(" x")]
    public void IsValid_rejects_paths_and_invalid_file_names(string? id) => Assert.False(EntityIds.IsValid(id));

    [Fact]
    public void IsValid_rejects_overlong_ids() => Assert.False(EntityIds.IsValid(new string('a', 129)));

    [Theory]
    [InlineData("Prod DB", "prod-db")]
    [InlineData("../../settings", "settings")]
    [InlineData("Prod: Sales (EU)", "prod-sales-eu")]
    [InlineData("Ñandú 2", "nandu-2")]
    [InlineData("  ", "")]
    public void Slugify_produces_lowercase_ascii_slug(string name, string expected) =>
        Assert.Equal(expected, EntityIds.Slugify(name));

    [Fact]
    public void Store_reads_with_invalid_id_return_nothing_instead_of_touching_other_paths()
    {
        var store = CreateStore();

        Assert.Null(store.GetById("../x"));
        Assert.False(store.Exists("..\\x"));
        Assert.False(store.Delete("../../data/x"));
    }

    [Fact]
    public void Store_save_with_traversal_id_throws_and_writes_nothing_outside()
    {
        var store = CreateStore();

        Assert.Throws<ArgumentException>(() => store.Save(CreatePipeline("../../evil")));

        Assert.Empty(Directory.GetFiles(_root, "*.json", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.GetFiles(_dataDir, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Store_save_new_rejects_existing_id_and_keeps_the_original()
    {
        var store = CreateStore();
        store.SaveNew(CreatePipeline("pipe-a", name: "original"));

        Assert.Throws<DuplicateIdException>(() => store.SaveNew(CreatePipeline("pipe-a", name: "impostor")));

        Assert.Equal("original", store.GetById("pipe-a")!.Name);
    }

    private PipelineStore CreateStore() => new(Options.Create(new AppOptions { DataDir = _dataDir }));

    private static Pipeline CreatePipeline(string id, string name = "p") =>
        new() { Id = id, Name = name, SourceId = "s", DestinationId = "d" };
}
