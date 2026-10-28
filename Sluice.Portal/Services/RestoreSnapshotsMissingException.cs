namespace Sluice.Portal.Services;

// Restaurar un punto rebobina el estado y vuelve a leer del lago los snapshots posteriores. Si la
// retención de origen ya borró alguno, sus cambios se perderían sin aviso, así que se rechaza. Lleva
// el punto y el snapshot más antiguo que queda para que la UI lo explique en su idioma.
public sealed class RestoreSnapshotsMissingException(string snapshot, string? oldestInLake)
    : InvalidOperationException(
        $"No se puede restaurar el punto {snapshot}: el lago ya no conserva todos los snapshots posteriores " +
        $"(el más antiguo que queda es {oldestInLake ?? "ninguno"}).")
{
    public string Snapshot { get; } = snapshot;
    public string? OldestInLake { get; } = oldestInLake;
}
