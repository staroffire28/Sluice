using Microsoft.Extensions.Localization;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class JobTypeLabels
{
    public static string Get(IStringLocalizer<SharedResource> localizer, JobType type) =>
        type switch
        {
            JobType.Incremental => localizer["Common.JobType.Incremental"].Value,
            JobType.Force => localizer["Common.JobType.Force"].Value,
            JobType.Reconcile => localizer["Common.JobType.Reconcile"].Value,
            JobType.Diff => localizer["Common.JobType.Diff"].Value,
            JobType.ResyncTable => localizer["Common.JobType.ResyncTable"].Value,
            JobType.ReloadFromParquet => localizer["Common.JobType.ReloadFromParquet"].Value,
            JobType.CompareValues => localizer["Common.JobType.CompareValues"].Value,
            _ => type.ToString(),
        };
}