using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

public class ParquetConsolidatorTests
{
    private static readonly CdmEntitySchema Schema = new()
    {
        EntityName = "account",
        Attributes =
        [
            new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
            new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
            new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
        ],
    };

    private static Dictionary<string, object?> Row(string id, string name, string wm) => new()
    {
        ["Id"] = Guid.Parse(id),
        ["Name"] = name,
        ["SinkModifiedOn"] = DateTimeOffset.Parse(wm),
    };

    private static async Task<byte[]> ParquetBytes(params Dictionary<string, object?>[] rows)
    {
        using var ms = new MemoryStream();
        await ParquetMaterializer.WriteToStreamAsync(ms, Schema, rows);
        return ms.ToArray();
    }

    // Factory que devuelve un stream fresco en cada llamada (el consolidador lee 2 veces).
    private static Func<CancellationToken, Task<Stream>> Source(byte[] bytes) =>
        _ => Task.FromResult<Stream>(new MemoryStream(bytes));

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ConsolidateAsync(
        CdmEntitySchema schema, params byte[][] sourcesNewestFirst)
    {
        using var output = new MemoryStream();
        await ParquetConsolidator.ConsolidateAsync(sourcesNewestFirst.Select(Source).ToList(), schema, output);
        output.Position = 0;
        return await ParquetMaterializer.ReadFromStreamAsync(output, schema);
    }

    // Empate de watermark entre fuentes: gana la fuente más nueva (la primera), una sola fila.
    [Fact]
    public async Task Consolidate_on_a_watermark_tie_keeps_the_newest_source_row()
    {
        const string id = "33333333-3333-3333-3333-333333333333";
        var older = await ParquetBytes(Row(id, "old", "2026-06-08T10:00:00Z"));
        var newer = await ParquetBytes(Row(id, "new", "2026-06-08T10:00:00Z"));

        var result = await ConsolidateAsync(Schema, newer, older);

        Assert.Equal("new", Assert.Single(result)["Name"]);
    }

    // Una fila sin watermark pierde frente a cualquiera con watermark, aunque venga más nueva.
    [Fact]
    public async Task Consolidate_prefers_a_row_with_watermark_over_one_without()
    {
        const string id = "44444444-4444-4444-4444-444444444444";
        var withoutWm = new Dictionary<string, object?> { ["Id"] = Guid.Parse(id), ["Name"] = "sin", ["SinkModifiedOn"] = null };
        var newer = await ParquetBytes(withoutWm);
        var older = await ParquetBytes(Row(id, "con", "2026-06-08T10:00:00Z"));

        var result = await ConsolidateAsync(Schema, newer, older);

        Assert.Equal("con", Assert.Single(result)["Name"]);
    }

    // Ids que no son GUID (entidades con clave de texto): se deduplican sin distinguir mayúsculas.
    [Fact]
    public async Task Consolidate_dedups_text_ids_ignoring_case()
    {
        var textSchema = new CdmEntitySchema
        {
            EntityName = "legacy",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "NVARCHAR(100)", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };
        static Dictionary<string, object?> TextRow(string id, string name, string wm) =>
            new() { ["Id"] = id, ["Name"] = name, ["SinkModifiedOn"] = DateTimeOffset.Parse(wm) };

        using var ms1 = new MemoryStream();
        await ParquetMaterializer.WriteToStreamAsync(ms1, textSchema, [TextRow("ABC-1", "v2", "2026-06-09T10:00:00Z")]);
        using var ms2 = new MemoryStream();
        await ParquetMaterializer.WriteToStreamAsync(ms2, textSchema,
            [TextRow("abc-1", "v1", "2026-06-08T10:00:00Z"), TextRow("xyz-9", "z", "2026-06-08T10:00:00Z")]);

        var result = await ConsolidateAsync(textSchema, ms1.ToArray(), ms2.ToArray());

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => (string?)r["Name"] == "v2");
        Assert.DoesNotContain(result, r => (string?)r["Name"] == "v1");
    }

    [Fact]
    public async Task Consolidate_keeps_latest_version_per_id_across_sources()
    {
        const string id1 = "11111111-1111-1111-1111-111111111111";
        const string id2 = "22222222-2222-2222-2222-222222222222";

        // Fuente vieja: Id1 v1 (t1), Id2 a2 (t1). Fuente nueva: Id1 v2 (t2).
        var older = await ParquetBytes(
            Row(id1, "v1", "2026-06-08T10:00:00Z"),
            Row(id2, "a2", "2026-06-08T10:00:00Z"));
        var newer = await ParquetBytes(
            Row(id1, "v2", "2026-06-09T10:00:00Z"));

        using var output = new MemoryStream();
        // Newest-first: [newer, older].
        await ParquetConsolidator.ConsolidateAsync([Source(newer), Source(older)], Schema, output);

        output.Position = 0;
        var result = await ParquetMaterializer.ReadFromStreamAsync(output, Schema);

        Assert.Equal(2, result.Count); // una fila por Id
        var byId = result.ToDictionary(r => r["Id"]!.ToString()!, r => r["Name"]!.ToString());
        Assert.Equal("v2", byId[id1]); // Id1 se queda con la versión más nueva
        Assert.Equal("a2", byId[id2]); // Id2 intacto
    }

    // Las filas sin Id válido (vacío o no parseable en el CSV) se descartan y se cuentan, en vez
    // de colapsar todas en una única fila con Guid.Empty.
    [Fact]
    public async Task Consolidate_drops_and_counts_rows_without_id()
    {
        var bytes = await ParquetBytes(
            Row("11111111-1111-1111-1111-111111111111", "a", "2026-06-08T10:00:00Z"),
            new Dictionary<string, object?> { ["Id"] = null, ["Name"] = "x", ["SinkModifiedOn"] = DateTimeOffset.Parse("2026-06-08T10:00:00Z") },
            new Dictionary<string, object?> { ["Id"] = null, ["Name"] = "y", ["SinkModifiedOn"] = DateTimeOffset.Parse("2026-06-08T10:01:00Z") });

        using var output = new MemoryStream();
        var result = await ParquetConsolidator.ConsolidateAsync([Source(bytes)], Schema, output);

        Assert.Equal(1, result.Rows);
        Assert.Equal(2, result.RowsWithoutId);
    }
}
