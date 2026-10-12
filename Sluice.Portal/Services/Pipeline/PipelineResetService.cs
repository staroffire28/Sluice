using Sluice.Portal.Models;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Pipeline;

// Un fallo por tabla al truncar SQL (permisos, tabla bloqueada, etc.).
public sealed record PipelineResetSqlFailure(string Entity, string Error);

public sealed record PipelineResetResult
{
    public required string PipelineId { get; init; }

    // false solo cuando truncateSql=true y alguna tabla falló al truncar: en ese caso NO se
    // tocaron parquets ni estado (ver PipelineResetService.ResetAsync) y el operador debe
    // resolver el problema de SQL antes de reintentar.
    public bool Completed { get; init; }
    public bool StateReset { get; init; }
    public int ParquetArtifactsDeleted { get; init; }
    public IReadOnlyList<string> TablesTruncated { get; init; } = [];
    public IReadOnlyList<PipelineResetSqlFailure> TableTruncateFailures { get; init; } = [];
}

public interface IPipelineResetService
{
    // Resetea un pipeline para pruebas desde cero: limpia LastProcessedSnapshot/estado de tablas
    // y borra los parquets consolidados en ADLS (fuerza re-ingesta completa en el próximo Run).
    // Si truncateSql=true, además vacía las tablas de destino en SQL para las entidades que el
    // pipeline llegó a cargar — evita arrastrar datos de una prueba anterior. NO toca la
    // configuración del pipeline (Source/Destination/Name) ni las pausas de sincronización
    // (TablePauseStore), que son preferencia del usuario, no datos de prueba.
    Task<PipelineResetResult> ResetAsync(string pipelineId, bool truncateSql, CancellationToken cancellationToken = default);
}

public sealed class PipelineResetService : IPipelineResetService
{
    private readonly IPipelineService _pipelines;
    private readonly PipelineStateStore _stateStore;
    private readonly IParquetArtifactService _parquet;
    private readonly ISqlConnectionFactory _sqlFactory;
    private readonly ISqlMergeExecutor _mergeExecutor;
    private readonly IJobService _jobs;

    public PipelineResetService(
        IPipelineService pipelines,
        PipelineStateStore stateStore,
        IParquetArtifactService parquet,
        ISqlConnectionFactory sqlFactory,
        ISqlMergeExecutor mergeExecutor,
        IJobService jobs)
    {
        _pipelines = pipelines;
        _stateStore = stateStore;
        _parquet = parquet;
        _sqlFactory = sqlFactory;
        _mergeExecutor = mergeExecutor;
        _jobs = jobs;
    }

    public async Task<PipelineResetResult> ResetAsync(string pipelineId, bool truncateSql, CancellationToken cancellationToken = default)
    {
        var pipeline = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{pipelineId}' not found.");

        // Mismo criterio que Run/Resync: resetear bajo un job en curso truncaría tablas y borraría
        // parquets que el job está usando, y al terminar este pisaría el estado reseteado.
        if (_jobs.ListRunning().Any(j => j.PipelineId == pipelineId))
        {
            throw new InvalidOperationException(
                "Hay un job en curso para este pipeline: espera a que termine o cancélalo antes de resetear.");
        }

        // Entidades a truncar: las que el pipeline ya llegó a cargar (PipelineState.Tables),
        // tomadas ANTES de tocar nada — si truncar falla a medio camino, el estado sigue
        // reflejando la realidad para poder reintentar.
        var state = _stateStore.GetOrCreate(pipelineId);
        var entities = state.Tables.Keys.ToList();

        var tablesTruncated = new List<string>();
        var truncateFailures = new List<PipelineResetSqlFailure>();

        if (truncateSql && entities.Count > 0)
        {
            var schema = _sqlFactory.GetSchema(pipeline.DestinationId);
            await using var connection = await _sqlFactory.OpenAsync(pipeline.DestinationId, cancellationToken);

            foreach (var entity in entities)
            {
                try
                {
                    if (await _mergeExecutor.TruncateTableAsync(connection, schema, entity, cancellationToken))
                    {
                        tablesTruncated.Add(entity);
                    }
                }
                catch (Exception ex)
                {
                    // Best-effort por tabla (mismo criterio de aislamiento que IngestExecutor/
                    // SqlExecutor), pero un fallo aquí SÍ detiene el resto del reset — ver el
                    // corte más abajo: no queremos borrar parquets/estado mientras SQL sigue con
                    // datos de la prueba anterior (la duplicidad que el reset busca evitar).
                    truncateFailures.Add(new PipelineResetSqlFailure(entity, $"{ex.GetType().Name}: {ex.Message}"));
                }
            }

            if (truncateFailures.Count > 0)
            {
                return new PipelineResetResult
                {
                    PipelineId = pipelineId,
                    Completed = false,
                    TablesTruncated = tablesTruncated,
                    TableTruncateFailures = truncateFailures,
                };
            }
        }

        var artifacts = await _parquet.ListAsync(pipelineId, cancellationToken);
        var deleted = 0;
        foreach (var artifact in artifacts)
        {
            if (await _parquet.DeleteAsync(pipelineId, artifact.Path, cancellationToken))
            {
                deleted++;
            }
        }

        // ListAsync solo devuelve *.parquet (ver AdlsClient.ListParquetAsync) — el sidecar de
        // schema ({entity}.schema.json) se borra aparte, mismo criterio de descubrimiento de
        // entidades por nombre de archivo que usa Force (OrchestrationService.EnqueueParquetReloadAsync):
        // un solo nivel bajo el prefijo del pipeline ({pipelineId}/{entity}.parquet).
        var entitiesFromParquet = artifacts
            .Where(a => a.Path.Count(c => c == '/') == 1 && a.Path.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
            .Select(a => Path.GetFileNameWithoutExtension(a.Path.Split('/').Last()));

        foreach (var entity in entitiesFromParquet)
        {
            var schemaPath = IngestManifestStore.GetConsolidatedSchemaBlobPath(pipelineId, entity);
            if (await _parquet.DeleteAsync(pipelineId, schemaPath, cancellationToken))
            {
                deleted++;
            }
        }

        _stateStore.Save(new PipelineState { PipelineId = pipelineId });

        return new PipelineResetResult
        {
            PipelineId = pipelineId,
            Completed = true,
            StateReset = true,
            ParquetArtifactsDeleted = deleted,
            TablesTruncated = tablesTruncated,
        };
    }
}
