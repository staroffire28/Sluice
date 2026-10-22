using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

// Pinta un texto guardado como (clave, args) en el idioma de quien lo ve. Sin clave, con una clave
// que el resx no tiene o con args que no encajan en la plantilla, devuelve el texto guardado: así
// los eventos anteriores a la clave y los de un worker más antiguo se siguen leyendo igual.
public static class EventText
{
    public static string Render(IStringLocalizer l, JobEvent e) => Render(l, e.MessageKey, e.MessageArgs, e.Message);

    public static string Render(IStringLocalizer l, string? key, object?[]? args, string fallback)
    {
        if (key is null || l[key] is not { ResourceNotFound: false } template)
        {
            return fallback;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template.Value, (args ?? []).Select(Unwrap).ToArray());
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    // Leídos de disco, los args llegan como JsonElement: los números vuelven a ser números para que
    // la plantilla los formatee con la cultura ({0:N0}).
    private static object? Unwrap(object? arg) => arg is not JsonElement json ? arg : json.ValueKind switch
    {
        JsonValueKind.Number => json.TryGetInt64(out var n) ? n : json.GetDouble(),
        JsonValueKind.String => json.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => json.GetRawText(),
    };
}
