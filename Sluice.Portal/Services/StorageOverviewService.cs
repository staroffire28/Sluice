using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public sealed record PipelineStorageStats(
    string PipelineId,
    string PipelineName,
    int SnapshotFolderCount,
    int ParquetFileCount,
    long ParquetBytes,
    int EligibleSnapshotPurgeCount,
    bool ParquetCertified,
    bool Unavailable = false);

public sealed record StorageOverviewResult(
    IReadOnlyList<PipelineStorageStats> Pipelines,
    int TotalSnapshotFolders,
    long TotalParquetBytes,
    int TotalEligiblePurge);

public interface IStorageOverviewService
{
    Task<StorageOverviewResult> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<PipelineStorageStats?> GetPipelineAsync(string pipelineId, CancellationToken cancellationToken = default);
}

public sealed class StorageOverviewService : IStorageOverviewService
{
    private readonly IPipelineService _pipelines;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly IParquetArtifactService _parquet;
    private readonly SettingsStore _settings;
    private readonly ILogger<StorageOverviewService> _logger;

    public StorageOverviewService(
        IPipelineService pipelines,
        IAdlsClientFactory adlsFactory,
        IParquetArtifactService parquet,
        SettingsStore settings,
        ILogger<StorageOverviewService> logger)
    {
        _pipelines = pipelines;
        _adlsFactory = adlsFactory;
        _parquet = parquet;
        _settings = settings;
        _logger = logger;
    }

    public async Task<StorageOverviewResult> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var pipelines = _pipelines.List();
        var built = await Task.WhenAll(
            pipelines.Select(p => BuildPipelineStatsAsync(p.Id, p.Name, cancellationToken)));
        var rows = built.Where(r => r is not null).Cast<PipelineStorageStats>().ToList();

        return new StorageOverviewResult(
            rows,
            rows.Sum(r => r.SnapshotFolderCount),
            rows.Sum(r => r.ParquetBytes),
            rows.Sum(r => r.EligibleSnapshotPurgeCount));
    }

    public async Task<PipelineStorageStats?> GetPipelineAsync(string pipelineId, CancellationToken cancellationToken = default)
    {
        var pipeline = _pipelines.Get(pipelineId);
        return pipeline is null
            ? null
            : await BuildPipelineStatsAsync(pipeline.Id, pipeline.Name, cancellationToken);
    }

    private async Task<PipelineStorageStats?> BuildPipelineStatsAsync(
        string pipelineId,
        string pipelineName,
        CancellationToken cancellationToken)
    {
        try
        {
            var pipeline = _pipelines.Get(pipelineId);
            if (pipeline is null)
            {
                return null;
            }

            var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
            var allSnapshots = await adls.ListSnapshotFoldersAsync(cancellationToken);
            var currentFolder = await adls.GetSettledSnapshotAsync(cancellationToken);
            var state = _pipelines.GetState(pipelineId);
            var retentionFloor = SnapshotRetentionService.SharedLastProcessed(_pipelines, pipeline.SourceId);
            var selection = SnapshotSelector.Select(allSnapshots, retentionFloor, currentFolder);
            var analysis = SnapshotRetentionService.Analyze(
                DateTimeOffset.UtcNow,
                _settings.Get(),
                allSnapshots,
                retentionFloor,
                selection);

            var parquets = await _parquet.ListAsync(pipelineId, cancellationToken);
            var consolidated = parquets
                .Where(p => p.Path.Count(c => c == '/') == 1)
                .ToList();

            var cert = SnapshotRetentionService.CertifyParquet(pipelineId, state, parquets);

            return new PipelineStorageStats(
                pipelineId,
                pipelineName,
                allSnapshots.Count,
                consolidated.Count,
                consolidated.Sum(p => p.SizeBytes),
                analysis.EligibleForPurge.Count,
                cert.IsComplete);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Storage overview failed for pipeline {PipelineId}",
                pipelineId);
            return new PipelineStorageStats(pipelineId, pipelineName, 0, 0, 0, 0, false, Unavailable: true);
        }
    }
}