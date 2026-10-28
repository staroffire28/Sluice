using System.Text.Json;

namespace Sluice.Portal.Services.Logging;

// Qué cambió al editar algo, para la entrada del registro: "clave: antes → después" por cada clave
// que no es secreta y, de los secretos, solo sus claves ("secretos cambiados: password"), nunca su
// valor. Text va en el texto guardado (Message); Part es lo mismo como (clave, args), para pintarlo
// en el idioma de quien lee, con profundidad fija por muchas claves que cambien.
public sealed record AuditChanges(string Text, object Part)
{
    // null si no cambió nada.
    public static AuditChanges? Between(
        IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after, Func<string, bool> isSecret)
    {
        var texts = new List<string>();
        var secrets = new List<string>();
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var old = before.GetValueOrDefault(key);
            var now = after.GetValueOrDefault(key);
            if (string.Equals(old ?? "", now ?? "", StringComparison.Ordinal))
            {
                continue;
            }

            if (isSecret(key))
            {
                secrets.Add(key);
            }
            else
            {
                texts.Add($"{key}: {Show(old)} → {Show(now)}");
            }
        }

        var text = string.Join("; ", texts);
        if (secrets.Count == 0)
        {
            return texts.Count == 0 ? null : new AuditChanges(text, text);
        }

        var secretKeys = string.Join(", ", secrets);
        var secretsPart = new EventPart("Activity.Change.Secrets", secretKeys);
        return texts.Count == 0
            ? new AuditChanges($"secretos cambiados: {secretKeys}", secretsPart)
            : new AuditChanges($"{text}; secretos cambiados: {secretKeys}", new EventPart("Activity.Change.Pair", text, secretsPart));
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
