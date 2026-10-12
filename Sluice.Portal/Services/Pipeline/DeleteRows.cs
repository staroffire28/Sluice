using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

// Filas de borrado de Synapse Link para F&O: IsDelete = True y el cuerpo vacío. El consolidado
// las conserva (impiden que una versión anterior del Id vuelva en el siguiente incremental); con
// DeleteMode.Delete se filtran al cargar SQL y al contar o comparar contra SQL.
public static class DeleteRows
{
    // Las filas del parquet usan diccionarios sin distinguir mayúsculas.
    public static bool IsDeleteRow(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue(CdmSchemaParser.IsDeleteColumn, out var value) && IsDeleteValue(value);

    // BIT (boolean en el model.json) se lee como bool; si la columna llega tipada como texto, el
    // CSV trae "True" o "1".
    public static bool IsDeleteValue(object? value) => value switch
    {
        bool flag => flag,
        string text => text == "1" || (bool.TryParse(text, out var parsed) && parsed),
        _ => false,
    };
}
