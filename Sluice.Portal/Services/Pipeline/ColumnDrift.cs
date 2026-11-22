using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Pipeline;

// Una columna de la tabla destino tal como la describe INFORMATION_SCHEMA.COLUMNS.
public sealed record SqlColumnType(string Name, string DataType, int? MaxLength, int? Precision, int? Scale, int? DateTimePrecision);

// La tabla SQL frente al esquema del origen que carga la etapa SQL. EnsureTableAsync solo añade
// columnas y amplía texto: un tipo que cambia en el origen o una columna que deja de llegar se quedan
// así en SQL. Sin estado: se mira en cada carga.
public static class ColumnDrift
{
    // El tipo de la columna en la forma en que lo escribe SqlTypeMapper. INFORMATION_SCHEMA da
    // precisión también en los enteros y en FLOAT: solo cuenta en DECIMAL/NUMERIC.
    public static string Describe(SqlColumnType column)
    {
        var type = column.DataType.ToUpperInvariant();
        return type switch
        {
            "DECIMAL" or "NUMERIC" => $"{type}({column.Precision},{column.Scale})",
            "DATETIME2" or "DATETIMEOFFSET" or "TIME" => $"{type}({column.DateTimePrecision})",
            "NVARCHAR" or "VARCHAR" or "NCHAR" or "CHAR" or "VARBINARY" or "BINARY" =>
                $"{type}({(column.MaxLength == -1 ? "MAX" : column.MaxLength?.ToString())})",
            _ => type,
        };
    }

    // Columnas que están en los dos lados con distinto tipo, y columnas de la tabla que no están en el
    // esquema del origen ni excluidas en el catálogo (esas tienen su propio aviso). null cuando no hay.
    // No distingue una columna que desapareció del origen de una añadida a mano en SQL.
    public static (List<ColumnTypeMismatch>? TypeMismatches, List<string>? NotInSource) Compare(
        IReadOnlyList<SqlColumnType> table, CdmEntitySchema source, IReadOnlySet<string> excluded)
    {
        var sourceTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attr in source.Attributes)
        {
            sourceTypes.TryAdd(attr.Name, attr.SqlType);
        }

        var excludedColumns = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        var mismatches = new List<ColumnTypeMismatch>();
        var notInSource = new List<string>();
        foreach (var column in table)
        {
            if (sourceTypes.TryGetValue(column.Name, out var sourceType))
            {
                var sqlType = Describe(column);
                if (Normalize(sqlType) != Normalize(sourceType))
                {
                    mismatches.Add(new ColumnTypeMismatch(column.Name, sqlType, sourceType));
                }
            }
            else if (!excludedColumns.Contains(column.Name))
            {
                notInSource.Add(column.Name);
            }
        }

        return (mismatches.Count > 0 ? mismatches : null, notInSource.Count > 0 ? notInSource : null);
    }

    // Forma comparable: sin mayúsculas; todo NVARCHAR es igual (la etapa lo amplía a MAX); NUMERIC es
    // DECIMAL; DATETIME2/DATETIMEOFFSET sin precisión (los admite SqlMergeBuilder) son la de 7.
    private static string Normalize(string sqlType)
    {
        var type = sqlType.Replace(" ", "").ToUpperInvariant();
        if (type.StartsWith("NVARCHAR", StringComparison.Ordinal))
        {
            return "NVARCHAR";
        }

        if (type.StartsWith("NUMERIC(", StringComparison.Ordinal))
        {
            return "DECIMAL" + type["NUMERIC".Length..];
        }

        return type is "DATETIME2" or "DATETIMEOFFSET" ? type + "(7)" : type;
    }
}
