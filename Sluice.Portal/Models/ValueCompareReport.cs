namespace Sluice.Portal.Models;

// Comparación de valores (bajo demanda): hash por fila del parquet consolidado frente a la tabla
// SQL, emparejadas por Id. Reporte propio (reports/values): no sustituye a la conciliación de conteos.
public sealed record ValueCompareReport
{
    public required string Id { get; init; }
    public required string PipelineId { get; init; }
    public required string JobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<ValueCompareEntityResult> Entities { get; init; } = [];

    public bool HasDifferences => Entities.Any(e => e.HasDifferences);
}

public sealed record ValueCompareEntityResult
{
    public required string Entity { get; init; }
    public long SourceRows { get; init; }
    public long DestinationRows { get; init; }
    public long Matching { get; init; }

    // Mismo Id con algún valor distinto.
    public long Different { get; init; }

    // Id del parquet que no está en SQL.
    public long MissingInDestination { get; init; }

    // Id de SQL que no está en el parquet (o repetido en SQL).
    public long ExtraInDestination { get; init; }

    // Muestras de Id para investigar (acotadas).
    public List<string> SampleDifferent { get; init; } = [];
    public List<string> SampleMissing { get; init; } = [];
    public List<string> SampleExtra { get; init; } = [];

    public int ComparedColumns { get; init; }

    // La tabla no se pudo comparar (p. ej. falta el parquet o la tabla SQL).
    public string? Error { get; init; }

    public bool HasDifferences => Error is null && Different + MissingInDestination + ExtraInDestination > 0;
}
