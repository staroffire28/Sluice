using Microsoft.Extensions.Localization;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class PipelineStageLabels
{
    public static string Get(IStringLocalizer<SharedResource> localizer, PipelineStage? stage) =>
        stage switch
        {
            PipelineStage.Ingest => localizer["Common.Stage.Ingest"].Value,
            PipelineStage.Sql => localizer["Common.Stage.Sql"].Value,
            PipelineStage.Reconcile => localizer["Common.Stage.Reconcile"].Value,
            null => "—",
            _ => stage.ToString()!,
        };

    public static string GetStageKey(IStringLocalizer<SharedResource> localizer, string stageKey) =>
        stageKey.ToLowerInvariant() switch
        {
            "ingest" => localizer["Common.Stage.Ingest"].Value,
            "sql" => localizer["Common.Stage.Sql"].Value,
            "reconcile" => localizer["Common.Stage.Reconcile"].Value,
            _ => stageKey,
        };
}