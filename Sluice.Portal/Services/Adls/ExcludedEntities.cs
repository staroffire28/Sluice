namespace Sluice.Portal.Services.Adls;

public static class ExcludedEntities
{
    private static readonly HashSet<string> Fixed = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptionsetMetadata",
        "Changelog",
        "EntitySyncFailure",
        "Microsoft.Athena.TrickleFeedService",
    };

    public static bool IsExcluded(string name) =>
        name.StartsWith('_') || Fixed.Contains(name);

    public static bool IsSnapshotFolder(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{4}-\d{2}-\d{2}");
}