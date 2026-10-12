namespace Sluice.Portal.Services.Adls;

public static class SnapshotSelector
{
    public sealed record SelectionResult(
        string? LastSettled,
        IReadOnlyList<string> ToProcess,
        IReadOnlyList<string> Unclosed,
        bool Blocked,
        string? BlockReason);

    // Decide qué snapshots son seguros de procesar.
    //
    // CONTRATO de Azure Synapse Link (incremental folder updates): el archivo
    // Changelog/changelog.info nombra la carpeta que el servicio está ESCRIBIENDO
    // en este momento ("the folder name that is currently updated"). Microsoft indica
    // consumir SOLO las carpetas anteriores a esa, para no leer datos a medio escribir.
    // Por eso `currentFolder` (la del changelog) y todo lo más nuevo se consideran NO
    // consumibles (Unclosed); únicamente lo estrictamente anterior es procesable.
    // Ref: https://learn.microsoft.com/power-apps/maker/data-platform/azure-synapse-incremental-updates#consume-incremental-data
    public static SelectionResult Select(
        IReadOnlyList<string> allSnapshots,
        string? lastProcessed,
        string? currentFolder)
    {
        var snapshots = allSnapshots
            .Where(ExcludedEntities.IsSnapshotFolder)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        if (string.IsNullOrWhiteSpace(currentFolder))
        {
            return new SelectionResult(null, [], [], true, "Changelog empty or missing — export in progress.");
        }

        if (!snapshots.Contains(currentFolder))
        {
            return new SelectionResult(null, [], [], true, $"Changelog folder '{currentFolder}' not found.");
        }

        var currentIndex = snapshots.IndexOf(currentFolder);

        // Seguras: estrictamente anteriores a la carpeta en escritura.
        var safe = snapshots.Take(currentIndex).ToList();
        // No consumibles: la carpeta en escritura (currentFolder) y las más nuevas.
        var unclosed = snapshots.Skip(currentIndex).ToList();
        // Última carpeta realmente cerrada: la inmediatamente anterior a la actual.
        var lastSettled = currentIndex > 0 ? snapshots[currentIndex - 1] : null;

        var toProcess = string.IsNullOrWhiteSpace(lastProcessed)
            ? safe
            : safe.Where(s => string.CompareOrdinal(s, lastProcessed) > 0).ToList();

        return new SelectionResult(lastSettled, toProcess, unclosed, false, null);
    }
}