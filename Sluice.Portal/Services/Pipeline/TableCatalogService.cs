using System.Text.Json;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Pipeline;

public sealed record CatalogTable(string Name, bool Included, bool Loaded, long RowCount, int ExcludedColumns);

public sealed record CatalogColumn(string Name, string SqlType, bool Required, bool Included);

// Catálogo de tablas de un pipeline: qué hay en el lago y qué se carga de cada tabla, antes o
// después de la primera ejecución. Lee el lago sin lanzar jobs: los esquemas del último snapshot
// cerrado y de la raíz, las carpetas de ese snapshot y las tablas ya cargadas. Incluir o excluir
// una tabla es la pausa de siempre (TablePauseStore): una lista de exclusión, así que una tabla
// nueva en el lago entra sola. Las columnas excluidas solo dejan de cargarse en SQL.
public sealed class TableCatalogService
{
    private readonly IPipelineService _pipelines;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly PipelineStateStore _stateStore;
    private readonly TablePauseStore _pauses;
    private readonly ColumnSelectionStore _columns;

    public TableCatalogService(
        IPipelineService pipelines,
        IAdlsClientFactory adlsFactory,
        PipelineStateStore stateStore,
        TablePauseStore pauses,
        ColumnSelectionStore columns)
    {
        _pipelines = pipelines;
        _adlsFactory = adlsFactory;
        _stateStore = stateStore;
        _pauses = pauses;
        _columns = columns;
    }

    public async Task<IReadOnlyList<CatalogTable>> ListTablesAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        var pipeline = Get(pipelineId);
        var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
        var latest = await LatestClosedSnapshotAsync(adls, cancellationToken);

        var names = new HashSet<string>(await adls.ListSchemaEntitiesAsync(null, cancellationToken), StringComparer.OrdinalIgnoreCase);
        if (latest is not null)
        {
            names.UnionWith(await adls.ListSchemaEntitiesAsync(latest, cancellationToken));
            names.UnionWith(await adls.ListEntitiesAsync(latest, cancellationToken));
        }

        var state = _stateStore.GetOrCreate(pipelineId);
        names.UnionWith(state.Tables.Keys);
        var paused = _pauses.GetPaused(pipelineId);
        var excluded = _columns.GetAll(pipelineId);

        return names
            .Where(n => !ExcludedEntities.IsExcluded(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new CatalogTable(
                n,
                Included: !paused.Contains(n),
                Loaded: state.Tables.ContainsKey(n),
                RowCount: state.Tables.GetValueOrDefault(n)?.RowCount ?? 0,
                ExcludedColumns: excluded.GetValueOrDefault(n)?.Count ?? 0))
            .ToList();
    }

    // Columnas del esquema: primero el del último snapshot cerrado y el del consolidado (una lectura
    // cada uno); solo si faltan los dos, la búsqueda completa del lago, que prueba snapshot a snapshot.
    public async Task<IReadOnlyList<CatalogColumn>> ListColumnsAsync(string pipelineId, string entity, CancellationToken cancellationToken = default)
    {
        var pipeline = Get(pipelineId);
        var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
        var latest = await LatestClosedSnapshotAsync(adls, cancellationToken);
        CdmEntitySchema schema;
        if (latest is not null && (await adls.ListSchemaEntitiesAsync(latest, cancellationToken)).Contains(entity, StringComparer.OrdinalIgnoreCase))
        {
            schema = await adls.GetEntitySchemaAsync(entity, latest, latest, cancellationToken);
        }
        else
        {
            var store = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
            var json = await store.ReadTextAsync(IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity), cancellationToken);
            schema = json is not null
                ? JsonSerializer.Deserialize<CdmEntitySchema>(json, JsonDefaults.Options) ?? throw new InvalidOperationException($"Esquema no válido para {entity}.")
                : await adls.GetEntitySchemaAsync(entity, latest ?? "", latest, cancellationToken);
        }

        var excluded = _columns.GetExcluded(pipelineId, entity);
        return schema.Attributes
            .Select(a => new CatalogColumn(a.Name, a.SqlType, ColumnSelection.IsRequired(a.Name), !excluded.Contains(a.Name)))
            .ToList();
    }

    public void SetTableIncluded(string pipelineId, string entity, bool included) =>
        _pauses.SetPaused(Get(pipelineId).Id, entity, paused: !included);

    // Devuelve las columnas que vuelven a cargarse: SQL solo las rellena al re-sincronizar la tabla.
    public IReadOnlyList<string> SetExcludedColumns(string pipelineId, string entity, IEnumerable<string> columns) =>
        _columns.SetExcluded(Get(pipelineId).Id, entity, columns);

    private Models.Pipeline Get(string pipelineId) =>
        _pipelines.Get(pipelineId) ?? throw new InvalidOperationException("Pipeline not found.");

    // Última carpeta cerrada: la anterior a la que el changelog marca en escritura (SnapshotSelector).
    private static async Task<string?> LatestClosedSnapshotAsync(IAdlsClient adls, CancellationToken cancellationToken)
    {
        var writing = await adls.GetSettledSnapshotAsync(cancellationToken);
        var folders = await adls.ListSnapshotFoldersAsync(cancellationToken);
        return folders
            .Where(f => string.IsNullOrWhiteSpace(writing) || string.CompareOrdinal(f, writing) < 0)
            .OrderBy(f => f, StringComparer.Ordinal)
            .LastOrDefault();
    }
}
