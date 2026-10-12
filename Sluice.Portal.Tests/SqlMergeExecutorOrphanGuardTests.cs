using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// DeleteOrphansAsync con 0 claves de origen borraba TODAS las filas de la tabla destino
// (DELETE ... WHERE NOT EXISTS contra una #keys vacía), p. ej. en un Reload cuyo parquet
// consolidado salió vacío. Con la guarda no se toca SQL: por eso basta una conexión nula.
public sealed class SqlMergeExecutorOrphanGuardTests
{
    [Fact]
    public async Task DeleteOrphansAsync_with_no_source_keys_deletes_nothing_and_never_touches_sql()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "account",
            Attributes = [new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false }],
        };

        var removed = await new SqlMergeExecutor().DeleteOrphansAsync(
            null!, "dbo", "account", schema, [], SqlServerVersion.SqlServer2022);

        Assert.Equal(0, removed);
    }
}
