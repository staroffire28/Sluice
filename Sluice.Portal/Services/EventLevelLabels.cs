using Microsoft.Extensions.Localization;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class EventLevelLabels
{
    public static string Get(IStringLocalizer<SharedResource> localizer, EventLevel level) =>
        level switch
        {
            EventLevel.Info => localizer["Common.EventLevel.Info"].Value,
            EventLevel.Warning => localizer["Common.EventLevel.Warning"].Value,
            EventLevel.Error => localizer["Common.EventLevel.Error"].Value,
            _ => level.ToString(),
        };
}