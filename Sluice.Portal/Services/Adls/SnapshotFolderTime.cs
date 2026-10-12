using System.Globalization;

namespace Sluice.Portal.Services.Adls;

public static class SnapshotFolderTime
{
    // Synapse Link nombra carpetas ISO con ':' sustituido por '.' → YYYY-MM-DDTHH.MM.SSZ
    public static bool TryParse(string folderName, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!ExcludedEntities.IsSnapshotFolder(folderName))
        {
            return false;
        }

        var tIndex = folderName.IndexOf('T', StringComparison.Ordinal);
        if (tIndex < 0)
        {
            return false;
        }

        var datePart = folderName[..tIndex];
        var timePart = folderName[(tIndex + 1)..].TrimEnd('Z');
        var normalized = $"{datePart}T{timePart.Replace('.', ':')}Z";

        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }
}