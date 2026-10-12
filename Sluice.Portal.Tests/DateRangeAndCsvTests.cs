using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

// Filtro por fecha de Logs y Jobs (DateRange) y CSV de exportación (CsvWriter).
public sealed class DateRangeAndCsvTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void An_empty_range_contains_everything()
    {
        Assert.True(new DateRange(null, null).Contains(At("2026-10-09T12:00:00Z"), Utc));
        Assert.True(new DateRange(null, null).IsEmpty);
    }

    // Ambos extremos incluidos y por día completo: "Hasta 9 oct" incluye las 23:59:59 del 9.
    [Theory]
    [InlineData("2026-10-08T23:59:59Z", false)]
    [InlineData("2026-10-09T00:00:00Z", true)]
    [InlineData("2026-10-10T23:59:59Z", true)]
    [InlineData("2026-10-11T00:00:00Z", false)]
    public void Both_ends_are_inclusive_whole_days(string timestamp, bool expected)
    {
        var range = new DateRange(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 10));

        Assert.Equal(expected, range.Contains(At(timestamp), Utc));
    }

    [Fact]
    public void Only_from_or_only_to_bound_one_side()
    {
        var from = new DateRange(new DateOnly(2026, 10, 9), null);
        var to = new DateRange(null, new DateOnly(2026, 10, 9));

        Assert.False(from.Contains(At("2026-10-08T10:00:00Z"), Utc));
        Assert.True(from.Contains(At("2030-01-01T00:00:00Z"), Utc));
        Assert.True(to.Contains(At("2020-01-01T00:00:00Z"), Utc));
        Assert.False(to.Contains(At("2026-10-10T00:00:00Z"), Utc));
    }

    // El día se toma en la zona que muestran las tablas: 03:00 UTC del 10 es el 9 en UTC-5.
    [Fact]
    public void The_day_is_taken_in_the_given_time_zone()
    {
        var bogota = TimeZoneInfo.CreateCustomTimeZone("UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");
        var range = new DateRange(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 9));

        Assert.True(range.Contains(At("2026-10-10T03:00:00Z"), bogota));
        Assert.False(range.Contains(At("2026-10-10T03:00:00Z"), Utc));
    }

    [Fact]
    public void Csv_uses_commas_and_quotes_fields_that_need_it()
    {
        var csv = Write(["a", "b,c", "di \"jo\"", "línea\nnueva", null]);

        Assert.Equal("a,\"b,c\",\"di \"\"jo\"\"\",\"línea\nnueva\",\r\n", csv);
    }

    // Un mensaje de log puede traer texto de fuera: Excel no debe ejecutarlo como fórmula.
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+cmd")]
    [InlineData("-2")]
    [InlineData("@SUM(A1)")]
    public void Csv_neutralizes_formula_like_fields(string value)
    {
        Assert.Equal($"'{value}\r\n", Write([value]));
    }

    [Fact]
    public void Csv_starts_with_a_utf8_bom_so_excel_reads_accents()
    {
        using var stream = new MemoryStream();
        using (var writer = CsvWriter.Create(stream))
        {
            CsvWriter.WriteRow(writer, ["Conciliación"]);
        }

        var bytes = stream.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("Conciliación\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

    private static string Write(string?[] fields)
    {
        using var writer = new StringWriter();
        CsvWriter.WriteRow(writer, fields);
        return writer.ToString();
    }
}
