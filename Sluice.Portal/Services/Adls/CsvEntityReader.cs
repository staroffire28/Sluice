using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Adls;

public static class CsvEntityReader
{
    // Lee filas en streaming: el parseo por línea (ParseCsvLine/CoerceValue) no depende de tener
    // el CSV completo en memoria, así que consume un IAsyncEnumerable<string> de líneas — la
    // fuente (AdlsClient) nunca tiene que juntar un archivo grande en un solo string.
    public static async IAsyncEnumerable<Dictionary<string, object?>> ReadRowsAsync(
        IAsyncEnumerable<string> lines,
        CdmEntitySchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                row[columns[i]] = CoerceValue(raw, sqlTypes[i]);
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

    public static object? CoerceValue(string? raw, string sqlType)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            if (sqlType.StartsWith("UNIQUEIDENTIFIER", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.Parse(raw);
            }

            if (sqlType.StartsWith("DATETIME", StringComparison.OrdinalIgnoreCase)
                || sqlType.StartsWith("DATETIMEOFFSET", StringComparison.OrdinalIgnoreCase))
            {
                // Synapse exporta en UTC: sin offset explícito no se toma la zona del servidor.
                return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            }

            if (sqlType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase))
            {
                return raw is "1" or "true" or "True";
            }

            if (sqlType.StartsWith("BIGINT", StringComparison.OrdinalIgnoreCase)
                || sqlType.StartsWith("INT", StringComparison.OrdinalIgnoreCase)
                || sqlType.StartsWith("SMALLINT", StringComparison.OrdinalIgnoreCase))
            {
                return long.Parse(raw, CultureInfo.InvariantCulture);
            }

            // NumberStyles.Float admite notación exponencial (1.5E-5), que antes acababa en null.
            if (sqlType.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase))
            {
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            if (sqlType.StartsWith("DECIMAL", StringComparison.OrdinalIgnoreCase))
            {
                return decimal.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            return null;
        }

        return raw;
    }
}