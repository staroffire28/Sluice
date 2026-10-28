using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
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
    Task<PipelineResetResult> ResetAsync(AuditActor actor, string pipelineId, bool truncateSql, CancellationToken cancellationToken = default);
}

public sealed class PipelineResetService : IPipelineResetService
{
    private readonly IPipelineService _pipelines;
    private readonly PipelineStateStore _stateStore;
    private readonly IParquetArtifactService _parquet;
    private readonly ISqlConnectionFactory _sqlFactory;
    private readonly ISqlMergeExecutor _mergeExecutor;
    private readonly IJobService _jobs;
    private readonly ITaskStore _tasks;
    private readonly RunQueueService? _runQueue;
    private readonly IAuditLog _audit;
    private readonly AppOptions _options;

    public PipelineResetService(
        IOptions<AppOptions> options,
        IPipelineService pipelines,
        PipelineStateStore stateStore,
        IParquetArtifactService parquet,
        ISqlConnectionFactory sqlFactory,
        ISqlMergeExecutor mergeExecutor,
        IJobService jobs,
        ITaskStore tasks,
        IAuditLog audit,
        RunQueueService? runQueue = null)
    {
        _audit = audit;
        _options = options.Value;
        _runQueue = runQueue;
        _pipelines = pipelines;
        _stateStore = stateStore;
        _parquet = parquet;
        _sqlFactory = sqlFactory;
        _mergeExecutor = mergeExecutor;
        _jobs = jobs;
        _tasks = tasks;
    }

    // Una entrada para todo el reset (no una por parquet borrado). Un truncado que falla en alguna
    // tabla detiene el reset antes de borrar nada: queda como error, con las tablas que fallaron.
    public Task<PipelineResetResult> ResetAsync(AuditActor actor, string pipelineId, bool truncateSql, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "pipeline.reset",
            pipelineId,
            () => ResetCoreAsync(actor, pipelineId, truncateSql, cancellationToken),
            result => result.Completed
                ? new AuditResult(
                    $"Pipeline {pipelineId} reseteado{(truncateSql ? " con truncado de SQL" : "")} " +
                    $"({result.TablesTruncated.Count} tabla(s) truncada(s), {result.ParquetArtifactsDeleted} parquet(s) borrado(s)).",
                    MessageKey: "Activity.Reset.Done",
                    MessageArgs: [pipelineId, truncateSql ? new EventPart("Activity.Reset.WithTruncate") : "", result.TablesTruncated.Count, result.ParquetArtifactsDeleted])
                : new AuditResult(
                    $"Reset de {pipelineId} detenido: fallo al truncar {string.Join(", ", result.TableTruncateFailures.Select(f => f.Entity))}.",
                    Outcome: AuditOutcome.Error,
                    MessageKey: "Activity.Reset.Stopped",
                    MessageArgs: [pipelineId, string.Join(", ", result.TableTruncateFailures.Select(f => f.Entity))]),
            $"No se pudo resetear {pipelineId}",
            failureKey: "Activity.Failed.Reset", failureArgs: [pipelineId]);

    private async Task<PipelineResetResult> ResetCoreAsync(AuditActor actor, string pipelineId, bool truncateSql, CancellationToken cancellationToken)
    {
        _ = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{pipelineId}' not found.");

        // Mismo criterio que Run/Resync: resetear bajo un job en curso truncaría tablas y borraría
        // parquets que el job está usando, y al terminar este pisaría el estado reseteado. El lock de
        // ejecución dura todo el reset: sin él, lo encolado o el auto-run arrancaban a mitad y su
        // ingest guardaba un estado avanzado sobre parquets borrados y SQL truncado.
        using var runLock = await PipelineRunLock.AcquireAsync(_options.DataDir, pipelineId, PipelineRunLock.DefaultTimeout, cancellationToken);
        var pipeline = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{pipelineId}' not found.");
        await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId,
            "Hay un job en curso para este pipeline: espera a que termine o cancélalo antes de resetear.", "Error.PipelineBusyReset", cancellationToken);

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
            if (await _parquet.DeleteUnauditedAsync(pipelineId, artifact.Path, cancellationToken))
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
            if (await _parquet.DeleteUnauditedAsync(pipelineId, schemaPath, cancellationToken))
            {
                deleted++;
            }
        }

        // Las versiones anteriores del consolidado describen la historia que el reset descarta.
        deleted += await _parquet.DeleteVersionsAsync(pipelineId, cancellationToken);

        _stateStore.Save(new PipelineState { PipelineId = pipelineId });

        // Lo pedido antes del reset contaba con el estado que se acaba de descartar. Solo aquí: un
        // reset que falla al truncar no descarta nada y conserva lo encolado.
        _runQueue?.Discard(actor, pipelineId, "el pipeline se reseteó");

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
