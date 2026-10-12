using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Comparación de valores: cada lado se normaliza a como SQL guarda el dato, así que las diferencias
// de representación (escala decimal, Guid en texto, DateTimeOffset al leer) no cuentan como
// diferencias; solo las de contenido.
public sealed class ValueComparerTests
{
    private static readonly Guid IdA = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly Guid IdB = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid IdC = Guid.Parse("a3bb189e-8bf9-3888-9912-ace4e6543002");
    private static readonly Guid IdD = Guid.Parse("e4eaaaf2-d142-11e1-b3e4-080027620cdd");

    private static readonly List<CdmAttribute> Columns =
    [
        new() { Name = "Id", SqlType = "UNIQUEIDENTIFIER" },
        new() { Name = "Name", SqlType = "NVARCHAR(MAX)" },
        new() { Name = "Amount", SqlType = "DECIMAL(32,2)" },
        new() { Name = "ModifiedOn", SqlType = "DATETIME2(7)" },
        new() { Name = "IsActive", SqlType = "BIT" },
    ];

    private static readonly DateTime Modified = new(2026, 10, 9, 12, 30, 15, DateTimeKind.Utc);

    [Fact]
    public void Decimales_equivalentes_tienen_la_misma_forma()
    {
        // SQL redondea al scale de la columna (mitades hacia fuera) y 1.5 = 1.500000.
        Assert.Equal(ValueComparer.Canonical(1.234m, "DECIMAL(32,2)"), ValueComparer.Canonical(1.23m, "DECIMAL(32,2)"));
        Assert.Equal(ValueComparer.Canonical(2.345m, "DECIMAL(32,2)"), ValueComparer.Canonical(2.35m, "DECIMAL(32,2)"));
        Assert.Equal(ValueComparer.Canonical(1.5m, "DECIMAL(38,6)"), ValueComparer.Canonical(1.500000m, "DECIMAL(38,6)"));
        Assert.NotEqual(ValueComparer.Canonical(1.5m, "DECIMAL(38,6)"), ValueComparer.Canonical(1.51m, "DECIMAL(38,6)"));
    }

    [Fact]
    public void Fechas_guid_bit_y_enteros_se_normalizan_como_los_guarda_SQL()
    {
        // El parquet trae DateTime (UTC); SQL devuelve DateTime sin Kind o DateTimeOffset.
        Assert.Equal(
            ValueComparer.Canonical(Modified, "DATETIME2(7)"),
            ValueComparer.Canonical(DateTime.SpecifyKind(Modified, DateTimeKind.Unspecified), "DATETIME2(7)"));
        Assert.Equal(
            ValueComparer.Canonical(Modified, "DATETIMEOFFSET(7)"),
            ValueComparer.Canonical(new DateTimeOffset(DateTime.SpecifyKind(Modified, DateTimeKind.Unspecified), TimeSpan.Zero), "DATETIMEOFFSET(7)"));
        Assert.NotEqual(ValueComparer.Canonical(Modified, "DATETIME2(7)"), ValueComparer.Canonical(Modified.AddTicks(1), "DATETIME2(7)"));

        Assert.Equal(ValueComparer.Canonical(IdA.ToString().ToUpperInvariant(), "UNIQUEIDENTIFIER"), ValueComparer.Canonical(IdA, "UNIQUEIDENTIFIER"));
        Assert.Equal(ValueComparer.Canonical("1", "BIT"), ValueComparer.Canonical(true, "BIT"));
        Assert.Equal(ValueComparer.Canonical(42, "BIGINT"), ValueComparer.Canonical(42L, "BIGINT"));
        Assert.Null(ValueComparer.Canonical(DBNull.Value, "NVARCHAR(MAX)"));
    }

    [Fact]
    public void NULL_y_cadena_vacia_no_dan_el_mismo_hash()
    {
        List<CdmAttribute> cols = [new() { Name = "Name", SqlType = "NVARCHAR(MAX)" }];

        Assert.NotEqual(ValueComparer.RowHash(cols, [null]), ValueComparer.RowHash(cols, [""]));
    }

