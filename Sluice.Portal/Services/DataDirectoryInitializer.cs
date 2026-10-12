using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class DataDirectoryInitializer
{
    public static void EnsureLayout(string dataDir)
    {
        var directories = new[]
        {
            dataDir,
            Path.Combine(dataDir, "connections"),
            Path.Combine(dataDir, "pipelines"),
            Path.Combine(dataDir, "jobs"),
            Path.Combine(dataDir, "reports", "reconcile"),
            Path.Combine(dataDir, "reports", "diff"),
        };

        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
        }

        foreach (PipelineStage stage in Enum.GetValues<PipelineStage>())
        {
            foreach (PipelineTaskStatus status in Enum.GetValues<PipelineTaskStatus>())
            {
                Directory.CreateDirectory(Path.Combine(dataDir, "tasks", stage.ToString().ToLowerInvariant(), status.ToString().ToLowerInvariant()));
            }
        }
    }

    public static void EnsureLayout(AppOptions options) => EnsureLayout(options.DataDir);
}