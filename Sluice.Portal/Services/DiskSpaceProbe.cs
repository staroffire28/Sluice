namespace Sluice.Portal.Services;

// Espacio del volumen donde está DATA_DIR. Bajo: menos del 10 % del volumen o menos de 10 GB, el
// menor de los dos umbrales (en 2 TB avisa con 10 GB libres; en 50 GB, con 5 GB).
public sealed record DiskSpace(long FreeBytes, long TotalBytes)
{
    public const long Gigabyte = 1024L * 1024 * 1024;
    public const long LowAbsoluteBytes = 10 * Gigabyte;
    public const int LowPercent = 10;

    public long LowThresholdBytes => Math.Min(TotalBytes * LowPercent / 100, LowAbsoluteBytes);

    public bool IsLow => FreeBytes < LowThresholdBytes;

    public double FreePercent => TotalBytes > 0 ? FreeBytes * 100.0 / TotalBytes : 0;
}

// Sustituible en los tests: el disco real de la máquina no debe decidir el resultado.
public interface IDiskSpaceProbe
{
    // Null si no se puede determinar (ruta sin unidad, error al leerla): estado desconocido.
    DiskSpace? Get(string path);
}

public sealed class DriveInfoDiskSpaceProbe : IDiskSpaceProbe
{
    public DiskSpace? Get(string path)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var root = FindMountRoot(
                Path.GetFullPath(path),
                DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName),
                comparison,
                Path.DirectorySeparatorChar);
            if (root is null)
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.TotalSize > 0 ? new DiskSpace(drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // En Linux cada punto de montaje es una "unidad": vale el de prefijo más largo que contenga la
    // ruta, cortando por separador (/data no contiene /database).
    internal static string? FindMountRoot(string fullPath, IEnumerable<string> roots, StringComparison comparison, char separator)
    {
        string? best = null;
        foreach (var root in roots)
        {
            var contains = root.EndsWith(separator)
                ? fullPath.StartsWith(root, comparison)
                : fullPath.Equals(root, comparison) || fullPath.StartsWith(root + separator, comparison);
            if (contains && (best is null || root.Length > best.Length))
            {
                best = root;
            }
        }

        return best;
    }
}
