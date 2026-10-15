using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services.Stores;

// Columnas que NO se cargan en SQL, por pipeline y tabla. Lista de exclusión: una columna nueva
// del origen entra sola. El parquet consolidado se guarda siempre completo; el filtro solo se
// aplica al cargar SQL (y al comparar valores), así que volver a incluir una columna no exige
// reingerir: basta re-sincronizar la tabla. Vive junto a paused_tables.json, como la pausa.
public sealed class ColumnSelectionStore
{
    private readonly AppOptions _options;
    private readonly object _lock = new();

    public ColumnSelectionStore(IOptions<AppOptions> options) => _options = options.Value;

    private sealed record ExcludedColumns
    {
        public Dictionary<string, HashSet<string>> Tables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> GetExcluded(string pipelineId, string entity)
    {
        lock (_lock)
        {
            return Read(pipelineId).Tables.GetValueOrDefault(entity) ?? [];
        }
    }

    public IReadOnlyDictionary<string, HashSet<string>> GetAll(string pipelineId)
    {
        lock (_lock)
        {
            return Read(pipelineId).Tables;
        }
    }

    // Guarda la exclusión de una tabla (las columnas obligatorias se ignoran) y devuelve las que
    // estaban excluidas y vuelven a cargarse.
    public IReadOnlyList<string> SetExcluded(string pipelineId, string entity, IEnumerable<string> columns)
    {
        lock (_lock)
        {
            var current = Read(pipelineId);
            var previous = current.Tables.GetValueOrDefault(entity) ?? [];
            var excluded = new HashSet<string>(columns.Where(c => !ColumnSelection.IsRequired(c)), StringComparer.OrdinalIgnoreCase);
            var tables = new Dictionary<string, HashSet<string>>(current.Tables, StringComparer.OrdinalIgnoreCase);
            if (excluded.Count == 0)
            {
                tables.Remove(entity);
            }
            else
            {
                tables[entity] = excluded;
            }

            Write(pipelineId, new ExcludedColumns { Tables = tables });
            return previous.Where(c => !excluded.Contains(c)).ToList();
        }
    }

    private ExcludedColumns Read(string pipelineId)
    {
        var path = GetPath(pipelineId);
        if (!File.Exists(path))
        {
            return new ExcludedColumns();
        }

        using var stream = File.OpenRead(path);
        var raw = JsonSerializer.Deserialize<ExcludedColumns>(stream, JsonDefaults.Options);

        // Re-normaliza sin distinguir mayúsculas (el deserializado usa el comparador por defecto).
        return new ExcludedColumns
        {
            Tables = (raw?.Tables ?? []).ToDictionary(
                t => t.Key,
                t => new HashSet<string>(t.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    private void Write(string pipelineId, ExcludedColumns value)
    {
        Directory.CreateDirectory(Path.Combine(_options.DataDir, pipelineId, "state"));
        AtomicJsonFile.Write(GetPath(pipelineId), value);
    }

    private string GetPath(string pipelineId) =>
        Path.Combine(_options.DataDir, pipelineId, "state", "excluded_columns.json");
}

public static class ColumnSelection
{
    // Sin ellas no hay MERGE (Id), ni desempate de versiones (SinkModifiedOn, versionnumber), ni
    // borrados de F&O (IsDelete): nunca se pueden excluir.
    private static readonly HashSet<string> Required = new(StringComparer.OrdinalIgnoreCase)
    {
        CdmSchemaParser.IdColumn,
        CdmSchemaParser.WatermarkColumn,
        CdmSchemaParser.VersionColumn,
        CdmSchemaParser.IsDeleteColumn,
    };

    public static bool IsRequired(string column) => Required.Contains(column);

    // El schema sin las columnas excluidas (las obligatorias se quedan siempre).
    public static CdmEntitySchema Apply(CdmEntitySchema schema, IReadOnlySet<string> excluded) =>
        excluded.Count == 0
            ? schema
            : schema with { Attributes = schema.Attributes.Where(a => IsRequired(a.Name) || !excluded.Contains(a.Name)).ToList() };
}
