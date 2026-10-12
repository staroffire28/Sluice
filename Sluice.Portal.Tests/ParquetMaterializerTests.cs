using Sluice.Portal.Models;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

public class ParquetMaterializerTests
{
    [Fact]
    public async Task WriteAndRead_RoundTripsRows()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "account",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };

        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var rows = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["Id"] = id,
                ["Name"] = "Alice",
                ["SinkModifiedOn"] = DateTimeOffset.Parse("2026-06-08T10:00:00Z"),
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"synapse-parquet-{Guid.NewGuid():N}.parquet");
        try
        {
            await ParquetMaterializer.WriteAsync(path, schema, rows);
            var read = await ParquetMaterializer.ReadAsync(path, schema);
            Assert.Single(read);
            Assert.Equal(id, read[0]["Id"]);
            Assert.Equal("Alice", read[0]["Name"]);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task WriteToStream_AndReadFromStream_RoundTripsRows()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "account",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
            ],
        };

        var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = id, ["Name"] = "Bob" },
        };

        using var buffer = new MemoryStream();
        await ParquetMaterializer.WriteToStreamAsync(buffer, schema, rows);

        // Simula el viaje por ADLS: lo escrito se vuelve a leer desde un stream con seek.
        buffer.Position = 0;
        var read = await ParquetMaterializer.ReadFromStreamAsync(buffer, schema);

        Assert.Single(read);
        Assert.Equal(id, read[0]["Id"]);
        Assert.Equal("Bob", read[0]["Name"]);
    }

    [Fact]
    public async Task WriteStreaming_writes_multiple_row_groups_and_reads_back_all_rows()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "account",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
                new CdmAttribute { Name = "SinkModifiedOn", SqlType = "DATETIME2(7)" },
            ],
        };

        static Dictionary<string, object?> Row(string id, string name, string wm) => new()
        {
            ["Id"] = Guid.Parse(id),
            ["Name"] = name,
            ["SinkModifiedOn"] = DateTimeOffset.Parse(wm),
        };

        // Dos batches → dos row groups en el mismo archivo.
        async IAsyncEnumerable<IReadOnlyList<Dictionary<string, object?>>> Batches()
        {
            yield return new List<Dictionary<string, object?>> { Row("11111111-1111-1111-1111-111111111111", "Alice", "2026-06-08T10:00:00Z") };
            await Task.Yield();
            yield return new List<Dictionary<string, object?>> { Row("22222222-2222-2222-2222-222222222222", "Bob", "2026-06-09T10:00:00Z") };
        }

        using var buffer = new MemoryStream();
        await ParquetMaterializer.WriteStreamingAsync(buffer, schema, Batches());

        // Lectura streaming: recupera ambas filas (ambos row groups).
        buffer.Position = 0;
        var streamed = new List<Dictionary<string, object?>>();
        await foreach (var row in ParquetMaterializer.ReadStreamingAsync(buffer, schema))
        {
            streamed.Add(row);
        }

        Assert.Equal(2, streamed.Count);
        Assert.Contains(streamed, r => (string?)r["Name"] == "Alice");
        Assert.Contains(streamed, r => (string?)r["Name"] == "Bob");

        // Proyección Id+Watermark: dos pares, sin materializar la columna Name.
        buffer.Position = 0;
        var keys = new List<(object? Id, object? Watermark)>();
        await foreach (var pair in ParquetMaterializer.ReadKeyWatermarksAsync(buffer, "Id", "SinkModifiedOn"))
        {
            keys.Add(pair);
        }

        Assert.Equal(2, keys.Count);
        Assert.All(keys, k => Assert.NotNull(k.Id));
        Assert.All(keys, k => Assert.NotNull(k.Watermark));
    }

    // El reconcile cuenta filas sobre un stream de ADLS leído por rangos: contar no debe leer los
    // datos (antes se descargaba el parquet entero; 2,3 GB y 1,9 GB de memoria en un job real).
    [Fact]
    public async Task CountRows_reads_only_the_footer_not_the_data()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "account",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Name", SqlType = "NVARCHAR(MAX)" },
            ],
        };

        async IAsyncEnumerable<IReadOnlyList<Dictionary<string, object?>>> Batches()
        {
            for (var b = 0; b < 3; b++)
            {
                await Task.Yield();
                yield return Enumerable.Range(0, 20_000)
                    .Select(_ => new Dictionary<string, object?> { ["Id"] = Guid.NewGuid(), ["Name"] = Guid.NewGuid().ToString("N") })
                    .ToList();
            }
        }

        using var buffer = new MemoryStream();
        await ParquetMaterializer.WriteStreamingAsync(buffer, schema, Batches());
        var counting = new ReadCountingStream(buffer.ToArray());

        var rows = await ParquetMaterializer.CountRowsAsync(counting);

        Assert.Equal(60_000, rows);
        Assert.True(counting.BytesRead < counting.Length / 20,
            $"Contar leyó {counting.BytesRead} de {counting.Length} bytes.");
    }

    // Stream con seek que cuenta los bytes leídos (como el de ADLS por rangos).
    private sealed class ReadCountingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await base.ReadAsync(buffer, offset, count, cancellationToken);
            BytesRead += read;
            return read;
        }
    }

    // Antes todas las columnas Parquet eran no anulables: un NULL del CSV se escribía como
    // Guid.Empty / 0 / false / 0001-01-01 y llegaba así a SQL.
    [Fact]
    public async Task WriteAndRead_PreservesNulls_InEveryColumnType()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "t",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "OwnerId", SqlType = "UNIQUEIDENTIFIER" },
                new CdmAttribute { Name = "DueOn", SqlType = "DATETIME2(7)" },
                new CdmAttribute { Name = "Active", SqlType = "BIT" },
                new CdmAttribute { Name = "Qty", SqlType = "BIGINT" },
                new CdmAttribute { Name = "Code", SqlType = "INT" },
                new CdmAttribute { Name = "Amount", SqlType = "DECIMAL(38,6)" },
                new CdmAttribute { Name = "Ratio", SqlType = "FLOAT" },
                new CdmAttribute { Name = "Note", SqlType = "NVARCHAR(MAX)" },
            ],
        };
        var id = Guid.NewGuid();

        using var ms = new MemoryStream();
        await ParquetMaterializer.WriteToStreamAsync(ms, schema, [new() { ["Id"] = id }]);
        var read = await ParquetMaterializer.ReadFromStreamAsync(new MemoryStream(ms.ToArray()), schema);

        var row = Assert.Single(read);
        Assert.Equal(id, row["Id"]);
        foreach (var column in new[] { "OwnerId", "DueOn", "Active", "Qty", "Code", "Amount", "Ratio", "Note" })
        {
            Assert.Null(row[column]);
        }
    }

    // Los Parquet ya escritos (consolidados en ADLS) tienen columnas no anulables: deben
    // seguir leyéndose igual después del cambio.
    [Fact]
    public async Task Read_LegacyNonNullableFile_StillReturnsValues()
    {
        var schema = new CdmEntitySchema
        {
            EntityName = "t",
            Attributes =
            [
                new CdmAttribute { Name = "Id", SqlType = "UNIQUEIDENTIFIER", IsNullable = false },
                new CdmAttribute { Name = "Qty", SqlType = "BIGINT" },
            ],
        };
        var id = Guid.NewGuid();
        var idField = new Parquet.Schema.DataField<Guid>("Id");
        var qtyField = new Parquet.Schema.DataField<long>("Qty");

        using var ms = new MemoryStream();
        await using (var writer = await Parquet.ParquetWriter.CreateAsync(new Parquet.Schema.ParquetSchema(idField, qtyField), ms))
        {
            using var group = writer.CreateRowGroup();
            await group.WriteAsync<Guid>(idField, new[] { id }.AsMemory());
            await group.WriteAsync<long>(qtyField, new[] { 7L }.AsMemory());
        }

        var rows = new List<Dictionary<string, object?>>();
        await foreach (var row in ParquetMaterializer.ReadStreamingAsync(new MemoryStream(ms.ToArray()), schema))
        {
            rows.Add(row);
        }

        var single = Assert.Single(rows);
        Assert.Equal(id, single["Id"]);
        Assert.Equal(7L, single["Qty"]);
    }
}