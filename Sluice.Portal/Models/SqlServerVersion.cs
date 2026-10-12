namespace Sluice.Portal.Models;

public enum SqlServerVersion
{
    SqlServer2016 = 2016,
    SqlServer2017 = 2017,
    SqlServer2019 = 2019,
    SqlServer2022 = 2022,
}

public static class SqlServerVersions
{
    public const string ConfigKey = "sql_version";

    public static SqlServerVersion Default => SqlServerVersion.SqlServer2022;

    public static SqlServerVersion Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        return value.Trim() switch
        {
            "2016" => SqlServerVersion.SqlServer2016,
            "2017" => SqlServerVersion.SqlServer2017,
            "2019" => SqlServerVersion.SqlServer2019,
            "2022" => SqlServerVersion.SqlServer2022,
            _ when Enum.TryParse<SqlServerVersion>(value, ignoreCase: true, out var parsed) => parsed,
            _ => Default,
        };
    }

    public static int ToMajorVersion(SqlServerVersion version) =>
        version switch
        {
            SqlServerVersion.SqlServer2016 => 13,
            SqlServerVersion.SqlServer2017 => 14,
            SqlServerVersion.SqlServer2019 => 15,
            SqlServerVersion.SqlServer2022 => 16,
            _ => 16,
        };

    public static SqlServerVersion FromMajorVersion(int major) =>
        major switch
        {
            >= 16 => SqlServerVersion.SqlServer2022,
            15 => SqlServerVersion.SqlServer2019,
            14 => SqlServerVersion.SqlServer2017,
            13 => SqlServerVersion.SqlServer2016,
            _ => SqlServerVersion.SqlServer2016,
        };

    public static string DisplayName(SqlServerVersion version) =>
        version switch
        {
            SqlServerVersion.SqlServer2016 => "SQL Server 2016",
            SqlServerVersion.SqlServer2017 => "SQL Server 2017",
            SqlServerVersion.SqlServer2019 => "SQL Server 2019",
            SqlServerVersion.SqlServer2022 => "SQL Server 2022",
            _ => version.ToString(),
        };

    /// <summary>
    /// El servidor detectado debe ser igual o más nuevo que la versión configurada.
    /// </summary>
    public static bool IsServerCompatible(SqlServerVersion configured, SqlServerVersion detected) =>
        ToMajorVersion(detected) >= ToMajorVersion(configured);
}