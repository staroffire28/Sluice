using System.Data;
using Microsoft.Data.SqlClient;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

// Cómo se cargan las filas en la tabla destino.
public enum MergeMode
{
    // MERGE incondicional: actualiza todas las coincidentes (Force, Reload, Resync).
    Upsert,

    // MERGE que solo actualiza si el origen es más nuevo por SinkModifiedOn (delta de un Incremental).
    UpsertNewer,

    // Tabla destino vacía: inserción masiva directa con TABLOCK, sin staging ni MERGE.
    InsertIntoEmpty,
}

public interface ISqlMergeExecutor
{
    Task<int> MergeEntityAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default);

    // Con modo explícito. Por defecto MERGE incondicional, para los dobles de test que no lo distinguen.
    Task<int> MergeEntityAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        SqlServerVersion sqlVersion,
        MergeMode mode,
        CancellationToken cancellationToken = default) =>
        MergeEntityAsync(connection, schema, entity, cdmSchema, rows, sqlVersion, cancellationToken);

    Task<long> CountRowsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default);

    // Borra del destino las filas cuyo Id no está en el conjunto de claves del origen (huérfanas).
    // Lo usa el ISourceKeySink en memoria por defecto (el Reload real va por SqlSourceKeySink);
    // devuelve el número de filas eliminadas.
    Task<int> DeleteOrphansAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyCollection<object?> sourceKeys,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default);

    // Claves del origen de un Reload para borrar las huérfanas al final (ver ISourceKeySink). Por
    // defecto se juntan en memoria y van enteras a DeleteOrphansAsync, para los dobles de test que
    // no ejercitan SQL.
    ISourceKeySink CreateSourceKeySink(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        SqlServerVersion sqlVersion) =>
        new InMemorySourceKeySink((keys, ct) => DeleteOrphansAsync(connection, schema, entity, cdmSchema, keys, sqlVersion, ct));

    // Borra del destino las filas cuyo Id está en el conjunto (filas borradas en F&O, con
    // DeleteMode.Delete); devuelve el número de filas eliminadas. Por defecto no hace nada, para
    // los dobles de test que no ejercitan SQL.
    Task<int> DeleteKeysAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyCollection<object?> keys,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default) => Task.FromResult(0);

    Task<CdmEntitySchema> EnsureTableAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default);

    // Vacía una tabla de destino (reset de pipeline para pruebas). Si la tabla no existe todavía
    // (ej. nunca se corrió el MERGE para esa entidad) no es un error: no hay nada que limpiar.
    // Devuelve true si se truncó, false si la tabla no existía.
    Task<bool> TruncateTableAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CancellationToken cancellationToken = default);

    // Índice único no agrupado sobre [Id] (ver SqlMergeBuilder.BuildEnsureUniqueIdIndex).
    // false si la tabla ya tiene Ids duplicados y no se pudo crear. Por defecto no hace nada,
    // para los dobles de test que no ejercitan SQL.
    Task<bool> EnsureUniqueIdIndexAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CancellationToken cancellationToken = default) => Task.FromResult(true);

    // Columnas que tiene hoy la tabla destino (vacío si no existe). Por defecto vacío, para los dobles
    // de test que no ejercitan SQL.
    Task<IReadOnlySet<string>> GetColumnsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

    // Lee la tabla entera (solo las columnas pedidas, en ese orden) para la comparación de valores.
    // DBNull llega como null. Por defecto no soportado, para los dobles de test que no lo ejercitan.
    IAsyncEnumerable<object?[]> ReadRowsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

// Claves del origen de un Reload: se añaden lote a lote mientras se lee el parquet y, al final,
// DeleteOrphansAsync borra del destino las filas cuyo Id no llegó. Count son las claves añadidas.
public interface ISourceKeySink : IAsyncDisposable
{
    long Count { get; }

    Task AddAsync(IReadOnlyCollection<object?> keys, CancellationToken cancellationToken = default);

    Task<int> DeleteOrphansAsync(CancellationToken cancellationToken = default);
}

internal sealed class InMemorySourceKeySink(Func<IReadOnlyCollection<object?>, CancellationToken, Task<int>> deleteOrphans) : ISourceKeySink
{
    private readonly List<object?> _keys = [];

    public long Count => _keys.Count;

