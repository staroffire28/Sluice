using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Pipeline;

// Unifica varias fuentes parquet (transitorios por snapshot + el consolidado previo en
// incrementales) en UN parquet por tabla, quedándose con la última versión por Id
// (máximo SinkModifiedOn; a igual SinkModifiedOn, máximo versionnumber si la tabla lo trae)
// — en streaming.
//
// Dos pasadas para no cargar las filas anchas en memoria:
//   Pasada 1: leer solo Id + watermark + versión (proyección columnar) → máximo por Id.
//   Pasada 2: leer filas completas row-group a row-group; emitir la primera fila por Id
//             cuyo (watermark, versión) == el máximo, en lotes (row groups) a la salida.
// Pico de memoria = mapa Id→watermark (ver KeyWatermarks), NO el dataset entero.
//
// Cada fuente se LEE DOS VECES, así que se recibe como factory que abre un stream fresco.
public static class ParquetConsolidator
{
    // RowsWithoutId: filas descartadas por no traer un Id válido (vacío o no parseable en el CSV).
    // DeleteRows: de las Rows escritas, cuántas son filas de borrado de F&O (IsDelete = True).
    public sealed record ConsolidationResult(int Rows, DateTimeOffset? MaxWatermark, int RowsWithoutId = 0, int DeleteRows = 0);

    public static async Task<ConsolidationResult> ConsolidateAsync(
        IReadOnlyList<Func<CancellationToken, Task<Stream>>> sourcesNewestFirst,
        CdmEntitySchema schema,
        Stream destination,
        int batchSize = 5000,
        CancellationToken cancellationToken = default)
    {
        const string idCol = CdmSchemaParser.IdColumn;
        const string wmCol = CdmSchemaParser.WatermarkColumn;
        const string versionCol = CdmSchemaParser.VersionColumn;

        // Pasada 1: máximo (watermark, versión) por Id.
        var maxWatermark = new KeyWatermarks();
        var rowsWithoutId = 0;
        foreach (var open in sourcesNewestFirst)
        {
            await using var stream = await open(cancellationToken);
            await foreach (var (id, wm, version) in ParquetMaterializer.ReadKeyWatermarksAsync(stream, idCol, wmCol, versionCol, cancellationToken))
            {
                if (!maxWatermark.Max(id, ParseWatermark(wm), ParseVersion(version)))
                {
                    rowsWithoutId++;
                }
            }
        }

        var rowsWritten = 0;
        var deleteRows = 0;
        DateTimeOffset? globalMax = null;

        async IAsyncEnumerable<IReadOnlyList<Dictionary<string, object?>>> WinningBatches(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var batch = new List<Dictionary<string, object?>>(batchSize);
            foreach (var open in sourcesNewestFirst)
            {
                await using var stream = await open(ct);
                await foreach (var row in ParquetMaterializer.ReadStreamingAsync(stream, schema, ct))
                {
                    // Gana la primera fila (fuentes de la más nueva a la más vieja) con el máximo
                    // (watermark, versión) de su Id; TryTake la saca del mapa, así que las demás se
                    // descartan.
                    var rowWm = ParseWatermark(row.TryGetValue(wmCol, out var wmv) ? wmv : null);
                    var rowVersion = ParseVersion(row.TryGetValue(versionCol, out var vv) ? vv : null);
                    if (maxWatermark.TryTake(row.TryGetValue(idCol, out var idv) ? idv : null, rowWm, rowVersion))
                    {
                        rowsWritten++;
                        if (DeleteRows.IsDeleteRow(row))
                        {
                            deleteRows++;
                        }

                        if (CompareWatermark(rowWm, globalMax) > 0)
                        {
                            globalMax = rowWm;
                        }

                        batch.Add(row);
                        if (batch.Count >= batchSize)
                        {
                            yield return batch;
                            batch = new List<Dictionary<string, object?>>(batchSize);
                        }
                    }
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
        }

        await ParquetMaterializer.WriteStreamingAsync(destination, schema, WinningBatches(cancellationToken), cancellationToken);
        return new ConsolidationResult(rowsWritten, globalMax, rowsWithoutId, deleteRows);
    }

    private static DateTimeOffset? ParseWatermark(object? value) => value switch
    {
        null => null,
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        string s when DateTimeOffset.TryParse(s, out var parsed) => parsed,
        _ => null,
    };

    private static long? ParseVersion(object? value) => value switch
    {
        null => null,
        long l => l,
        int i => i,
        string s when long.TryParse(s, out var parsed) => parsed,
        _ => null,
    };

    // Máximo (watermark, versión) por Id, compacto: con 11 M de filas, un
    // Dictionary<string, DateTimeOffset?> más un HashSet<string> de escritos llegaba a ~4 GB (dos
    // strings de GUID por fila). Los Id GUID (todos los de Dataverse) van como Guid y el watermark
    // como ticks UTC más la versión (~40 B por Id); los Id de texto, sin distinguir mayúsculas como
    // antes. Escrito = quitado del mapa.
    private sealed class KeyWatermarks
    {
        private const long Missing = long.MinValue;

        private readonly Dictionary<Guid, (long Ticks, long Version)> _guids = new();
        private readonly Dictionary<string, (long Ticks, long Version)> _texts = new(StringComparer.OrdinalIgnoreCase);

        // false si la fila no trae Id.
        public bool Max(object? id, DateTimeOffset? watermark, long? version)
        {
            var stamp = Stamp(watermark, version);
            if (TryGuid(id, out var guid))
            {
                ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_guids, guid, out var exists);
                slot = exists && slot.CompareTo(stamp) >= 0 ? slot : stamp;
                return true;
            }

            var text = id?.ToString();
            if (text is null)
            {
                return false;
            }

            ref var textSlot = ref CollectionsMarshal.GetValueRefOrAddDefault(_texts, text, out var textExists);
            textSlot = textExists && textSlot.CompareTo(stamp) >= 0 ? textSlot : stamp;
            return true;
        }

        // true (y lo quita) si el Id sigue pendiente y este es su (watermark, versión) máximo.
        public bool TryTake(object? id, DateTimeOffset? watermark, long? version)
        {
            var stamp = Stamp(watermark, version);
            if (TryGuid(id, out var guid))
            {
                if (!_guids.TryGetValue(guid, out var max) || max != stamp)
                {
                    return false;
                }

                return _guids.Remove(guid);
            }

            var text = id?.ToString();
            if (text is null || !_texts.TryGetValue(text, out var textMax) || textMax != stamp)
            {
                return false;
            }

            return _texts.Remove(text);
        }

        // Orden lexicográfico de la tupla: manda el watermark; la versión solo desempata.
        private static (long Ticks, long Version) Stamp(DateTimeOffset? watermark, long? version) =>
            (watermark?.UtcTicks ?? Missing, version ?? Missing);

        private static bool TryGuid(object? id, out Guid guid)
        {
            switch (id)
            {
                case Guid g:
                    guid = g;
                    return true;
                case string s when Guid.TryParse(s, out guid):
                    return true;
                default:
                    guid = default;
                    return false;
            }
        }
    }

    // null = mínimo (una fila sin watermark pierde frente a cualquiera con watermark).
    private static int CompareWatermark(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        return a.Value.CompareTo(b.Value);
    }
}
