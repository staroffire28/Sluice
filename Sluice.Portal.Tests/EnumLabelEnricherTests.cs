using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Tests;

public sealed class EnumLabelEnricherTests
{
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>> Meta(
        string entity, string column, params (long Option, string External)[] values)
    {
        var map = values.ToDictionary(v => v.Option, v => v.External);
        return new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>>(StringComparer.OrdinalIgnoreCase)
        {
            [entity] = new Dictionary<string, IReadOnlyDictionary<long, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [column] = map,
            },
        };
    }

    private static CdmEntitySchema Schema(string entity, params string[] columns) => new()
    {
        EntityName = entity,
        Attributes = columns.Select(c => new CdmAttribute { Name = c, SqlType = "BIGINT" }).ToList(),
    };

    [Fact]
    public void Apply_AppendsLabelColumnAtEnd_WithEnumSourceAndMap()
    {
        var schema = Schema("accountingdistribution", "Id", "amountsource");
        var meta = Meta("accountingdistribution", "amountsource", (0, "PrimaryAmount"), (1, "Discount"));

        var result = EnumLabelEnricher.Apply(schema, meta);

        var last = result.Attributes[^1];
        Assert.Equal("amountsource_$label", last.Name);
        Assert.Equal("amountsource", last.EnumSourceColumn);
        Assert.Equal("NVARCHAR(MAX)", last.SqlType);
        Assert.NotNull(last.EnumMap);
        Assert.Equal("Discount", last.EnumMap![1]);
    }

    [Fact]
    public void Apply_IgnoresOptionsetsWithoutMatchingRealColumn()
    {
        var schema = Schema("accountingdistribution", "Id", "amountsource");
        var meta = Meta("accountingdistribution", "doesnotexist", (0, "X"));

        var result = EnumLabelEnricher.Apply(schema, meta);

        Assert.DoesNotContain(result.Attributes, a => a.Name.EndsWith("_$label"));
    }

    [Fact]
    public void Apply_EntityNotInMetadata_ReturnsSchemaUnchanged()
    {
        var schema = Schema("unrelated", "Id", "col");
        var meta = Meta("accountingdistribution", "amountsource", (0, "X"));

        var result = EnumLabelEnricher.Apply(schema, meta);

        Assert.Same(schema, result);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var schema = Schema("accountingdistribution", "Id", "amountsource");
        var meta = Meta("accountingdistribution", "amountsource", (0, "PrimaryAmount"));

        var once = EnumLabelEnricher.Apply(schema, meta);
        var twice = EnumLabelEnricher.Apply(once, meta);

        Assert.Equal(once.Attributes.Count, twice.Attributes.Count);
        Assert.Single(twice.Attributes, a => a.Name == "amountsource_$label");
    }
}