    public Task AddAsync(IReadOnlyCollection<object?> keys, CancellationToken cancellationToken = default)
    {
        _keys.AddRange(keys);
        return Task.CompletedTask;
    }

    public Task<int> DeleteOrphansAsync(CancellationToken cancellationToken = default) => deleteOrphans(_keys, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqlMergeExecutor : ISqlMergeExecutor
{
    // Filas por staging + MERGE. Con 1000 eran ~11.000 ciclos CREATE/bulk/MERGE/DROP para una tabla
    // de 11 M de filas; 20.000 los reduce ~20x sin disparar la memoria con tablas anchas.
    internal const int BatchSize = 20_000;

    public async Task<CdmEntitySchema> EnsureTableAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildCreateTableIfNotExists(schema, entity, cdmSchema, sqlVersion), cancellationToken);
        await EnsureColumnsAsync(connection, schema, entity, cdmSchema, cancellationToken);
        await AppendOrphanLabelColumnsAsync(connection, schema, entity, cdmSchema, cancellationToken);
        await ExpandStringColumnsAsync(connection, schema, entity, cdmSchema, cancellationToken);
        return cdmSchema;
    }

    public Task<int> MergeEntityAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default) =>
        MergeEntityAsync(connection, schema, entity, cdmSchema, rows, sqlVersion, MergeMode.Upsert, cancellationToken);

    public async Task<int> MergeEntityAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        SqlServerVersion sqlVersion,
        MergeMode mode,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        if (mode == MergeMode.InsertIntoEmpty)
        {
            // Tabla vacía y filas ya deduplicadas por Id: no hay nada que casar, inserción directa
            // con TABLOCK (bulk load con log mínimo cuando la base lo permite).
            var target = $"{SqlMergeBuilder.Quote(schema)}.{SqlMergeBuilder.Quote(entity)}";
            await BulkInsertAsync(connection, target, cdmSchema, rows, cancellationToken, SqlBulkCopyOptions.TableLock);
            return rows.Count;
        }

        var merged = 0;
        foreach (var batch in rows.Chunk(BatchSize))
        {
            merged += await MergeBatchAsync(connection, schema, entity, cdmSchema, batch, sqlVersion, mode == MergeMode.UpsertNewer, cancellationToken);
        }

        return merged;
    }

    public async Task<long> CountRowsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT_BIG(1) FROM {SqlMergeBuilder.Quote(schema)}.{SqlMergeBuilder.Quote(entity)}";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<bool> EnsureUniqueIdIndexAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = SqlMergeBuilder.BuildEnsureUniqueIdIndex(schema, entity);
        try
        {
            return Convert.ToBoolean(await cmd.ExecuteScalarAsync(cancellationToken));
        }
        catch (SqlException ex) when (ex.Number == 1913)
        {
            // 1913: otra réplica creó el índice a la vez. Ya existe, que es lo que se quería.
            return true;
        }
    }

    // Lectura en streaming (SequentialAccess): una fila en memoria cada vez. Sin CommandTimeout, como
    // el resto de sentencias de la conexión (SqlConnectionFactory): en tablas grandes tarda.
    public async IAsyncEnumerable<object?[]> ReadRowsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        IReadOnlyList<string> columns,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(SqlMergeBuilder.Quote))} FROM {SqlMergeBuilder.Quote(schema)}.{SqlMergeBuilder.Quote(entity)}";
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                values[i] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i);
            }

            yield return values;
        }
    }

    public async Task<bool> TruncateTableAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CancellationToken cancellationToken = default)
    {
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(1)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
                """;
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", entity);
            var exists = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
            {
                return false;
            }
        }

        await ExecuteNonQueryAsync(connection, $"TRUNCATE TABLE {SqlMergeBuilder.Quote(schema)}.{SqlMergeBuilder.Quote(entity)}", cancellationToken);
        return true;
    }

    public async Task<int> DeleteOrphansAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyCollection<object?> sourceKeys,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default)
    {
        var idAttr = cdmSchema.Attributes.FirstOrDefault(a => string.Equals(a.Name, "Id", StringComparison.OrdinalIgnoreCase));
        if (idAttr is null)
        {
            // Sin columna Id no hay clave de identidad: no se puede determinar huérfanas con seguridad.
            return 0;
        }

        // Sin claves de origen no hay contra qué comparar: el DELETE ... WHERE NOT EXISTS contra
        // una #keys vacía vaciaría la tabla entera (p. ej. un Reload con parquet consolidado vacío).
        if (sourceKeys.Count == 0)
        {
            return 0;
        }

        return await DeleteAgainstKeysAsync(connection, idAttr.SqlType, sourceKeys, sqlVersion,
            keyTable => SqlMergeBuilder.BuildDeleteOrphans(schema, entity, keyTable, sqlVersion), cancellationToken);
    }

    public ISourceKeySink CreateSourceKeySink(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        SqlServerVersion sqlVersion)
    {
        var idAttr = cdmSchema.Attributes.FirstOrDefault(a => string.Equals(a.Name, "Id", StringComparison.OrdinalIgnoreCase));
        return new SqlSourceKeySink(connection, schema, entity, idAttr?.SqlType, sqlVersion);
    }

    public async Task<int> DeleteKeysAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyCollection<object?> keys,
        SqlServerVersion sqlVersion,
        CancellationToken cancellationToken = default)
    {
        var idAttr = cdmSchema.Attributes.FirstOrDefault(a => string.Equals(a.Name, "Id", StringComparison.OrdinalIgnoreCase));
        if (idAttr is null || keys.Count == 0)
        {
            return 0;
        }

        return await DeleteAgainstKeysAsync(connection, idAttr.SqlType, keys, sqlVersion,
            keyTable => SqlMergeBuilder.BuildDeleteKeys(schema, entity, keyTable, sqlVersion), cancellationToken);
    }

    // Sube las claves a una #keys temporal y ejecuta el DELETE que la usa (un solo statement).
    private static async Task<int> DeleteAgainstKeysAsync(
        SqlConnection connection,
        string idSqlType,
        IReadOnlyCollection<object?> keys,
        SqlServerVersion sqlVersion,
        Func<string, string> buildDelete,
        CancellationToken cancellationToken)
    {
        // Sin el nombre de la entidad: va sin corchetes y no debe depender de datos externos.
        var keyTable = $"#keys_{Guid.NewGuid():N}";
        await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildKeyTable(keyTable, idSqlType, sqlVersion), cancellationToken);
        await BulkInsertKeysAsync(connection, keyTable, idSqlType, keys, cancellationToken);

        int deleted;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = buildDelete(keyTable);
            deleted = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildDropStaging(keyTable), cancellationToken);
        return deleted;
    }

    private static async Task BulkInsertKeysAsync(
        SqlConnection connection,
        string keyTable,
        string idSqlType,
        IReadOnlyCollection<object?> keys,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("Id", MapClrType(idSqlType));
        foreach (var key in keys)
        {
            var row = table.NewRow();
            row["Id"] = CoerceForSql(key, idSqlType) ?? DBNull.Value;
            table.Rows.Add(row);
        }

        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = keyTable, BulkCopyTimeout = 0 };
        bulk.ColumnMappings.Add("Id", "Id");
        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    // #keys de la conexión: cada lote se sube con SqlBulkCopy en cuanto llega, así en memoria solo
    // está el lote en curso y no todas las claves de la tabla (con 11 M de filas eran GB). Se crea
    // con el primer lote: sin claves no se toca SQL, y el DELETE contra una #keys vacía vaciaría
    // la tabla entera. Vive en la sesión, así que sobrevive a los #stg_ de cada lote del MERGE.
    private sealed class SqlSourceKeySink(
        SqlConnection connection,
        string schema,
        string entity,
        string? idSqlType,
        SqlServerVersion sqlVersion) : ISourceKeySink
    {
        // Sin el nombre de la entidad: va sin corchetes y no debe depender de datos externos.
        private readonly string _keyTable = $"#keys_{Guid.NewGuid():N}";
        private bool _created;

        public long Count { get; private set; }

        public async Task AddAsync(IReadOnlyCollection<object?> keys, CancellationToken cancellationToken = default)
        {
            // Sin columna Id no hay clave de identidad: no se puede determinar huérfanas con seguridad.
            if (idSqlType is null || keys.Count == 0)
            {
                return;
            }

            if (!_created)
            {
                await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildKeyTable(_keyTable, idSqlType, sqlVersion), cancellationToken);
                _created = true;
            }

            await BulkInsertKeysAsync(connection, _keyTable, idSqlType, keys, cancellationToken);
            Count += keys.Count;
        }

        public async Task<int> DeleteOrphansAsync(CancellationToken cancellationToken = default)
        {
            if (!_created)
            {
                return 0;
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlMergeBuilder.BuildDeleteOrphans(schema, entity, _keyTable, sqlVersion);
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Si la entidad falló con la conexión ya rota, la #keys muere con la sesión.
        public async ValueTask DisposeAsync()
        {
            if (_created && connection.State == ConnectionState.Open)
            {
                _created = false;
                try
                {
                    await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildDropStaging(_keyTable), CancellationToken.None);
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    // Sesión rota: la #keys muere con ella; no tapar la excepción original.
                }
            }
        }
    }

    private static async Task<int> MergeBatchAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        IReadOnlyList<Dictionary<string, object?>> batch,
        SqlServerVersion sqlVersion,
        bool onlyNewer,
        CancellationToken cancellationToken)
    {
        // Sin el nombre de la entidad: va sin corchetes y no debe depender de datos externos.
        var stagingName = $"#stg_{Guid.NewGuid():N}";
        await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildStagingTable(stagingName, cdmSchema, sqlVersion), cancellationToken);
        await BulkInsertAsync(connection, stagingName, cdmSchema, batch, cancellationToken);
        await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildMerge(schema, entity, stagingName, cdmSchema, sqlVersion, onlyNewer), cancellationToken);
        await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildDropStaging(stagingName), cancellationToken);
        return batch.Count;
    }

    private static async Task BulkInsertAsync(
        SqlConnection connection,
        string stagingName,
        CdmEntitySchema schema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken cancellationToken,
        SqlBulkCopyOptions options = SqlBulkCopyOptions.Default)
    {
        var table = new DataTable();
        foreach (var attr in schema.Attributes)
        {
            table.Columns.Add(attr.Name, MapClrType(attr.SqlType));
        }

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();
            foreach (var attr in schema.Attributes)
            {
                // Columna de traducción de enum: no viene en el parquet; se resuelve desde el valor
                // crudo de su columna origen (Option -> ExternalValue). Sin match => NULL, igual que
                // el CASE ... END del SP de Microsoft cuando el valor no está en el optionset.
                var raw = attr.EnumSourceColumn is { } src && attr.EnumMap is { } map
                    ? ResolveEnumLabel(row, src, map)
                    : row.TryGetValue(attr.Name, out var v) ? v : null;

                dataRow[attr.Name] = CoerceForSql(raw, attr.SqlType) ?? DBNull.Value;
            }

            table.Rows.Add(dataRow);
        }

        using var bulk = new SqlBulkCopy(connection, options, externalTransaction: null)
        {
            DestinationTableName = stagingName,
            BulkCopyTimeout = 0,
        };

        foreach (var attr in schema.Attributes)
        {
            bulk.ColumnMappings.Add(attr.Name, attr.Name);
        }

        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    // Crea las columnas del schema que falten en una tabla ya existente. Sin esto, activar la
    // traducción de enums sobre tablas materializadas en una corrida anterior dejaría la columna
    // [<col>_$label] fuera de la tabla (CREATE TABLE IF NOT EXISTS no la añade) y el MERGE
    // fallaría con "Invalid column name". Idempotente: sobre una tabla recién creada no hace nada.
    // Con TranslateEnums desactivado, tablas que ya tienen columnas [<col>_$label] deben seguir
    // recibiendo NULL en cada MERGE; si no, las etiquetas antiguas quedan obsoletas en silencio.
    private static async Task AppendOrphanLabelColumnsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        CancellationToken cancellationToken)
    {
        var known = cdmSchema.Attributes.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", entity);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var column = reader.GetString(0);
            if (!column.EndsWith(EnumLabelEnricher.LabelSuffix, StringComparison.OrdinalIgnoreCase)
                || known.Contains(column))
            {
                continue;
            }

            cdmSchema.Attributes.Add(new CdmAttribute
            {
                Name = column,
                SqlType = "NVARCHAR(MAX)",
                IsNullable = true,
            });
            known.Add(column);
        }
    }

    public async Task<IReadOnlySet<string>> GetColumnsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CancellationToken cancellationToken = default)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            """;
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", entity);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            existing.Add(reader.GetString(0));
        }

        return existing;
    }

    private async Task EnsureColumnsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        CancellationToken cancellationToken)
    {
        var existing = await GetColumnsAsync(connection, schema, entity, cancellationToken);

        foreach (var attr in cdmSchema.Attributes)
        {
            if (existing.Contains(attr.Name))
            {
                continue;
            }

            await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildAlterAddColumn(schema, entity, attr), cancellationToken);
        }
    }

    private static async Task ExpandStringColumnsAsync(
        SqlConnection connection,
        string schema,
        string entity,
        CdmEntitySchema cdmSchema,
        CancellationToken cancellationToken)
    {
        foreach (var attr in cdmSchema.Attributes.Where(a => a.SqlType.StartsWith("NVARCHAR", StringComparison.OrdinalIgnoreCase)))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT CHARACTER_MAXIMUM_LENGTH
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND COLUMN_NAME = @column
                """;
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", entity);
            cmd.Parameters.AddWithValue("@column", attr.Name);

            var maxLen = await cmd.ExecuteScalarAsync(cancellationToken);
            if (maxLen is int len && len != -1)
            {
                await ExecuteNonQueryAsync(connection, SqlMergeBuilder.BuildAlterStringToMax(schema, entity, attr.Name), cancellationToken);
            }
        }
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    // Traduce el valor crudo de un optionset a su ExternalValue. El parquet puede traer el Option
    // como long/int/decimal o como string numérico; todos se normalizan a long para el lookup.
    // Devuelve null si la columna origen es nula o el valor no está en el mapa (= CASE sin match).
    private static string? ResolveEnumLabel(
        IReadOnlyDictionary<string, object?> row,
        string sourceColumn,
        IReadOnlyDictionary<long, string> map)
    {
        if (!row.TryGetValue(sourceColumn, out var raw) || raw is null)
        {
            return null;
        }

        long option;
        switch (raw)
        {
            case long l:
                option = l;
                break;
            case int i:
                option = i;
                break;
            case short s:
                option = s;
                break;
            case decimal d:
                // Un Option de enum siempre es entero. Un decimal con parte fraccionaria es un
                // valor anómalo: devolver null (sin etiqueta) en vez de truncar y mapear a otra.
                if (d != Math.Truncate(d) || d < long.MinValue || d > long.MaxValue)
                {
                    return null;
                }

                option = (long)d;
                break;
            default:
                if (!long.TryParse(raw.ToString(), out option))
                {
                    return null;
                }

                break;
        }

        return map.TryGetValue(option, out var label) ? label : null;
    }

    private static object? CoerceForSql(object? value, string sqlType)
    {
        if (value is null)
        {
            return null;
        }

        if (sqlType.StartsWith("UNIQUEIDENTIFIER", StringComparison.OrdinalIgnoreCase))
        {
            return value is Guid g ? g : Guid.Parse(value.ToString()!);
        }

        if (sqlType.StartsWith("DATETIME", StringComparison.OrdinalIgnoreCase))
        {
            return value switch
            {
                DateTimeOffset dto => dto.UtcDateTime,
                DateTime dt => dt,
                string s => DateTime.Parse(s),
                _ => null,
            };
        }

        if (sqlType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase))
        {
            return value is bool b ? b : value.ToString() is "1" or "true";
        }

        return value;
    }

    private static Type MapClrType(string sqlType)
    {
        if (sqlType.StartsWith("UNIQUEIDENTIFIER", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(Guid);
        }

        if (sqlType.StartsWith("DATETIME", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(DateTime);
        }

        if (sqlType.StartsWith("BIT", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(bool);
        }

        if (sqlType.StartsWith("BIGINT", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(long);
        }

        if (sqlType.StartsWith("INT", StringComparison.OrdinalIgnoreCase) && !sqlType.StartsWith("INT64", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(int);
        }

        if (sqlType.StartsWith("SMALLINT", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(short);
        }

        if (sqlType.StartsWith("DECIMAL", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(decimal);
        }

        if (sqlType.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(double);
        }

        return typeof(string);
    }
}