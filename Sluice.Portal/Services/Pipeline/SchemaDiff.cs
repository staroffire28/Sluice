using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Pipeline;

// Cambios de esquema de una tabla entre el consolidado anterior y la ejecución actual (columnas
// del model.json de Synapse Link). Sluice añade en SQL las columnas nuevas, pero las que dejan de
// llegar y los cambios de tipo no se reflejan solos: se avisan en el job.
public sealed record SchemaDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<(string Column, string Before, string After)> TypeChanged)
{
    public const string ColumnsAddedCode = "schema.columnsAdded";
    public const string ColumnsRemovedCode = "schema.columnsRemoved";
    public const string TypeChangedCode = "schema.typeChanged";

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && TypeChanged.Count == 0;

    public static SchemaDiff Compare(CdmEntitySchema previous, CdmEntitySchema current)
    {
        var before = previous.Attributes.ToDictionary(a => a.Name, a => a.SqlType, StringComparer.OrdinalIgnoreCase);
        var after = current.Attributes.ToDictionary(a => a.Name, a => a.SqlType, StringComparer.OrdinalIgnoreCase);

        return new SchemaDiff(
            [.. current.Attributes.Where(a => !before.ContainsKey(a.Name)).Select(a => a.Name)],
            [.. previous.Attributes.Where(a => !after.ContainsKey(a.Name)).Select(a => a.Name)],
            [.. current.Attributes
                .Where(a => before.TryGetValue(a.Name, out var type) && !string.Equals(type, a.SqlType, StringComparison.OrdinalIgnoreCase))
                .Select(a => (a.Name, before[a.Name], a.SqlType))]);
    }
}
