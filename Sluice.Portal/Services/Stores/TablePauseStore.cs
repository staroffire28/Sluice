using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services.Stores;

// Conjunto de tablas con la sincronización PAUSADA, por pipeline. Ausencia = activa
// (default habilitada), así no hay que migrar estado existente ni declarar tablas.
// Vive junto a pipeline_state.json en el DataDir compartido: en modo distribuido todos
// los workers leen la misma lista sin coordinación extra. Es un store aparte (no un campo
// en TableState) porque TableState se sobrescribe entero en cada ingest y un flag ahí se
// perdería en la siguiente corrida.
public sealed class TablePauseStore
{
    private readonly AppOptions _options;
    private readonly object _lock = new();

    public TablePauseStore(IOptions<AppOptions> options) => _options = options.Value;

    private sealed record PausedTables
    {
        public HashSet<string> Paused { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> GetPaused(string pipelineId)
    {
        lock (_lock)
        {
            return Read(pipelineId).Paused;
        }
    }

    public bool IsPaused(string pipelineId, string entity)
    {
        lock (_lock)
        {
            return Read(pipelineId).Paused.Contains(entity);
        }
    }

    public void SetPaused(string pipelineId, string entity, bool paused)
    {
        lock (_lock)
        {
            var current = Read(pipelineId);
            var set = new HashSet<string>(current.Paused, StringComparer.OrdinalIgnoreCase);
            if (paused)
            {
                set.Add(entity);
            }
            else
            {
                set.Remove(entity);
            }

            Write(pipelineId, new PausedTables { Paused = set });
        }
    }

    private PausedTables Read(string pipelineId)
    {
        var path = GetPath(pipelineId);
        if (!File.Exists(path))
        {
            return new PausedTables();
        }

        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<PausedTables>(stream, JsonDefaults.Options);
        if (raw is null)
        {
            return new PausedTables();
        }

        // Re-normaliza con comparador case-insensitive (el deserializado de HashSet usa el
        // comparador por defecto, sensible a mayúsculas).
        return new PausedTables { Paused = new HashSet<string>(raw.Paused, StringComparer.OrdinalIgnoreCase) };
    }

    private void Write(string pipelineId, PausedTables value)
    {
        Directory.CreateDirectory(GetDirectory(pipelineId));
        // Escritura atómica (temp + rename): un crash a mitad no deja el JSON truncado,
        // que rompería GetPaused (lo lee el ingest worker) en la siguiente corrida.
        AtomicJsonFile.Write(GetPath(pipelineId), value);
    }

    private string GetDirectory(string pipelineId) =>
        Path.Combine(_options.DataDir, pipelineId, "state");

    private string GetPath(string pipelineId) =>
        Path.Combine(GetDirectory(pipelineId), "paused_tables.json");
}
