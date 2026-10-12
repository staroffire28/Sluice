using System.Globalization;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Pipeline;

// Comparación de valores entre el parquet consolidado y la tabla SQL. Cada valor se normaliza a como
// SQL lo guarda (las mismas conversiones que el MERGE: SqlMergeExecutor.CoerceForSql + tipo de la
// columna), cada fila se resume en un hash de 64 bits y las filas se emparejan por Id.
//
// Memoria: el lado parquet queda en un índice Id → hash (un Guid y un ulong por fila, ~40 bytes;
// ~0,4–0,5 GB para 11 M de filas). Se compara una tabla cada vez.
public static class ValueComparer
{
    public const int SampleSize = 20;

    public static async Task<ValueCompareEntityResult> CompareAsync(
        string entity,
        IReadOnlyList<CdmAttribute> columns,
        IAsyncEnumerable<Dictionary<string, object?>> parquetRows,
        IAsyncEnumerable<object?[]> sqlRows,
        CancellationToken cancellationToken = default)
    {
        var idIndex = IndexOfId(columns);
        if (idIndex < 0)
        {
            return new ValueCompareEntityResult { Entity = entity, Error = "La tabla no tiene columna Id: no se pueden emparejar las filas." };
        }

        var idType = columns[idIndex].SqlType;
        var index = new KeyIndex();
        long sourceRows = 0;
        var values = new object?[columns.Count];
        await foreach (var row in parquetRows.WithCancellation(cancellationToken))
        {
            sourceRows++;
            for (var i = 0; i < columns.Count; i++)
            {
                values[i] = row.TryGetValue(columns[i].Name, out var v) ? v : null;
            }

            if (values[idIndex] is { } id)
            {
                index.TryAdd(id, idType, RowHash(columns, values));
            }
        }

        long destinationRows = 0, matching = 0, different = 0, extra = 0;
        var sampleDifferent = new List<string>();
        var sampleExtra = new List<string>();
        await foreach (var row in sqlRows.WithCancellation(cancellationToken))
        {
            destinationRows++;
            var id = row[idIndex];
            if (id is null)
            {
                extra++;
                continue;
            }

            if (!index.TryRemove(id, idType, out var expected))
            {
                // No está en el parquet, o es un Id repetido en SQL (el primero ya se emparejó).
                extra++;
                AddSample(sampleExtra, Canonical(id, idType));
            }
            else if (expected == RowHash(columns, row))
            {
                matching++;
            }
            else
            {
                different++;
                AddSample(sampleDifferent, Canonical(id, idType));
            }
        }

        return new ValueCompareEntityResult
        {
            Entity = entity,
            SourceRows = sourceRows,
            DestinationRows = destinationRows,
            Matching = matching,
            Different = different,
            MissingInDestination = index.Count,
            ExtraInDestination = extra,
            SampleDifferent = sampleDifferent,
            SampleMissing = index.Remaining(SampleSize).ToList(),
            SampleExtra = sampleExtra,
            ComparedColumns = columns.Count,
        };
    }

    // Forma canónica de un valor según el tipo de la columna destino; null = NULL en SQL.
    public static string? Canonical(object? value, string sqlType)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (Is(sqlType, "UNIQUEIDENTIFIER"))
        {
            return (value is Guid g ? g : Guid.Parse(value.ToString()!)).ToString("D");
        }

        if (Is(sqlType, "DATETIME"))
        {
            // DATETIME2(7) y DATETIMEOFFSET(7) guardan el tick exacto. El MERGE envía siempre un
            // DateTime (CoerceForSql: DateTimeOffset → UtcDateTime); en una columna DATETIMEOFFSET
            // SQL conserva esa hora de reloj, así que al leerla se compara su DateTime, no el UTC.
            var ticks = value switch
            {
                DateTimeOffset dto => dto.DateTime.Ticks,
                DateTime dt => dt.Ticks,
                _ => DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture).Ticks,
            };
            return ticks.ToString(CultureInfo.InvariantCulture);
        }

        if (Is(sqlType, "BIT"))
        {
            return (value is bool b ? b : value.ToString() is "1" or "true") ? "1" : "0";
        }

        if (Is(sqlType, "BIGINT") || Is(sqlType, "INT") || Is(sqlType, "SMALLINT"))
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        if (Is(sqlType, "DECIMAL"))
        {
            // SQL redondea al scale de la columna (mitades hacia fuera) y 1.5 = 1.50.
            var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (DecimalScale(sqlType) is { } scale)
            {
                d = Math.Round(d, scale, MidpointRounding.AwayFromZero);
            }

            return (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
        }

        if (Is(sqlType, "FLOAT"))
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    // FNV-1a de 64 bits sobre las formas canónicas, con separador y marca de NULL (NULL ≠ "").
    public static ulong RowHash(IReadOnlyList<CdmAttribute> columns, IReadOnlyList<object?> values)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        for (var i = 0; i < columns.Count; i++)
        {
            var text = Canonical(values[i], columns[i].SqlType);
            if (text is null)
            {
                hash = (hash ^ 0xFFFF) * prime;
            }
            else
            {
                foreach (var c in text)
                {
                    hash = (hash ^ c) * prime;
                }
            }

            hash = (hash ^ 0x1F) * prime;
        }

        return hash;
    }

    private static int IndexOfId(IReadOnlyList<CdmAttribute> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, "Id", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool Is(string sqlType, string prefix) => sqlType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static int? DecimalScale(string sqlType)
    {
        var open = sqlType.IndexOf('(');
        var comma = sqlType.IndexOf(',');
        var close = sqlType.IndexOf(')');
        return open >= 0 && comma > open && close > comma
            && int.TryParse(sqlType.AsSpan(comma + 1, close - comma - 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale)
            ? scale
            : null;
    }

    private static void AddSample(List<string> sample, string? id)
    {
        if (sample.Count < SampleSize && id is not null)
        {
            sample.Add(id);
        }
    }

    // Id → hash. Los Id GUID (el caso de Synapse Link) se guardan como Guid (16 bytes) en vez de texto.
    private sealed class KeyIndex
    {
        private readonly Dictionary<Guid, ulong> _guids = [];
        private readonly Dictionary<string, ulong> _others = new(StringComparer.Ordinal);

        public long Count => _guids.Count + _others.Count;

        public void TryAdd(object id, string sqlType, ulong hash)
        {
            if (AsGuid(id) is { } g)
            {
                _guids.TryAdd(g, hash);
            }
            else
            {
                _others.TryAdd(Canonical(id, sqlType)!, hash);
            }
        }

        public bool TryRemove(object id, string sqlType, out ulong hash) =>
            AsGuid(id) is { } g
                ? _guids.Remove(g, out hash)
                : _others.Remove(Canonical(id, sqlType)!, out hash);

        public IEnumerable<string> Remaining(int take) =>
            _guids.Keys.Select(g => g.ToString("D")).Concat(_others.Keys).Take(take);

        private static Guid? AsGuid(object id) =>
            id is Guid g ? g : id is string s && Guid.TryParse(s, out var parsed) ? parsed : null;
    }
}
