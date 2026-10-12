namespace Sluice.Portal.Services;

// Textos de eventos que, además de leerse en la cronología, sirven para reconstruir el flujo
// de jobs antiguos (sin StageTimings ni progress.json): JobFlowBuilder los busca literalmente.
// Cambiarlos rompe esa reconstrucción para los jobs ya guardados.
public static class JobEventMarkers
{
    public const string IngestStarted = "Ingest stage started.";
    public const string SqlStarted = "SQL MERGE stage started.";
    public const string ReconcileStarted = "Reconcile stage started.";
    public const string NoNewSnapshots = "No new snapshots to process.";

    // Prefijos de los eventos de cierre de cada etapa.
    public const string IngestCompletedPrefix = "Ingest completed:";
    public const string IngestStoppedEarlyPrefix = "Ingest stopped early after";
    public const string SqlCompletedPrefix = "SQL MERGE completed";
    public const string SqlNoManifest = "No ingest manifest — skipping SQL stage.";
    public const string ReconcileCompletedPrefix = "Reconcile completed.";

    // Evento de creación de un Re-sync: "Re-sync de tabla {entity} ({mode})."
    public const string ResyncTablePrefix = "Re-sync de tabla ";
}
