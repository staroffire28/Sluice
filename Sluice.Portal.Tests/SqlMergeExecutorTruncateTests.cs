using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Prueba en vivo de TruncateTableAsync (reset de pipeline para pruebas) contra SQL Server real.
// Necesita SQL Server (SLUICE_TEST_SQL, ver SqlFactAttribute): sin la variable sale Skipped en
// local; en CI se ejecuta contra el servicio mssql del workflow.
public sealed class SqlMergeExecutorTruncateTests
{
    private static string ConnStr => SqlFactAttribute.RequireConnectionString();

    private const string Schema = "dbo";
    private const string Entity = "truncate_it_test";

    private static CdmEntitySchema TableSchema() => new()
    {
        EntityName = Entity,
        Attributes =
        [
            new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
            new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(100)", IsNullable = true },
        ],
    };

    [SqlFact]
    public async Task TruncateTableAsync_ExistingTable_EmptiesItAndReturnsTrue()
    {

        var merge = new SqlMergeExecutor();
        var schema = TableSchema();
        const SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022;

        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();

        await DropIfExistsAsync(conn);
        try
        {
            await merge.EnsureTableAsync(conn, Schema, Entity, schema, sqlVersion);
            await merge.MergeEntityAsync(conn, Schema, Entity, schema,
                [new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["Name"] = "old data" }], sqlVersion);
            Assert.Equal(1, await merge.CountRowsAsync(conn, Schema, Entity, sqlVersion));

            var truncated = await merge.TruncateTableAsync(conn, Schema, Entity);

            Assert.True(truncated);
            Assert.Equal(0, await merge.CountRowsAsync(conn, Schema, Entity, sqlVersion));
        }
        finally
        {
            await DropIfExistsAsync(conn);
        }
    }

    [SqlFact]
    public async Task TruncateTableAsync_MissingTable_ReturnsFalseWithoutThrowing()
    {

        var merge = new SqlMergeExecutor();

        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();

        await DropIfExistsAsync(conn);

        var truncated = await merge.TruncateTableAsync(conn, Schema, Entity);

        Assert.False(truncated);
    }

    private static async Task DropIfExistsAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF OBJECT_ID('[{Schema}].[{Entity}]','U') IS NOT NULL DROP TABLE [{Schema}].[{Entity}];";
        await cmd.ExecuteNonQueryAsync();
    }
}
