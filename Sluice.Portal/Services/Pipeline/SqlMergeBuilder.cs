using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

public static class SqlMergeBuilder
{
    public const string UniqueIdIndexName = "UX_Sluice_Id";

    // Nombres de entidad (carpetas de ADLS), columnas (model.json) y schema (config de la
    // conexión) vienen de fuera: se escapan como QUOTENAME (']' -> ']]') en todo el T-SQL, para
    // que una ']' no cierre el identificador e inyecte sentencias.
    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    private static string Table(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";

    // sqlVersion permite ramificar el T-SQL por destino (2016–2022). Hoy el dialecto es
    // común (MERGE + staging); el parámetro queda cableado para futuras diferencias.
    public static string BuildCreateTableIfNotExists(
        string schema,
        string table,
        CdmEntitySchema entity,
        SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022)
    {
        var columns = string.Join(",\n    ", entity.Attributes.Select(a =>
        {
            var nullability = a.IsNullable ? "NULL" : "NOT NULL";
            return $"{Quote(a.Name)} {a.SqlType} {nullability}";
        }));

        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = {Literal(schema)} AND t.name = {Literal(table)})
            BEGIN
                CREATE TABLE {Table(schema, table)} (
                    {columns}
                );
            END
            """;
    }

    // Índice único NO agrupado sobre [Id]: el MERGE busca por Id sin recorrer la tabla y no
    // pueden entrar duplicados. No agrupado porque los Id son GUID aleatorios (un índice
    // agrupado fragmentaría la tabla en cada carga). Idempotente; si la tabla ya tiene Ids
    // duplicados no se crea y devuelve 0 (el llamador avisa); 1 si existe o se creó.
    public static string BuildEnsureUniqueIdIndex(string schema, string table) =>
        $"""
        IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({Literal(Table(schema, table))}) AND name = {Literal(UniqueIdIndexName)})
            SELECT CAST(1 AS bit);
        ELSE IF EXISTS (SELECT [Id] FROM {Table(schema, table)} GROUP BY [Id] HAVING COUNT(*) > 1)
            SELECT CAST(0 AS bit);
        ELSE
        BEGIN
            CREATE UNIQUE NONCLUSTERED INDEX {Quote(UniqueIdIndexName)} ON {Table(schema, table)} ([Id]);
            SELECT CAST(1 AS bit);
        END
        """;

    public static string BuildAlterStringToMax(string schema, string table, string column) =>
        $"ALTER TABLE {Table(schema, table)} ALTER COLUMN {Quote(column)} NVARCHAR(MAX) NULL;";

    // Añade una columna que falta a una tabla ya existente (evolución de esquema: p. ej. las
    // columnas [<col>_$label] al activar la traducción de enums sobre tablas ya materializadas).
    // Siempre NULL: una columna nueva sobre una tabla con filas no puede ser NOT NULL sin default.
    public static string BuildAlterAddColumn(string schema, string table, CdmAttribute attr) =>
        $"ALTER TABLE {Table(schema, table)} ADD {Quote(attr.Name)} {attr.SqlType} NULL;";

    public static string BuildStagingTable(
        string stagingName,
        CdmEntitySchema entity,
        SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022)
    {
        var columns = string.Join(", ", entity.Attributes.Select(a =>
        {
            var sqlType = a.SqlType.StartsWith("NVARCHAR", StringComparison.OrdinalIgnoreCase)
                ? "NVARCHAR(MAX)"
                : a.SqlType;
            return $"{Quote(a.Name)} {sqlType} NULL";
        }));

        return $"CREATE TABLE {stagingName} ({columns});";
    }

    // onlyNewer (delta de un Incremental): actualiza solo si el origen es más nuevo por
    // SinkModifiedOn ("mismo watermark" no reescribe salvo versionnumber mayor, si la tabla lo
    // trae). Misma regla que el consolidador: sin watermark o sin versión pierde frente a
    // cualquiera. Sin columna de watermark, MERGE incondicional.
    public static string BuildMerge(
        string schema,
        string table,
        string stagingName,
        CdmEntitySchema entity,
        SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022,
        bool onlyNewer = false)
    {
        var nonId = entity.Attributes.Where(a => !string.Equals(a.Name, "Id", StringComparison.OrdinalIgnoreCase)).ToList();
        var updateSet = string.Join(", ", nonId.Select(a => $"target.{Quote(a.Name)} = source.{Quote(a.Name)}"));
        var insertCols = string.Join(", ", entity.Attributes.Select(a => Quote(a.Name)));
        var insertVals = string.Join(", ", entity.Attributes.Select(a => $"source.{Quote(a.Name)}"));
        var wm = Quote(CdmSchemaParser.WatermarkColumn);
        var ver = Quote(CdmSchemaParser.VersionColumn);
        var versionTie = entity.Attributes.Any(a => string.Equals(a.Name, CdmSchemaParser.VersionColumn, StringComparison.OrdinalIgnoreCase))
            ? $" OR (source.{wm} = target.{wm} AND source.{ver} IS NOT NULL AND (target.{ver} IS NULL OR source.{ver} > target.{ver}))"
            : "";
        var matched = onlyNewer && entity.Attributes.Any(a => string.Equals(a.Name, CdmSchemaParser.WatermarkColumn, StringComparison.OrdinalIgnoreCase))
            ? $"WHEN MATCHED AND (target.{wm} IS NULL OR source.{wm} > target.{wm}{versionTie}) THEN UPDATE"
            : "WHEN MATCHED THEN UPDATE";

        return $"""
            MERGE {Table(schema, table)} AS target
            USING {stagingName} AS source
            ON target.[Id] = source.[Id]
            {matched} SET {updateSet}
            WHEN NOT MATCHED THEN INSERT ({insertCols}) VALUES ({insertVals});
            """;
    }

    public static string BuildDropStaging(string stagingName) => $"DROP TABLE {stagingName};";

    // Recarga por tabla (modo Reload del re-sync): tras el upsert del parquet consolidado, se
    // borran las filas cuyo Id ya no existe en el origen (huérfanas que el MERGE upsert no quita).
    // Se hace contra una tabla temporal con TODAS las claves del parquet, en un solo DELETE: la
    // tabla nunca queda vacía (a diferencia de un TRUNCATE previo), así que no hay ventana de crash.
    public static string BuildKeyTable(
        string keyTableName,
        string idSqlType,
        SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022) =>
        $"CREATE TABLE {keyTableName} ([Id] {idSqlType} NOT NULL);";

    public static string BuildDeleteOrphans(
        string schema,
        string table,
        string keyTableName,
        SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022) =>
        $"DELETE t FROM {Table(schema, table)} t WHERE NOT EXISTS (SELECT 1 FROM {keyTableName} k WHERE k.[Id] = t.[Id]);";

    // Filas borradas en F&O (DeleteMode.Delete): se borran las que SÍ están en la #keys.
    public static string BuildDeleteKeys(
        string schema,
        string table,
        string keyTableName,
        SqlServerVersion sqlVersion = SqlServerVersion.SqlServer2022) =>
        $"DELETE t FROM {Table(schema, table)} t WHERE EXISTS (SELECT 1 FROM {keyTableName} k WHERE k.[Id] = t.[Id]);";
}
