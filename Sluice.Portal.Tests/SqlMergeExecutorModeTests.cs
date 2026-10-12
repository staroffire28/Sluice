using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Modos de MERGE contra SQL Server real (ver SqlFactAttribute): UpsertNewer para el delta de un
// Incremental (no pisa lo más nuevo ni reescribe lo igual) e InsertIntoEmpty para la primera carga.
public sealed class SqlMergeExecutorModeTests
{
    private const string Schema = "dbo";

    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-06-02T00:00:00Z");

    private static string ConnStr => SqlFactAttribute.RequireConnectionString();

    private static CdmEntitySchema TableSchema(string entity) => new()
    {
        EntityName = entity,
        Attributes =
        [
            new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
            new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(100)", IsNullable = true },
            new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)", IsNullable = true },
        ],
    };

    private static Dictionary<string, object?> Row(Guid id, string name, DateTimeOffset? wm) =>
        new() { ["Id"] = id, ["Name"] = name, ["SinkModifiedOn"] = wm };

    // Origen más nuevo → actualiza; igual → no reescribe; más viejo → no pisa; destino sin
    // watermark → actualiza; origen sin watermark sobre destino con watermark → no pisa; Id nuevo → inserta.
    [SqlFact]
    public async Task UpsertNewer_updates_only_rows_whose_source_watermark_is_newer()
    {
        var entity = "mm_newer_" + Guid.NewGuid().ToString("N")[..8];
        var schema = TableSchema(entity);
        Guid newer = Guid.NewGuid(), equal = Guid.NewGuid(), older = Guid.NewGuid(),
            targetNull = Guid.NewGuid(), sourceNull = Guid.NewGuid(), brandNew = Guid.NewGuid();
        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();
        var merge = new SqlMergeExecutor();
        try
        {
            await merge.EnsureTableAsync(conn, Schema, entity, schema, SqlServerVersion.SqlServer2022);
            await merge.MergeEntityAsync(conn, Schema, entity, schema,
                [Row(newer, "t", T1), Row(equal, "t", T1), Row(older, "t", T2), Row(targetNull, "t", null), Row(sourceNull, "t", T1)],
                SqlServerVersion.SqlServer2022, MergeMode.Upsert);

            await merge.MergeEntityAsync(conn, Schema, entity, schema,
                [Row(newer, "s", T2), Row(equal, "s", T1), Row(older, "s", T1), Row(targetNull, "s", T1), Row(sourceNull, "s", null), Row(brandNew, "s", T1)],
                SqlServerVersion.SqlServer2022, MergeMode.UpsertNewer);

            var names = await NamesAsync(conn, entity);
            Assert.Equal("s", names[newer]);
            Assert.Equal("t", names[equal]);
            Assert.Equal("t", names[older]);
            Assert.Equal("s", names[targetNull]);
            Assert.Equal("t", names[sourceNull]);
            Assert.Equal("s", names[brandNew]);
        }
        finally
        {
            await DropAsync(conn, entity);
        }
    }

    // Reaplicar el mismo delta (reintento) no cambia nada.
    [SqlFact]
    public async Task UpsertNewer_is_idempotent()
    {
        var entity = "mm_idem_" + Guid.NewGuid().ToString("N")[..8];
        var schema = TableSchema(entity);
        var rows = new List<Dictionary<string, object?>> { Row(Guid.NewGuid(), "a", T1), Row(Guid.NewGuid(), "b", T2) };
        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();
        var merge = new SqlMergeExecutor();
        try
        {
            await merge.EnsureTableAsync(conn, Schema, entity, schema, SqlServerVersion.SqlServer2022);
            await merge.MergeEntityAsync(conn, Schema, entity, schema, rows, SqlServerVersion.SqlServer2022, MergeMode.UpsertNewer);
            await merge.MergeEntityAsync(conn, Schema, entity, schema, rows, SqlServerVersion.SqlServer2022, MergeMode.UpsertNewer);

            Assert.Equal(2, await merge.CountRowsAsync(conn, Schema, entity, SqlServerVersion.SqlServer2022));
        }
        finally
        {
            await DropAsync(conn, entity);
        }
    }

    // Primera carga sobre la tabla vacía: inserción directa (sin staging ni MERGE), en varios
    // lotes y con el índice único ya creado.
    [SqlFact]
    public async Task InsertIntoEmpty_loads_every_row_in_several_batches()
    {
        var entity = "mm_ins_" + Guid.NewGuid().ToString("N")[..8];
        var schema = TableSchema(entity);
        await using var conn = new SqlConnection(ConnStr);
        await conn.OpenAsync();
        var merge = new SqlMergeExecutor();
        try
        {
            await merge.EnsureTableAsync(conn, Schema, entity, schema, SqlServerVersion.SqlServer2022);
            Assert.True(await merge.EnsureUniqueIdIndexAsync(conn, Schema, entity));
            for (var batch = 0; batch < 3; batch++)
            {
                var rows = Enumerable.Range(0, 1_000).Select(i => Row(Guid.NewGuid(), $"r{i}", T1)).ToList();
                Assert.Equal(1_000, await merge.MergeEntityAsync(conn, Schema, entity, schema, rows, SqlServerVersion.SqlServer2022, MergeMode.InsertIntoEmpty));
            }

            Assert.Equal(3_000, await merge.CountRowsAsync(conn, Schema, entity, SqlServerVersion.SqlServer2022));
        }
        finally
        {
            await DropAsync(conn, entity);
        }
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(SqlConnection conn, string entity)
    {
        var names = new Dictionary<Guid, string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [Id], [Name] FROM [{Schema}].[{entity}];";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names[reader.GetGuid(0)] = reader.GetString(1);
        }

        return names;
    }

    private static async Task DropAsync(SqlConnection conn, string entity)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP TABLE IF EXISTS [{Schema}].[{entity}];";
        await cmd.ExecuteNonQueryAsync();
    }
}
