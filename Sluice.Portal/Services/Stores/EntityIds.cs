using System.Globalization;
using System.Text;

namespace Sluice.Portal.Services.Stores;

// Los ids de conexiones, pipelines y jobs son nombres de archivo bajo DATA_DIR
// ({id}.json): un id con separadores o ".." escribiría fuera del directorio.
public static class EntityIds
{
    private const int MaxLength = 128;
    private static readonly char[] Forbidden = ['/', '\\', ':', '<', '>', '"', '|', '?', '*'];

    // Permisivo a propósito: basta con que sea un único segmento de nombre de archivo válido
    // en Windows y Linux, para no dejar inaccesibles ids ya guardados (p. ej. con acentos).
    // Los ids nuevos generados desde la UI usan además Slugify.
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaxLength
        && id == id.Trim()
        && id is not ("." or "..")
        && !id.EndsWith('.') // Windows recorta el punto final: "x." y "x" serían el mismo archivo
        && id.IndexOfAny(Forbidden) < 0
        && !id.Any(char.IsControl);

    // Slug estricto [a-z0-9-] a partir de un nombre visible: "Ñandú 2" -> "nandu-2".
    public static string Slugify(string? name)
    {
        var slug = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }
}
