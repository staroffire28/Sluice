using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Tests;

public class CsvEntityReaderTests
{
    // Filas "cortas" (menos columnas que el esquema): las que faltan quedan a null.
    [Fact]
    public async Task ReadRowsAsync_HandlesRaggedRows()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "account",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };

        var csv = """
            11111111-1111-1111-1111-111111111111,Alice,2026-06-08T10:00:00Z
            22222222-2222-2222-2222-222222222222,Bob
            """;

        var rows = new List<Dictionary<string, object?>>();
        await foreach (var row in CsvEntityReader.ReadRowsAsync(ToAsyncLines(csv), schema))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), rows[0]["Id"]);
        Assert.Equal("Alice", rows[0]["Name"]);
        Assert.NotNull(rows[0]["SinkModifiedOn"]);
        Assert.Equal("Bob", rows[1]["Name"]);
        Assert.Null(rows[1]["SinkModifiedOn"]);
    }

    [Fact]
    public void CoerceValue_InvalidCast_ReturnsNull()
    {
        var value = CsvEntityReader.CoerceValue("not-a-guid", "UNIQUEIDENTIFIER");
        Assert.Null(value);
    }

    // Simula un CSV "grande" (decenas de miles de filas) para confirmar que el streaming produce
    // el conteo correcto sin materializar nada de más — no es una prueba de memoria real (eso solo
    // se confirma contra el storage account real), pero sí de que el parseo por lotes no pierde ni
    // duplica filas al procesar un volumen mayor al de los tests unitarios típicos.
    [Fact]
    public async Task ReadRowsAsync_HandlesManyRows_WithoutLossOrDuplication()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "bigtable",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Seq", SqlType = "BIGINT" },
            ],
        };

        const int rowCount = 25_000;
        IEnumerable<string> GenerateLines()
        {
            for (var i = 0; i < rowCount; i++)
            {
                yield return $"{Guid.NewGuid()},{i}";
            }
        }

        var seen = new HashSet<long>();
        var count = 0;
        await foreach (var row in CsvEntityReader.ReadRowsAsync(ToAsyncLines(GenerateLines()), schema))
        {
            count++;
            Assert.True(seen.Add((long)row["Seq"]!));
        }

        Assert.Equal(rowCount, count);
    }

    // Synapse mete saltos de línea dentro de campos entrecomillados (notas, descripciones): antes
    // el campo partía la fila en dos (una truncada y otra basura con Id inválido).
    [Fact]
    public async Task ReadRowsAsync_JoinsQuotedFieldsSpanningSeveralLines()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "note",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Note", SqlType = "NVARCHAR(MAX)" },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };
        const string csv =
            "11111111-1111-1111-1111-111111111111,\"línea 1\nlínea \"\"2\"\"\n\nlínea 4\",2026-06-08T10:00:00Z\n" +
            "22222222-2222-2222-2222-222222222222,simple,2026-06-08T10:05:00Z";

        var rows = new List<Dictionary<string, object?>>();
        await foreach (var row in CsvEntityReader.ReadRowsAsync(ToAsyncLines(csv), schema))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal("línea 1\nlínea \"2\"\n\nlínea 4", rows[0]["Note"]);
        Assert.NotNull(rows[0]["SinkModifiedOn"]);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), rows[1]["Id"]);
    }

    [Theory]
    [InlineData("1.5E-5", "FLOAT")]
    [InlineData("2e3", "DECIMAL(38,6)")]
    public void CoerceValue_ParsesNumbersWithExponent(string raw, string sqlType) =>
        Assert.NotNull(CsvEntityReader.CoerceValue(raw, sqlType));

    // Synapse exporta en UTC: una fecha sin offset no debe tomar la zona horaria del servidor.
    [Fact]
    public void CoerceValue_DateWithoutOffset_IsUtc()
    {
        var value = Assert.IsType<DateTimeOffset>(CsvEntityReader.CoerceValue("2026-06-08 10:00:00", "DATETIME2(7)"));

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(10, value.Hour);
    }

    private static async IAsyncEnumerable<string> ToAsyncLines(string csv)
    {
        foreach (var line in csv.Split('\n'))
        {
            yield return line;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<string> ToAsyncLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            yield return line;
            await Task.Yield();
        }
    }
}