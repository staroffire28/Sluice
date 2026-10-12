using Sluice.Portal.Models;

namespace Sluice.Portal.Tests;

public sealed class SqlServerVersionsTests
{
    [Theory]
    [InlineData("2017", SqlServerVersion.SqlServer2017)]
    [InlineData("2022", SqlServerVersion.SqlServer2022)]
    [InlineData(null, SqlServerVersion.SqlServer2022)]
    [InlineData("", SqlServerVersion.SqlServer2022)]
    public void Parse_ReadsConfigValue(string? raw, SqlServerVersion expected) =>
        Assert.Equal(expected, SqlServerVersions.Parse(raw));

    [Theory]
    [InlineData(14, SqlServerVersion.SqlServer2017)]
    [InlineData(16, SqlServerVersion.SqlServer2022)]
    public void FromMajorVersion_MapsProductMajor(int major, SqlServerVersion expected) =>
        Assert.Equal(expected, SqlServerVersions.FromMajorVersion(major));

    [Theory]
    [InlineData(SqlServerVersion.SqlServer2017, SqlServerVersion.SqlServer2017, true)]
    [InlineData(SqlServerVersion.SqlServer2017, SqlServerVersion.SqlServer2022, true)]
    [InlineData(SqlServerVersion.SqlServer2022, SqlServerVersion.SqlServer2017, false)]
    public void IsServerCompatible_RequiresEqualOrNewerServer(
        SqlServerVersion configured,
        SqlServerVersion detected,
        bool expected) =>
        Assert.Equal(expected, SqlServerVersions.IsServerCompatible(configured, detected));

    [Theory]
    [InlineData(SqlServerVersion.SqlServer2017, "SQL Server 2017")]
    [InlineData(SqlServerVersion.SqlServer2022, "SQL Server 2022")]
    public void DisplayName_ReturnsHumanLabel(SqlServerVersion version, string expected) =>
        Assert.Equal(expected, SqlServerVersions.DisplayName(version));
}