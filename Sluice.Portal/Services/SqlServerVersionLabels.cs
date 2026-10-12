using Microsoft.Extensions.Localization;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

public static class SqlServerVersionLabels
{
    public static string Get(IStringLocalizer<SharedResource> localizer, SqlServerVersion version) =>
        version switch
        {
            SqlServerVersion.SqlServer2016 => localizer["Page.Destinations.SqlVersion.2016"].Value,
            SqlServerVersion.SqlServer2017 => localizer["Page.Destinations.SqlVersion.2017"].Value,
            SqlServerVersion.SqlServer2019 => localizer["Page.Destinations.SqlVersion.2019"].Value,
            SqlServerVersion.SqlServer2022 => localizer["Page.Destinations.SqlVersion.2022"].Value,
            _ => version.ToString(),
        };

    public static IEnumerable<(string Value, string Label)> Options(IStringLocalizer<SharedResource> localizer) =>
    [
        ("2022", Get(localizer, SqlServerVersion.SqlServer2022)),
        ("2019", Get(localizer, SqlServerVersion.SqlServer2019)),
        ("2017", Get(localizer, SqlServerVersion.SqlServer2017)),
        ("2016", Get(localizer, SqlServerVersion.SqlServer2016)),
    ];
}