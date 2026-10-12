using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Adls;

/// <summary>
/// Añade a un <see cref="CdmEntitySchema"/> las columnas de traducción de enum [<col>_$label] al
/// final de la tabla, una por cada optionset que (a) esté en el metadata y (b) exista como columna
/// real de la entidad. Equivale a concatenar el resultado de source_GetEnumTranslation detrás de
/// {selectcolumns} en source_createOrAlterViews. Idempotente.
/// </summary>
public static class EnumLabelEnricher
{
    public const string LabelSuffix = "_$label";

    public static CdmEntitySchema Apply(
        CdmEntitySchema schema,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<long, string>>> optionsetMetadata)
    {
        if (!optionsetMetadata.TryGetValue(schema.EntityName, out var columns) || columns.Count == 0)
        {
            return schema;
        }

        var realColumns = new HashSet<string>(
            schema.Attributes.Select(a => a.Name),
            StringComparer.OrdinalIgnoreCase);

        var labels = new List<CdmAttribute>();
        foreach (var (column, map) in columns)
        {
            if (!realColumns.Contains(column))
            {
                continue;
            }

            var labelName = column + LabelSuffix;
            if (realColumns.Contains(labelName))
            {
                continue;
            }

            labels.Add(new CdmAttribute
            {
                Name = labelName,
                SqlType = "NVARCHAR(MAX)",
                IsNullable = true,
                EnumSourceColumn = column,
                EnumMap = map,
            });
        }

        if (labels.Count == 0)
        {
            return schema;
        }

        var attrs = schema.Attributes.ToList();
        attrs.AddRange(labels.OrderBy(a => a.Name, StringComparer.Ordinal));
        return schema with { Attributes = attrs };
    }
}
