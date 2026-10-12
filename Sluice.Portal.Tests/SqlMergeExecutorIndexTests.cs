using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Índice único no agrupado sobre [Id] en las tablas destino: sin él cada MERGE recorría la tabla
// entera y nada impedía Ids duplicados. Necesita SQL Server (ver SqlFactAttribute).
public sealed class SqlMergeExecutorIndexTests
{
    private const string Schema = "dbo";

    private static string ConnStr => SqlFactAttribute.RequireConnectionString();

    private static CdmEntitySchema TableSchema(string entity) => new()
    {
        EntityName = entity,
        Attributes =
        [
            new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
            new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(100)", IsNullable = true },
        ],
    };

    [SqlFact]
    public async Task Creates_the_index_when_there_are_no_duplicates_and_is_idempotent()
    {
        var entity = "ix_ok_" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();
        var merge = new SqlMergeExecutor();
        try
        {
            await merge.EnsureTableAsync(conn, Schema, entity, TableSchema(entity), SqlServerVersion.SqlServer2022);

            Assert.True(await merge.EnsureUniqueIdIndexAsync(conn, Schema, entity));
            Assert.True(await merge.EnsureUniqueIdIndexAsync(conn, Schema, entity)); // segunda vez: ya existe
            Assert.True(await IndexExistsAsync(conn, entity));
        }
        finally
        {
            await DropAsync(conn, entity);
        }
    }

    [SqlFact]
    public async Task Does_not_create_the_index_when_ids_are_duplicated()
    {
        var entity = "ix_dup_" + Guid.NewGuid().ToString("N")[..8];
        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();
        var merge = new SqlMergeExecutor();
        try
        {
            await merge.EnsureTableAsync(conn, Schema, entity, TableSchema(entity), SqlServerVersion.SqlServer2022);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"INSERT INTO [{Schema}].[{entity}] ([Id], [Name]) VALUES (@id, 'a'), (@id, 'b');";
                cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync();
            }

            Assert.False(await merge.EnsureUniqueIdIndexAsync(conn, Schema, entity));
            Assert.False(await IndexExistsAsync(conn, entity));
        }
        finally
        {
            await DropAsync(conn, entity);
        }
    }

    private static async Task<bool> IndexExistsAsync(SqlConnection conn, string entity)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sys.indexes WHERE object_id = OBJECT_ID(@table) AND name = 'UX_Sluice_Id';";
        cmd.Parameters.AddWithValue("@table", $"[{Schema}].[{entity}]");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task DropAsync(SqlConnection conn, string entity)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS [{Schema}].[{entity}];";
        await cmd.ExecuteNonQueryAsync();
    }
}
