using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Adls;

public static class CsvEntityReader
{
    // Lee filas en streaming: el parseo por línea (ParseCsvLine/TryCoerceValue) no depende de tener
    // el CSV completo en memoria, así que consume un IAsyncEnumerable<string> de líneas — la
    // fuente (AdlsClient) nunca tiene que juntar un archivo grande en un solo string.
    public static async IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(
        IAsyncEnumerable<string> lines,
        CdmEntitySchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        CsvValueIssues? issues = null)
    {
        // Nombre y tipo por posición, calculados una vez: antes se buscaba el atributo por nombre
        // en cada columna de cada fila (O(columnas²) por fila en tablas anchas).
        var columns = schema.Attributes.Select(a => a.Name).ToList();
        var sqlTypes = schema.Attributes.Select(a => a.SqlType).ToList();

        Dictionary<string, object?> ToRow(string record)
        {
            var values = ParseCsvLine(record);
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                var raw = i < values.Count ? values[i] : null;
                if (!TryCoerceValue(raw, sqlTypes[i], out var value))
                {
                    issues?.Record(columns[i], raw!);
                }

                row[columns[i]] = value;
            }

            return row;
        }

        // Synapse mete saltos de línea dentro de campos entrecomillados: una línea que deja las
        // comillas abiertas (número impar de '"' acumulado; "" escapado suma dos) se une con las
        // siguientes hasta cerrarlas. Sigue siendo streaming: solo se acumula un registro.
        var record = new StringBuilder();
        var insideQuotes = false;
        await foreach (var line in lines.WithCancellation(cancellationToken))
        {
            if (!insideQuotes && string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (insideQuotes)
            {
                record.Append('\n');
            }

            record.Append(line);
            if (line.Count(c => c == '"') % 2 == 1)
            {
                insideQuotes = !insideQuotes;
            }

            if (!insideQuotes)
            {
                yield return ToRow(record.ToString());
                record.Clear();
            }
        }

        // Comilla sin cerrar al final del archivo: se emite lo acumulado en vez de perderlo.
        if (record.Length > 0)
        {
            yield return ToRow(record.ToString());
        }
    }

    // Un campo vacío se devuelve como null (NormalizeField).
    public static IReadOnlyList<string?> ParseCsvLine(string line)
    {
        var result = new List<string?>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(NormalizeField(sb.ToString()));
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        result.Add(NormalizeField(sb.ToString()));
        return result;
    }

    private static string? NormalizeField(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value;
    }

    public static object? CoerceValue(string? raw, string sqlType) =>
        TryCoerceValue(raw, sqlType, out var value) ? value : null;

    // false si el valor no se puede leer con el tipo de su columna: queda en null y quien lee el CSV
    // lo cuenta (CsvValueIssues) para avisar en vez de perderlo en silencio.
    public static bool TryCoerceValue(string? raw, string sqlType, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        try
        {
            if (sqlType.StartsWith("UNIQUEIDENTIFIER", StringComparison.OrdinalIgnoreCase))
            {
                value = Guid.Parse(raw);
                return true;
            }

            if (sqlType.StartsWith("DATETIME", StringComparison.OrdinalIgnoreCase)
                || sqlType.StartsWith("DATETIMEOFFSET", StringComparison.OrdinalIgnoreCase))
            {
                // Synapse exporta en UTC: sin offset explícito no se toma la zona del servidor.
                value = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                return true;
            }

            if (sqlType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase))
            {
                if (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    value = true;
                    return true;
                }

                if (raw == "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    value = false;
                    return true;
                }

                return false;
            }

            if (sqlType.StartsWith("BIGINT", StringComparison.OrdinalIgnoreCase)
                || sqlType.StartsWith("INT", StringComparison.OrdinalIgnoreCase)
                || sqlType.StartsWith("SMALLINT", StringComparison.OrdinalIgnoreCase))
            {
                value = long.Parse(raw, CultureInfo.InvariantCulture);
                return true;
            }

            // NumberStyles.Float admite notación exponencial (1.5E-5), que antes acababa en null.
            if (sqlType.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase))
            {
                value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                return true;
            }

            if (sqlType.StartsWith("DECIMAL", StringComparison.OrdinalIgnoreCase))
            {
                value = decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }

        value = raw;
        return true;
    }
}

// Valores de un CSV que no se pudieron leer con el tipo de su columna (se cargan como NULL): recuento
// por columna con el primer valor de ejemplo, para el aviso del job.
public sealed class CsvValueIssues
{
    private const int MaxSampleLength = 40;
    private readonly Dictionary<string, (int Count, string Sample)> _byColumn = new(StringComparer.OrdinalIgnoreCase);

    public int Total { get; private set; }

    public void Record(string column, string raw)
    {
        var sample = raw.Length > MaxSampleLength ? raw[..MaxSampleLength] + "…" : raw;
        _byColumn[column] = _byColumn.TryGetValue(column, out var current) ? (current.Count + 1, current.Sample) : (1, sample);
        Total++;
    }

    public void Merge(CsvValueIssues other)
    {
        foreach (var (column, (count, sample)) in other._byColumn)
        {
            _byColumn[column] = _byColumn.TryGetValue(column, out var current) ? (current.Count + count, current.Sample) : (count, sample);
        }

        Total += other.Total;
    }

    public override string ToString() => string.Join("; ", _byColumn
        .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
        .Select(c => $"{c.Key}: {c.Value.Count} (p. ej. '{c.Value.Sample}')"));
}
