using System.Text.Json;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

// Lectura tipada de Job.Summary: en memoria los executors guardan int/long/double/string, pero
// un job leído de disco trae cada valor como JsonElement. Devuelve null si falta o no encaja.
public static class JobSummary
{
    public static int? GetInt(Job job, string key) =>
        GetLong(job, key) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    public static long? GetLong(Job job, string key) =>
        job.Summary.GetValueOrDefault(key) switch
        {
            int i => i,
            long l => l,
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var l) => l,
            _ => null,
        };

    public static double? GetDouble(Job job, string key) =>
        job.Summary.GetValueOrDefault(key) switch
        {
            int i => i,
            long l => l,
            double d => d,
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
            _ => null,
        };

    public static string? GetString(Job job, string key) =>
        job.Summary.GetValueOrDefault(key) switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => null,
        };
}
