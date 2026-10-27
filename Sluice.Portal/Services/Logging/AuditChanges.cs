using System.Text.Json;

namespace Sluice.Portal.Services.Logging;

// Qué cambió al editar algo, para la entrada del registro: "clave: antes → después" por cada clave
// que no es secreta y "clave: cambiado" por cada secreto, nunca su valor. Text va en el texto
// guardado (Message); Part es lo mismo como (clave, args), para pintarlo en el idioma de quien lee.
public sealed record AuditChanges(string Text, object Part)
{
    // null si no cambió nada.
    public static AuditChanges? Between(
        IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after, Func<string, bool> isSecret)
    {
        var texts = new List<string>();
        object? part = null;
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var old = before.GetValueOrDefault(key);
            var now = after.GetValueOrDefault(key);
            if (string.Equals(old ?? "", now ?? "", StringComparison.Ordinal))
            {
                continue;
            }

            var secret = isSecret(key);
            var text = secret ? $"{key}: cambiado" : $"{key}: {Show(old)} → {Show(now)}";
            object item = secret ? new EventPart("Activity.Change.Secret", key) : text;
            texts.Add(text);
            part = part is null ? item : new EventPart("Activity.Change.Pair", part, item);
        }

        return part is null ? null : new AuditChanges(string.Join("; ", texts), part);
    }

    // Las propiedades de un registro tal como se guardan (camelCase, enums como texto), sin las que
    // no se editan.
    public static Dictionary<string, string?> Fields<T>(T value, params string[] ignore)
    {
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(value, JsonDefaults.Options).EnumerateObject())
        {
            if (!ignore.Contains(property.Name, StringComparer.Ordinal))
            {
                fields[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                };
            }
        }

        return fields;
    }

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "∅" : value;
}
