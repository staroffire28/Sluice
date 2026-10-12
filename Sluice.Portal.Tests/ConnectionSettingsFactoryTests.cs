using Microsoft.Data.SqlClient;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Tests;

public class ConnectionSettingsFactoryTests
{
    [Fact]
    public void ResolveAdls_ReadsAllFieldsFromConnectionConfig()
    {
        var connection = new Connection
        {
            Id = "src",
            Name = "ADLS",
            Role = ConnectionRole.Source,
            Type = "adls",
            Config = new Dictionary<string, string?>
            {
                ["account"] = "acct",
                ["container"] = "exports",
                ["parquet_container"] = "parquet-out",
                ["tenant_id"] = "tenant",
                ["client_id"] = "client",
                ["client_secret"] = "secret",
            },
        };

        var settings = ConnectionSettingsFactory.ResolveAdls(connection);
        Assert.Equal("acct", settings.Account);
        Assert.Equal("exports", settings.Container);
        Assert.Equal("parquet-out", settings.ParquetContainer);
    }

    [Fact]
    public void ResolveAdls_ThrowsWhenRequiredFieldMissing()
    {
        var connection = new Connection
        {
            Id = "src",
            Name = "ADLS",
            Role = ConnectionRole.Source,
            Type = "adls",
            Config = new Dictionary<string, string?> { ["container"] = "exports" },
        };

        Assert.Throws<InvalidOperationException>(
            () => ConnectionSettingsFactory.ResolveAdls(connection));
    }

    [Fact]
    public void ResolveSql_BuildsConnectionStringFromParts()
    {
        var connection = new Connection
        {
            Id = "dst",
            Name = "SQL",
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
            Config = new Dictionary<string, string?>
            {
                ["host"] = "sql.local",
                ["database"] = "SynapseLink",
                ["user"] = "sa",
                ["password"] = "p@ss",
            },
        };

        var settings = ConnectionSettingsFactory.ResolveSql(connection);
        // Se interpreta la cadena en vez de buscar texto: SqlConnectionStringBuilder la emite
        // con sus palabras clave canónicas ("Data Source", "Initial Catalog").
        var parsed = new SqlConnectionStringBuilder(settings.ConnectionString);
        Assert.Equal("sql.local", parsed.DataSource);
        Assert.Equal("SynapseLink", parsed.InitialCatalog);
        Assert.Equal(SqlServerVersion.SqlServer2022, settings.Version);
    }

    [Fact]
    public void ResolveSql_ReadsSqlVersionFromConfig()
    {
        var connection = new Connection
        {
            Id = "dst",
            Name = "SQL",
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
            Config = new Dictionary<string, string?>
            {
                ["host"] = "sql.local",
                ["database"] = "SynapseLink",
                ["user"] = "sa",
                ["password"] = "p@ss",
                [SqlServerVersions.ConfigKey] = "2017",
            },
        };

        var settings = ConnectionSettingsFactory.ResolveSql(connection);
        Assert.Equal(SqlServerVersion.SqlServer2017, settings.Version);
    }

    // La connection string se construía interpolando los valores: un ';' o '=' en la contraseña
    // (o en el host) añadía palabras clave nuevas a la cadena.
    [Fact]
    public void ResolveSql_keeps_special_characters_inside_their_own_values()
    {
        var settings = ConnectionSettingsFactory.ResolveSql(new Connection
        {
            Id = "conn-dst-test",
            Name = "Dst",
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
            Config = new Dictionary<string, string?>
            {
                ["host"] = "sql.example.local",
                ["database"] = "SynapseLink",
                ["user"] = "loader",
                ["password"] = "Pa55;Database=master;Integrated Security=true",
            },
        });

        var parsed = new SqlConnectionStringBuilder(settings.ConnectionString);
        Assert.Equal("Pa55;Database=master;Integrated Security=true", parsed.Password);
        Assert.Equal("SynapseLink", parsed.InitialCatalog);
        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal("sql.example.local", parsed.DataSource);
        Assert.Equal("loader", parsed.UserID);
    }
}
