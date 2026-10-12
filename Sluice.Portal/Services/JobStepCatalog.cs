namespace Sluice.Portal.Services;

public enum StepUnit
{
    None,
    Snapshots,
    Entities,
}

public sealed record JobStepDef(string Key, string Stage, StepUnit Unit, int Weight);

// Pasos reales de un job, en orden de pantalla. PipelineStage sigue con 3 valores (ingest/sql/
// reconcile): los pasos son datos dentro de cada etapa. Solo se listan pasos que ocurren uno
// detrás de otro y se pueden cronometrar; lo que se intercala por entidad en un mismo bucle
// (crear tabla, MERGE, huérfanas, conteo) es la fase en curso del paso "merge".
// Weight: peso relativo dentro de su etapa para el porcentaje (heurístico, ajustable).
public static class JobStepCatalog
{
    public const string Discover = "discover";
    public const string Snapshots = "snapshots";
    public const string Consolidate = "consolidate";
    public const string Register = "register";
    public const string Prepare = "prepare";
    public const string Merge = "merge";
    public const string Compare = "compare";
    public const string Report = "report";

    public static IReadOnlyList<JobStepDef> All { get; } =
    [
        new(Discover, "ingest", StepUnit.None, 2),
        new(Snapshots, "ingest", StepUnit.Snapshots, 78),
        new(Consolidate, "ingest", StepUnit.Entities, 15),
        new(Register, "ingest", StepUnit.None, 5),
        new(Prepare, "sql", StepUnit.None, 5),
        new(Merge, "sql", StepUnit.Entities, 95),
        new(Compare, "reconcile", StepUnit.Entities, 90),
        new(Report, "reconcile", StepUnit.None, 10),
    ];

    public static IEnumerable<JobStepDef> ForStage(string stage) =>
        All.Where(s => string.Equals(s.Stage, stage, StringComparison.Ordinal));

    public static JobStepDef? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
}
