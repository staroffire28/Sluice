using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class JobEntityResultMerger
{
    public static List<JobEntityResult> Merge(IEnumerable<JobEntityResult> existing, IEnumerable<JobEntityResult> updates)
    {
        var map = new Dictionary<string, JobEntityResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing)
        {
            map[item.Entity] = item;
        }

        foreach (var item in updates)
        {
            map[item.Entity] = item;
        }

        return map.Values.ToList();
    }
}