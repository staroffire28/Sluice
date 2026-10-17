using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class JobEntityResultMerger
{
    public const string WarningMarker = " — ADVERTENCIA:";

    public static List<JobEntityResult> Merge(IEnumerable<JobEntityResult> existing, IEnumerable<JobEntityResult> updates)
    {
        var map = new Dictionary<string, JobEntityResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing)
        {
            map[item.Entity] = item;
        }

        foreach (var item in updates)
        {
            map[item.Entity] = map.TryGetValue(item.Entity, out var previous) ? KeepWarnings(previous, item) : item;
        }

        return map.Values.ToList();
    }

    private static JobEntityResult KeepWarnings(JobEntityResult previous, JobEntityResult update)
    {
        var start = previous.Message?.IndexOf(WarningMarker, StringComparison.Ordinal) ?? -1;
        if (start < 0 || update.Message?.Contains(WarningMarker, StringComparison.Ordinal) == true)
        {
            return update;
        }

        return update with { Message = update.Message + previous.Message![start..] };
    }
}