using System.Text;

namespace Sluice.Portal.Services;

// CSV de las exportaciones (Logs, Jobs): separador coma, comillas según RFC 4180 y UTF-8 con BOM
// para que Excel lea bien las tildes.
public static class CsvWriter
{
    private static readonly char[] NeedsQuotes = [',', '"', '\r', '\n'];

    public static StreamWriter Create(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 64 * 1024, leaveOpen: true);

    public static void WriteRow(TextWriter writer, IEnumerable<string?> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                writer.Write(',');
            }

            writer.Write(Escape(field));
            first = false;
        }

        writer.Write("\r\n");
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // Un mensaje de log puede traer texto de fuera: una celda que empieza por = + - @ Excel la
        // ejecutaría como fórmula (inyección CSV). Con ' delante se muestra como texto.
        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny(NeedsQuotes) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
