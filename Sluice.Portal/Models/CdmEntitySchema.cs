namespace Sluice.Portal.Models;

public sealed record CdmEntitySchema
{
    public required string EntityName { get; init; }
    public List<CdmAttribute> Attributes { get; init; } = [];
}

public sealed record CdmAttribute
{
    public required string Name { get; init; }
    public required string SqlType { get; init; }
    public bool IsNullable { get; init; } = true;

    // Columnas derivadas de traducción de enum ([<col>_$label]). Cuando EnumSourceColumn no es
    // null, esta columna no existe en el parquet: su valor se resuelve en el staging a partir del
    // valor crudo de EnumSourceColumn usando EnumMap (Option -> ExternalValue). Vive en memoria; el
    // motor de merge la reinyecta en runtime, no se persiste en el schema guardado en ADLS.
    public string? EnumSourceColumn { get; init; }
    public IReadOnlyDictionary<long, string>? EnumMap { get; init; }
}