    [Fact]
    public async Task Cuenta_iguales_distintas_faltantes_y_sobrantes_por_Id()
    {
        var parquet = new[]
        {
            Row(IdA, "Contoso", 10.0m, true),
            Row(IdB, "Fabrikam", 20.5m, false),
            Row(IdC, "Northwind", 30m, true),
        };
        var sql = new[]
        {
            // A: mismos valores con otra representación (escala, Guid, sin Kind, bool).
            SqlRow(IdA, "Contoso", 10.00m, true),
            // B: nombre cambiado en SQL.
            SqlRow(IdB, "Fabrikam Inc", 20.50m, false),
            // D: no está en el parquet; A repetida en SQL.
            SqlRow(IdD, "Extra", 1m, true),
            SqlRow(IdA, "Contoso", 10.00m, true),
        };

        var result = await ValueComparer.CompareAsync("account", Columns, Stream(parquet), Stream(sql));

        Assert.Null(result.Error);
        Assert.Equal(3, result.SourceRows);
        Assert.Equal(4, result.DestinationRows);
        Assert.Equal(1, result.Matching);
        Assert.Equal(1, result.Different);
        Assert.Equal(1, result.MissingInDestination);
        Assert.Equal(2, result.ExtraInDestination);
        Assert.Equal([IdB.ToString()], result.SampleDifferent);
        Assert.Equal([IdC.ToString()], result.SampleMissing);
        Assert.Equal([IdD.ToString(), IdA.ToString()], result.SampleExtra);
        Assert.Equal(Columns.Count, result.ComparedColumns);
        Assert.True(result.HasDifferences);
    }

    [Fact]
    public async Task Tablas_identicas_no_tienen_diferencias()
    {
        var result = await ValueComparer.CompareAsync(
            "account",
            Columns,
            Stream([Row(IdA, "Contoso", 10m, true), Row(IdB, null, 0m, false)]),
            Stream([SqlRow(IdB, null, 0.00m, false), SqlRow(IdA, "Contoso", 10.00m, true)]));

        Assert.Equal(2, result.Matching);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public async Task Id_de_texto_que_no_es_guid_tambien_se_empareja()
    {
        List<CdmAttribute> cols = [new() { Name = "Id", SqlType = "NVARCHAR(MAX)" }, new() { Name = "Qty", SqlType = "INT" }];

        var result = await ValueComparer.CompareAsync(
            "items",
            cols,
            Stream([new Dictionary<string, object?> { ["Id"] = "ITEM-1", ["Qty"] = 5 }, new Dictionary<string, object?> { ["Id"] = "ITEM-2", ["Qty"] = 7 }]),
            Stream<object?[]>([["ITEM-1", 5L], ["ITEM-2", 8L]]));

        Assert.Equal(1, result.Matching);
        Assert.Equal(1, result.Different);
        Assert.Equal(["ITEM-2"], result.SampleDifferent);
    }

    [Fact]
    public async Task Sin_columna_Id_devuelve_error_sin_comparar()
    {
        List<CdmAttribute> cols = [new() { Name = "Name", SqlType = "NVARCHAR(MAX)" }];

        var result = await ValueComparer.CompareAsync("noid", cols, Stream<Dictionary<string, object?>>([]), Stream<object?[]>([]));

        Assert.NotNull(result.Error);
        Assert.False(result.HasDifferences);
    }

    private static Dictionary<string, object?> Row(Guid id, string? name, decimal amount, bool active) => new()
    {
        ["Id"] = id.ToString(),
        ["Name"] = name,
        ["Amount"] = amount,
        ["ModifiedOn"] = Modified,
        ["IsActive"] = active,
    };

    // Como lo devuelve SqlDataReader: Guid, decimal con la escala de la columna y DateTime sin Kind.
    private static object?[] SqlRow(Guid id, string? name, decimal amount, bool active) =>
        [id, name, amount, DateTime.SpecifyKind(Modified, DateTimeKind.Unspecified), active];

    private static async IAsyncEnumerable<T> Stream<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }
}
