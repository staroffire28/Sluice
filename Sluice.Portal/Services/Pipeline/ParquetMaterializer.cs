using System.Runtime.CompilerServices;
using Parquet;
using Parquet.Schema;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

public static class ParquetMaterializer
{
    // --- Streaming (para consolidación memory-safe y MERGE por lotes) ---

    // Metadato del footer con las filas que no son de borrado de F&O: con él, contar sin borrados
    // (reconcile) lee solo el footer en vez de la columna IsDelete de cada row group.
    public const string NonDeleteRowsKey = "sluice.nonDeleteRows";

    // Escritura en streaming: cada batch se escribe como un row group propio, así se puede
    // volcar gran volumen sin tener todo en memoria a la vez. metadata se pide al terminar los lotes
    // (cuando ya se conocen los recuentos) y va a los metadatos clave-valor del footer.
    public static async Task WriteStreamingAsync(
        Stream destination,
        CdmEntitySchema schema,
        IAsyncEnumerable<IReadOnlyList<Dictionary<string, object?>>> batches,
        CancellationToken cancellationToken = default,
        Func<Dictionary<string, string>>? metadata = null)
    {
        var fields = schema.Attributes.Select(CreateDataField).ToArray();
        var parquetSchema = new ParquetSchema(fields);

        await using var writer = await ParquetWriter.CreateAsync(parquetSchema, destination, cancellationToken: cancellationToken);
        await foreach (var batch in batches.WithCancellation(cancellationToken))
        {
            if (batch.Count == 0)
            {
                continue;
            }

            using var groupWriter = writer.CreateRowGroup();
            foreach (var field in fields)
            {
                await WriteFieldAsync(groupWriter, field, schema, batch, cancellationToken);
            }
        }

        if (metadata is not null)
        {
            writer.CustomMetadata = metadata();
        }
    }

    // Lectura en streaming: yield fila a fila, leyendo un row group a la vez (pico de
    // memoria = un row group, no el archivo entero).
    public static async IAsyncEnumerable<Dictionary<string, object?>> ReadStreamingAsync(
        Stream source,
        CdmEntitySchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var reader = await ParquetReader.CreateAsync(source, cancellationToken: cancellationToken);
        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var rowGroupReader = reader.OpenRowGroupReader(g);
            var rowCount = (int)rowGroupReader.RowCount;
            var columnData = new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var attr in schema.Attributes)
            {
                var field = reader.Schema.DataFields.FirstOrDefault(f =>
                    string.Equals(f.Name, attr.Name, StringComparison.OrdinalIgnoreCase));
                if (field is null)
                {
                    continue;
                }

                columnData[attr.Name] = await ReadFieldAsync(rowGroupReader, field, rowCount, cancellationToken);
            }

