using Microsoft.Extensions.Localization;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class JobStatusLabels
{
    public static string Get(IStringLocalizer<SharedResource> localizer, JobStatus status) =>
        status switch
        {
            JobStatus.Running => localizer["Common.JobStatus.Running"].Value,
            JobStatus.Ok => localizer["Common.JobStatus.Ok"].Value,
            JobStatus.Partial => localizer["Common.JobStatus.Partial"].Value,
            JobStatus.Error => localizer["Common.JobStatus.Error"].Value,
            JobStatus.Blocked => localizer["Common.JobStatus.Blocked"].Value,
            JobStatus.Cancelled => localizer["Common.JobStatus.Cancelled"].Value,
            _ => status.ToString(),
        };
}