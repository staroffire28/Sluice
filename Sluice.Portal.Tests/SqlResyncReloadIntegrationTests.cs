using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Prueba en vivo del re-sync en modo Reload contra un SQL Server real (no mocks): upsert del
// "parquet" + borrado de huérfanas. Verifica las dos direcciones de divergencia a la vez:
//  - source > dest: una fila nueva (C) que falta en SQL se inserta.
//  - dest > source: una fila huérfana (ORPHAN) que ya no está en el origen se borra.
//  - además, una fila existente (A) se actualiza (upsert).
// Necesita SQL Server (SLUICE_TEST_SQL, ver SqlFactAttribute): sin la variable sale Skipped en
// local; en CI se ejecuta contra el servicio mssql del workflow. Para correrlo en local:
//   docker run -d --name mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=... -p 14333:1433 \
//     mcr.microsoft.com/mssql/server:2022-latest
//   SLUICE_TEST_SQL="Server=127.0.0.1,14333;Database=master;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False" \
//     dotnet test --filter SqlResyncReloadIntegrationTests
public sealed class SqlResyncReloadIntegrationTests
{
    private static string ConnStr => SqlFactAttribute.RequireConnectionString();

    private const string Schema = "dbo";
    private const string Entity = "resync_it_test";

    private static readonly Guid IdA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid IdOrphan = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid IdC = new("33333333-3333-3333-3333-333333333333");

    private static CdmEntitySchema TableSchema() => new()
    {
        EntityName = Entity,
        Attributes =
        [
            new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
            new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(100)", IsNullable = true },
        ],
    };

    private static Dictionary<string, object?> Row(Guid id, string name) =>
        new() { ["Id"] = id, ["Name"] = name };

    [SqlFact]
    public async Task ReloadResync_UpsertsMissing_UpdatesExisting_AndDeletesOrphans()
    {
        var merge = new SqlMergeExecutor();
        var schema = TableSchema();

        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();

        await DropIfExistsAsync(conn);
        try
        {
            const SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022;
            await merge.EnsureTableAsync(conn, Schema, Entity, schema, sqlVersion);

            // Estado "viejo" en SQL: A (se actualizará) + ORPHAN (ya no está en el origen).
            await merge.MergeEntityAsync(conn, Schema, Entity, schema,
                [Row(IdA, "old-A"), Row(IdOrphan, "ghost")], sqlVersion);
            Assert.Equal(2, await merge.CountRowsAsync(conn, Schema, Entity, sqlVersion));

            // Origen actual (parquet consolidado): A (actualizado) + C (nuevo). ORPHAN ya no existe.
            var sourceRows = new List<Dictionary<string, object?>> { Row(IdA, "new-A"), Row(IdC, "added-C") };
            var sourceKeys = new List<object?> { IdA, IdC };

            // --- Re-sync Reload: upsert + borrado de huérfanas (lo que hace SqlExecutor) ---
            await merge.MergeEntityAsync(conn, Schema, Entity, schema, sourceRows, sqlVersion);
            // Tras el upsert: A actualizado, C insertado, ORPHAN aún presente (upsert no borra).
            Assert.Equal(3, await merge.CountRowsAsync(conn, Schema, Entity, sqlVersion));

            var deleted = await merge.DeleteOrphansAsync(conn, Schema, Entity, schema, sourceKeys, sqlVersion);

            // --- Verificación: SQL == origen exactamente ---
            Assert.Equal(1, deleted);                                              // solo ORPHAN borrado
            Assert.Equal(2, await merge.CountRowsAsync(conn, Schema, Entity, sqlVersion));     // SQL == parquet
            Assert.Equal("new-A", await NameOfAsync(conn, IdA));                   // upsert actualizó A
            Assert.True(await ExistsAsync(conn, IdC));                             // C insertado
            Assert.False(await ExistsAsync(conn, IdOrphan));                       // huérfana eliminada
        }
        finally
        {
            await DropIfExistsAsync(conn);
        }
    }

    private static async Task DropIfExistsAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF OBJECT_ID('[{Schema}].[{Entity}]','U') IS NOT NULL DROP TABLE [{Schema}].[{Entity}];";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ExistsAsync(SqlConnection conn, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(1) FROM [{Schema}].[{Entity}] WHERE [Id] = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<string?> NameOfAsync(SqlConnection conn, Guid id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [Name] FROM [{Schema}].[{Entity}] WHERE [Id] = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return (await cmd.ExecuteScalarAsync()) as string;
    }
}
