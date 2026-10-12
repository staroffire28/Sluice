using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

public class SqlMergeBuilderTests
{
    private static CdmEntitySchema Schema() => new()
    {
        EntityName = "account",
        Attributes =
        [
            new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
            new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(100)" },
            new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
        ],
    };

    [Fact]
    public void BuildCreateTableIfNotExists_ContainsIdColumn()
    {
        var sql = SqlMergeBuilder.BuildCreateTableIfNotExists("dbo", "account", Schema());
        Assert.Contains("CREATE TABLE", sql);
        Assert.Contains("[Id] UNIQUEIDENTIFIER NOT NULL", sql);
    }

    [Fact]
    public void BuildMerge_UsesIdAsKey_NoDelete()
    {
        var sql = SqlMergeBuilder.BuildMerge("dbo", "account", "#stg_account_1", Schema());
        Assert.Contains("ON target.[Id] = source.[Id]", sql);
        Assert.Contains("WHEN MATCHED THEN UPDATE", sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", sql);
        Assert.DoesNotContain("DELETE", sql);
    }

    // Incremental con delta: solo se actualiza si el origen es más nuevo (">": el mismo
    // SinkModifiedOn no reescribe). Destino sin watermark: se actualiza (como en el consolidador,
    // sin watermark pierde frente a cualquiera).
    [Fact]
    public void BuildMerge_OnlyNewer_UpdatesOnlyWhenTheSourceIsNewer()
    {
        var sql = SqlMergeBuilder.BuildMerge("dbo", "account", "#stg_1", Schema(), onlyNewer: true);

        Assert.Contains(
            "WHEN MATCHED AND (target.[SinkModifiedOn] IS NULL OR source.[SinkModifiedOn] > target.[SinkModifiedOn]) THEN UPDATE",
            sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", sql);
    }

    // Sin columna de watermark no hay con qué comparar: MERGE incondicional de siempre.
    [Fact]
    public void BuildMerge_OnlyNewer_WithoutWatermarkColumn_IsUnconditional()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "plain",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(100)" },
            ],
        };

        var sql = SqlMergeBuilder.BuildMerge("dbo", "plain", "#stg_1", schema, onlyNewer: true);

        Assert.Contains("WHEN MATCHED THEN UPDATE", sql);
        Assert.DoesNotContain("SinkModifiedOn", sql);
    }

    [Fact]
    public void BuildAlterStringToMax_OnlyExpandsStrings()
    {
        var sql = SqlMergeBuilder.BuildAlterStringToMax("dbo", "account", "Name");
        Assert.Contains("ALTER COLUMN [Name] NVARCHAR(MAX)", sql);
    }

    [Fact]
    public void BuildAlterAddColumn_AddsNullableLabelColumn()
    {
        var attr = new CdmAttribute { Name = "statuscode_$label", SqlType = "NVARCHAR(MAX)", IsNullable = true };
        var sql = SqlMergeBuilder.BuildAlterAddColumn("dbo", "account", attr);
        Assert.Equal("ALTER TABLE [dbo].[account] ADD [statuscode_$label] NVARCHAR(MAX) NULL;", sql);
    }

    [Fact]
    public void BuildDeleteOrphans_RemovesRowsAbsentFromKeySet()
    {
        // El re-sync en modo Reload borra las filas cuyo Id no está en el parquet (huérfanas),
        // contra una tabla temporal de claves, sin vaciar la tabla.
        var sql = SqlMergeBuilder.BuildDeleteOrphans("dbo", "account", "#keys_account_1");
        Assert.Contains("DELETE t FROM [dbo].[account] t", sql);
        Assert.Contains("WHERE NOT EXISTS", sql);
        Assert.Contains("#keys_account_1 k WHERE k.[Id] = t.[Id]", sql);
    }

    [Fact]
    public void BuildKeyTable_DeclaresIdColumnWithSourceType()
    {
        var sql = SqlMergeBuilder.BuildKeyTable("#keys_account_1", "UNIQUEIDENTIFIER");
        Assert.Equal("CREATE TABLE #keys_account_1 ([Id] UNIQUEIDENTIFIER NOT NULL);", sql);
    }

    // Los nombres de entidad (carpetas de ADLS), columnas (model.json) y schema (config de la
    // conexión) se interpolaban como [{name}] sin escapar: una ']' cerraba el identificador y
    // permitía inyectar T-SQL. Se escapan como QUOTENAME (']' -> ']]').
    [Fact]
    public void Identifiers_with_closing_bracket_are_escaped()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "a]b",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "x] INT); DROP TABLE t;--", SqlType = "NVARCHAR(100)" },
            ],
        };

        var merge = SqlMergeBuilder.BuildMerge("db]o", "a]b", "#stg_1", schema);
        var create = SqlMergeBuilder.BuildCreateTableIfNotExists("db]o", "a]b", schema);
        var orphans = SqlMergeBuilder.BuildDeleteOrphans("db]o", "a]b", "#keys_1");

        Assert.Contains("[db]]o].[a]]b]", merge);
        Assert.Contains("[x]] INT); DROP TABLE t;--]", merge);
        Assert.Contains("[x]] INT); DROP TABLE t;--] NVARCHAR(100) NULL", create);
        Assert.Contains("[db]]o].[a]]b]", orphans);
        Assert.DoesNotContain("[a]b]", merge + create + orphans);
    }

    // El schema y la tabla también iban dentro de literales '...' en la comprobación de existencia.
    [Fact]
    public void Names_inside_string_literals_escape_single_quotes()
    {
        var sql = SqlMergeBuilder.BuildCreateTableIfNotExists("o'brien", "it's", Schema());

        Assert.Contains("s.name = N'o''brien'", sql);
        Assert.Contains("t.name = N'it''s'", sql);
    }

    [Fact]
    public void BuildEnsureUniqueIdIndex_creates_a_nonclustered_unique_index_only_without_duplicates()
    {
        var sql = SqlMergeBuilder.BuildEnsureUniqueIdIndex("dbo", "account");

        Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [UX_Sluice_Id] ON [dbo].[account] ([Id])", sql);
        Assert.Contains("GROUP BY [Id] HAVING COUNT(*) > 1", sql);
        Assert.Contains("sys.indexes", sql);
    }
}