            for (var r = 0; r < rowCount; r++)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var attr in schema.Attributes)
                {
                    if (columnData.TryGetValue(attr.Name, out var values))
                    {
                        row[attr.Name] = values[r];
                    }
                }

                yield return row;
            }
        }
    }

    // Conteo de filas por metadatos (suma de RowCount de cada row group), sin materializar. Con
    // excludeDeleteRows vale el recuento que guardó la consolidación en el footer (NonDeleteRowsKey);
    // si no está (un parquet anterior), se lee la columna IsDelete (solo esa) para no contar las filas
    // de borrado de F&O.
    public static async Task<long> CountRowsAsync(Stream source, bool excludeDeleteRows = false, CancellationToken cancellationToken = default)
    {
        await using var reader = await ParquetReader.CreateAsync(source, cancellationToken: cancellationToken);
        if (excludeDeleteRows
            && reader.CustomMetadata.TryGetValue(NonDeleteRowsKey, out var stored)
            && long.TryParse(stored, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var nonDeleteRows))
        {
            return nonDeleteRows;
        }

        var isDeleteField = excludeDeleteRows
            ? reader.Schema.DataFields.FirstOrDefault(f => string.Equals(f.Name, CdmSchemaParser.IsDeleteColumn, StringComparison.OrdinalIgnoreCase))
            : null;
        long count = 0;
        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var rowGroupReader = reader.OpenRowGroupReader(g);
            if (isDeleteField is null)
            {
                count += rowGroupReader.RowCount;
                continue;
            }

            var flags = await ReadFieldAsync(rowGroupReader, isDeleteField, (int)rowGroupReader.RowCount, cancellationToken);
            count += flags.Count(flag => !DeleteRows.IsDeleteValue(flag));
        }

        return count;
    }

    // Proyección de Id + Watermark (+ versión, si se pide y existe) para la pasada 1 del
    // consolidador: lee solo esas columnas (columnar = barato) sin materializar las filas anchas.
    public static async IAsyncEnumerable<(object? Id, object? Watermark, object? Version)> ReadKeyWatermarksAsync(
        Stream source,
        string idColumn,
        string watermarkColumn,
        string? versionColumn = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var reader = await ParquetReader.CreateAsync(source, cancellationToken: cancellationToken);
        var idField = reader.Schema.DataFields.FirstOrDefault(f => string.Equals(f.Name, idColumn, StringComparison.OrdinalIgnoreCase));
        var wmField = reader.Schema.DataFields.FirstOrDefault(f => string.Equals(f.Name, watermarkColumn, StringComparison.OrdinalIgnoreCase));
        var versionField = versionColumn is null
            ? null
            : reader.Schema.DataFields.FirstOrDefault(f => string.Equals(f.Name, versionColumn, StringComparison.OrdinalIgnoreCase));
        if (idField is null)
        {
            yield break;
        }

        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var rowGroupReader = reader.OpenRowGroupReader(g);
            var rowCount = (int)rowGroupReader.RowCount;
            var ids = await ReadFieldAsync(rowGroupReader, idField, rowCount, cancellationToken);
            var wms = wmField is null ? null : await ReadFieldAsync(rowGroupReader, wmField, rowCount, cancellationToken);
            var versions = versionField is null ? null : await ReadFieldAsync(rowGroupReader, versionField, rowCount, cancellationToken);

            for (var r = 0; r < rowCount; r++)
            {
                yield return (ids[r], wms?[r], versions?[r]);
            }
        }
    }

    public static async Task WriteAsync(
        string path,
        CdmEntitySchema schema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await WriteToStreamAsync(stream, schema, rows, cancellationToken);
    }

    // El parquet canónico vive en ADLS (Approach B): el ingest materializa a un stream
    // con seek (MemoryStream) y lo sube; el stage SQL lo lee descargándolo a memoria.
    public static async Task WriteToStreamAsync(
        Stream destination,
        CdmEntitySchema schema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        var fields = schema.Attributes.Select(CreateDataField).ToArray();
        var parquetSchema = new ParquetSchema(fields);

        await using var writer = await ParquetWriter.CreateAsync(parquetSchema, destination, cancellationToken: cancellationToken);
        using var groupWriter = writer.CreateRowGroup();

        foreach (var field in fields)
        {
            await WriteFieldAsync(groupWriter, field, schema, rows, cancellationToken);
        }
    }

    public static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAsync(
        string path,
        CdmEntitySchema schema,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await ReadFromStreamAsync(stream, schema, cancellationToken);
    }

    public static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadFromStreamAsync(
        Stream source,
        CdmEntitySchema schema,
        CancellationToken cancellationToken = default)
    {
        await using var reader = await ParquetReader.CreateAsync(source, cancellationToken: cancellationToken);
        if (reader.RowGroupCount == 0)
        {
            return [];
        }

        using var rowGroupReader = reader.OpenRowGroupReader(0);
        var rowCount = (int)rowGroupReader.RowCount;
        var columnData = new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var attr in schema.Attributes)
        {
            var field = reader.Schema.DataFields.FirstOrDefault(f =>
                string.Equals(f.Name, attr.Name, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                continue;
            }

            columnData[attr.Name] = await ReadFieldAsync(rowGroupReader, field, rowCount, cancellationToken);
        }

        var rows = new List<Dictionary<string, object?>>(rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in schema.Attributes)
            {
                if (columnData.TryGetValue(attr.Name, out var values))
                {
                    row[attr.Name] = values[r];
                }
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task WriteFieldAsync(
        ParquetRowGroupWriter groupWriter,
        DataField field,
        CdmEntitySchema schema,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        if (field is DataField<Guid?> guidField)
        {
            await WriteColumnAsync(groupWriter, guidField, rows, CoerceGuid, cancellationToken);
            return;
        }

        if (field is DataField<DateTime?> dateField)
        {
            await WriteColumnAsync(groupWriter, dateField, rows, CoerceDateTime, cancellationToken);
            return;
        }

        if (field is DataField<bool?> boolField)
        {
            await WriteColumnAsync(groupWriter, boolField, rows, CoerceBool, cancellationToken);
            return;
        }

        if (field is DataField<long?> longField)
        {
            await WriteColumnAsync(groupWriter, longField, rows, CoerceLong, cancellationToken);
            return;
        }

        if (field is DataField<int?> intField)
        {
            await WriteColumnAsync(groupWriter, intField, rows, CoerceInt, cancellationToken);
            return;
        }

        if (field is DataField<decimal?> decimalField)
        {
            await WriteColumnAsync(groupWriter, decimalField, rows, CoerceDecimal, cancellationToken);
            return;
        }

        if (field is DataField<double?> doubleField)
        {
            await WriteColumnAsync(groupWriter, doubleField, rows, CoerceDouble, cancellationToken);
            return;
        }

        if (field is DataField<string> stringField)
        {
            var strings = rows.Select(r => r.TryGetValue(field.Name, out var v) ? v?.ToString() : null).ToArray();
            await groupWriter.WriteAsync(stringField, strings);
        }
    }

    private static async Task<object?[]> ReadFieldAsync(
        ParquetRowGroupReader rowGroupReader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        if (field.ClrType == typeof(Guid))
        {
            return await ReadColumnAsync<Guid>(rowGroupReader, field, rowCount, cancellationToken);
        }

        if (field.ClrType == typeof(DateTime))
        {
            return await ReadColumnAsync<DateTime>(rowGroupReader, field, rowCount, cancellationToken);
        }

        if (field.ClrType == typeof(bool))
        {
            return await ReadColumnAsync<bool>(rowGroupReader, field, rowCount, cancellationToken);
        }

        if (field.ClrType == typeof(long))
        {
            return await ReadColumnAsync<long>(rowGroupReader, field, rowCount, cancellationToken);
        }

        if (field.ClrType == typeof(int))
        {
            return await ReadColumnAsync<int>(rowGroupReader, field, rowCount, cancellationToken);
        }

        if (field.ClrType == typeof(decimal))
        {
            return await ReadColumnAsync<decimal>(rowGroupReader, field, rowCount, cancellationToken);
        }

        if (field.ClrType == typeof(double))
        {
            return await ReadColumnAsync<double>(rowGroupReader, field, rowCount, cancellationToken);
        }

        var strings = new string[rowCount];
        await rowGroupReader.ReadAsync(field, strings, cancellationToken: cancellationToken);
        return strings.Cast<object?>().ToArray();
    }

    private static async Task WriteColumnAsync<T>(
        ParquetRowGroupWriter groupWriter,
        DataField<T?> field,
        IReadOnlyList<Dictionary<string, object?>> rows,
        Func<object?, T?> coerce,
        CancellationToken cancellationToken)
        where T : struct
    {
        var values = rows.Select(r => coerce(r.TryGetValue(field.Name, out var v) ? v : null)).ToArray();
        await groupWriter.WriteAsync<T>(field, values.AsMemory(), cancellationToken: cancellationToken);
    }

    // Se lee según cómo está declarada la columna EN EL ARCHIVO: los parquet nuevos son
    // anulables; los escritos antes de este cambio (consolidados en ADLS) no, y se siguen
    // leyendo igual.
    private static async Task<object?[]> ReadColumnAsync<T>(
        ParquetRowGroupReader rowGroupReader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
        where T : struct
    {
        if (field.IsNullable)
        {
            var nullable = new T?[rowCount];
            await rowGroupReader.ReadAsync(field, nullable.AsMemory(), cancellationToken: cancellationToken);
            return nullable.Select(v => v.HasValue ? (object?)v.Value : null).ToArray();
        }

        var buffer = new T[rowCount];
        await rowGroupReader.ReadAsync(field, buffer.AsMemory(), cancellationToken: cancellationToken);
        return buffer.Cast<object?>().ToArray();
    }

    // Todas las columnas se escriben anulables: un NULL del CSV debe llegar como NULL a SQL
    // (antes se convertía en Guid.Empty/0/false/0001-01-01). El Id también: una fila sin Id
    // válido queda con Id null y el consolidador la descarta (y la cuenta) en vez de colapsar
    // todas en Guid.Empty.
    private static DataField CreateDataField(CdmAttribute attr) =>
        MapParquetType(attr.SqlType) switch
        {
            Type t when t == typeof(Guid) => new DataField<Guid?>(attr.Name),
            Type t when t == typeof(DateTime) => new DataField<DateTime?>(attr.Name),
            Type t when t == typeof(bool) => new DataField<bool?>(attr.Name),
            Type t when t == typeof(long) => new DataField<long?>(attr.Name),
            Type t when t == typeof(int) => new DataField<int?>(attr.Name),
            Type t when t == typeof(decimal) => new DataField<decimal?>(attr.Name),
            Type t when t == typeof(double) => new DataField<double?>(attr.Name),
            _ => new DataField<string>(attr.Name),
        };

    private static Guid? CoerceGuid(object? value) =>
        value switch
        {
            Guid g => g,
            null => null,
            _ => Guid.Parse(value.ToString()!),
        };

    private static DateTime? CoerceDateTime(object? value) => value switch
    {
        DateTimeOffset dto => dto.UtcDateTime,
        DateTime dt => dt,
        null => null,
        _ => DateTime.Parse(value.ToString()!),
    };

    private static bool? CoerceBool(object? value) =>
        value is null ? null : value is bool b ? b : value.ToString() is "1" or "true" or "True";

    private static long? CoerceLong(object? value) => value is null ? null : Convert.ToInt64(value);
    private static int? CoerceInt(object? value) => value is null ? null : Convert.ToInt32(value);
    private static decimal? CoerceDecimal(object? value) => value is null ? null : Convert.ToDecimal(value);
    private static double? CoerceDouble(object? value) => value is null ? null : Convert.ToDouble(value);

    private static Type MapParquetType(string sqlType)
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

        if (sqlType.StartsWith("INT", StringComparison.OrdinalIgnoreCase)
            && !sqlType.StartsWith("INT64", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(int);
        }

        if (sqlType.StartsWith("SMALLINT", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(int);
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