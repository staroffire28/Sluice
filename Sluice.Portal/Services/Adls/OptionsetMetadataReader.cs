namespace Sluice.Portal.Services.Adls;

/// <summary>
/// Lee y parsea GlobalOptionsetMetadata.csv (export de Synapse Link F&O). Réplica fiel del SP
/// dvtosql.source_GetEnumTranslation de Microsoft: agrupa por (EntityName, OptionSetName) el mapa
/// Option -> ExternalValue, filtrando idioma 1033 (inglés) y excluyendo el optionset sysdatastatecode.
///
/// El CSV no trae cabecera. Orden de columnas (igual que el WITH del SP):
///   0 OptionSetName | 1 Option | 2 IsUserLocalizedLabel | 3 LocalizedLabelLanguageCode
///   4 LocalizedLabel | 5 GlobalOptionSetName | 6 EntityName | 7 ExternalValue
/// </summary>
public static class OptionsetMetadataReader
{
    private const string EnglishLanguageCode = "1033";
    private const string ExcludedOptionSet = "sysdatastatecode";

    // entidad -> (columna optionset -> (Option -> ExternalValue))
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>> Parse(string csv)
    {
        // Quita el BOM inicial si el llamador pasa el texto crudo (el StreamReader del path de
        // producción ya lo consume, pero en tests/otros usos un BOM dejaría ﻿ pegado al
        // primer OptionSetName y descartaría esa fila al no casar con ninguna columna real).
        csv = csv.TrimStart('﻿');

        // entity -> column -> option -> externalValue (mutable durante la construcción)
        var acc = new Dictionary<string, Dictionary<string, Dictionary<long, string>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fields in EnumerateRows(csv))
        {
            if (fields.Count < 8)
            {
                continue;
            }

            var optionSetName = fields[0];
            var languageCode = fields[3];
            var entityName = fields[6];
            var externalValue = fields[7];

            if (!string.Equals(languageCode, EnglishLanguageCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(optionSetName, ExcludedOptionSet, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entityName) || string.IsNullOrEmpty(optionSetName))
            {
                continue;
            }

            if (!long.TryParse(fields[1], out var option))
            {
                continue;
            }

            var byColumn = acc.TryGetValue(entityName, out var cols)
                ? cols
                : acc[entityName] = new Dictionary<string, Dictionary<long, string>>(StringComparer.OrdinalIgnoreCase);

            var byOption = byColumn.TryGetValue(optionSetName, out var opts)
                ? opts
                : byColumn[optionSetName] = new Dictionary<long, string>();

            // Igual que el GROUP BY del SP: la primera fila por (entidad, columna, option) gana.
            byOption.TryAdd(option, externalValue);
        }

        return acc.ToDictionary(
            e => e.Key,
            e => (IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>)e.Value.ToDictionary(
                c => c.Key,
                c => (IReadOnlyDictionary<long, string>)c.Value,
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    // Parser CSV mínimo: campos separados por coma, comillas dobles opcionales con "" de escape.
    // Synapse exporta sin cabecera y puede meter saltos de línea dentro de campos entrecomillados.
    private static IEnumerable<List<string>> EnumerateRows(string csv)
    {
        var field = new System.Text.StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var rowHasContent = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    rowHasContent = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    rowHasContent = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    if (rowHasContent || field.Length > 0 || row.Count > 0)
                    {
                        row.Add(field.ToString());
                        yield return row;
                        row = new List<string>();
                        field.Clear();
                        rowHasContent = false;
                    }

                    break;
                default:
                    field.Append(c);
                    rowHasContent = true;
                    break;
            }
        }

        if (rowHasContent || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
