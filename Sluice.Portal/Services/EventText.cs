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

    public static string Render(IStringLocalizer l, ActivityLogEntry e) => Render(l, e.MessageKey, e.MessageArgs, e.Message);

    // El mensaje de un registro con el nombre del pipeline en vez de su id: se sustituyen los
    // argumentos que son exactamente el id (no el texto, que lo lleva también dentro del id del
    // job). Lo usan la tabla de Logs y la búsqueda de su CSV, para que encuentren por el mismo texto.
    public static string Render(IStringLocalizer l, ActivityLogEntry e, IReadOnlyDictionary<string, string> pipelineNames)
    {
        if (e.PipelineId is not { } id || e.MessageArgs is not { } args || !pipelineNames.TryGetValue(id, out var name))
        {
            return Render(l, e);
        }

        var named = args.Select(a => IsText(a, id) ? name : a).ToArray();
        return Render(l, e.MessageKey, named, e.Message);
    }

    public static string Render(IStringLocalizer l, string? key, object?[]? args, string fallback) =>
        key is null ? fallback : TryRender(l, key, args) ?? fallback;

    private static string? TryRender(IStringLocalizer l, string key, object?[]? args)
    {
        if (l[key] is not { ResourceNotFound: false } template)
        {
            return null;
        }

        var values = new object?[args?.Length ?? 0];
        for (var i = 0; i < values.Length; i++)
        {
            if (!TryUnwrap(l, args![i], out values[i]))
            {
                return null;
            }
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template.Value, values);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Leídos de disco, los args llegan como JsonElement: los números vuelven a ser números para que
    // la plantilla los formatee con la cultura ({0:N0}), y una parte ({ key, args }) se pinta a su
    // vez. Si una parte no se puede pintar, falla el texto entero (se muestra el guardado).
    private static bool TryUnwrap(IStringLocalizer l, object? arg, out object? value)
    {
        value = arg;
        if (arg is EventPart part)
        {
            return (value = TryRender(l, part.Key, part.Args)) is not null;
        }

        if (arg is not JsonElement json)
        {
            return true;
        }

        if (json.ValueKind == JsonValueKind.Object)
        {
            if (!json.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            object?[]? partArgs = json.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => (object?)x.Clone()).ToArray()
                : null;
            return (value = TryRender(l, key.GetString()!, partArgs)) is not null;
        }

        value = json.ValueKind switch
        {
            JsonValueKind.Number => json.TryGetInt64(out var n) ? n : json.GetDouble(),
            JsonValueKind.String => json.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => json.GetRawText(),
        };
        return true;
    }

    private static bool IsText(object? arg, string value) => arg switch
    {
        string s => s == value,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() == value,
        _ => false,
    };
}

// Un trozo de un texto compuesto (p. ej. las partes opcionales del cierre del ingest): se guarda
// como { key, args } dentro de MessageArgs y se pinta con su propia clave.
public sealed record EventPart(string Key, params object?[] Args);
