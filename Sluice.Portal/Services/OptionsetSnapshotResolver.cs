using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class OptionsetSnapshotResolver
{
    public static string? Resolve(IngestManifest manifest, string? pipelineLastProcessed, string? settledSnapshot)
    {
        var fromEntities = manifest.Entities
            .Select(e => e.Snapshot)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderByDescending(s => s!, StringComparer.Ordinal)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(fromEntities))
        {
            return fromEntities;
        }

        if (!string.IsNullOrWhiteSpace(manifest.LastProcessedSnapshot))
        {
            return manifest.LastProcessedSnapshot;
        }

        if (!string.IsNullOrWhiteSpace(pipelineLastProcessed))
        {
            return pipelineLastProcessed;
        }

        return string.IsNullOrWhiteSpace(settledSnapshot) ? null : settledSnapshot;
    }
}