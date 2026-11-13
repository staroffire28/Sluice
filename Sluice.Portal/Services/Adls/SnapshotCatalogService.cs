using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Adls;

public sealed record SnapshotEntry(string Name, string Status, string? RetentionDisposition = null);

public sealed record SnapshotCatalogResult(
    string? SettledSnapshot,
    string? LastProcessedSnapshot,
    DateTimeOffset? ChangelogLastModified,
    bool IsChangelogStalled,
    IReadOnlyList<SnapshotEntry> Snapshots,
    IReadOnlyList<string> PendingSnapshots,
    IReadOnlyList<string> UnclosedSnapshots,
    bool Blocked,
    string? BlockReason,
    int EligibleRetentionPurgeCount,
    bool RetentionDryRun,
    string? BlockReasonKey = null,
    object?[]? BlockReasonArgs = null);

public sealed record SnapshotRetentionPreviewResult(
    int EligibleCount,
    IReadOnlyList<string> EligibleFolders,
    IReadOnlyDictionary<string, string> DispositionBySnapshot,
    bool DryRun,
    bool ParquetCertified,
    IReadOnlyList<string> MissingParquetEntities);

public interface ISnapshotCatalogService
{
    Task<SnapshotCatalogResult> GetCatalogAsync(string pipelineId, CancellationToken cancellationToken = default);
    Task<SnapshotRetentionPreviewResult> GetRetentionPreviewAsync(string pipelineId, CancellationToken cancellationToken = default);
    bool IsChangelogStalled(DateTimeOffset? changelogLastModified, DateTimeOffset now);
}

public sealed class SnapshotCatalogService : ISnapshotCatalogService
{
    private readonly AppOptions _options;
    private readonly IPipelineService _pipelines;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly SettingsStore _settings;
    private readonly ILogger<SnapshotCatalogService> _logger;

    public SnapshotCatalogService(
        IOptions<AppOptions> options,
        IPipelineService pipelines,
        IAdlsClientFactory adlsFactory,
        SettingsStore settings,
        ILogger<SnapshotCatalogService>? logger = null)
    {
        _options = options.Value;
        _pipelines = pipelines;
        _adlsFactory = adlsFactory;
        _settings = settings;
        _logger = logger ?? NullLogger<SnapshotCatalogService>.Instance;
    }

    public bool IsChangelogStalled(DateTimeOffset? changelogLastModified, DateTimeOffset now)
    {
        if (changelogLastModified is null)
        {
            return false;
        }

        return now - changelogLastModified.Value > TimeSpan.FromHours(Math.Max(1, _options.ChangelogStallHours));
    }

    public async Task<SnapshotCatalogResult> GetCatalogAsync(
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var pipeline = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{pipelineId}' not found.");

        var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
        var currentFolder = await adls.GetSettledSnapshotAsync(cancellationToken);
        var changelogLastModified = await adls.GetChangelogLastModifiedAsync(cancellationToken);
        var allSnapshots = await adls.ListSnapshotFoldersAsync(cancellationToken);
        var state = _pipelines.GetState(pipelineId);
        var selection = SnapshotSelector.Select(allSnapshots, state.LastProcessedSnapshot, currentFolder);
        var now = DateTimeOffset.UtcNow;
        var appSettings = _settings.Get();
        // La retención lee el estado de todos los pipelines de la fuente. Si el de otro es ilegible,
        // el catálogo de este no debe fallar (el scheduler lo pide en cada poll): se muestra sin
        // carpetas elegibles. El barrido real sigue sin purgar mientras ese estado no se lea.
        var siblingStateUnreadable = HasUnreadableSiblingState(pipeline);
        SnapshotRetentionAnalysis analysis;
        if (siblingStateUnreadable)
        {
            analysis = new SnapshotRetentionAnalysis([], new Dictionary<string, string>());
        }
        else
        {
            var retentionFloor = SnapshotRetentionService.SharedLastProcessed(_pipelines, pipeline.SourceId);
            analysis = SnapshotRetentionService.Analyze(
                now,
                appSettings,
                allSnapshots,
                retentionFloor,
                SnapshotSelector.Select(allSnapshots, retentionFloor, currentFolder),
                SnapshotRetentionService.SharedBackfillFrom(_pipelines, pipeline.SourceId));
        }

        var entries = allSnapshots
            .Select(name => new SnapshotEntry(
                name,
                ResolveStatus(name, selection.LastSettled, state.LastProcessedSnapshot, selection),
                analysis.DispositionBySnapshot.GetValueOrDefault(name)))
            .ToList();

        return new SnapshotCatalogResult(
            selection.LastSettled,
            state.LastProcessedSnapshot,
            changelogLastModified,
            IsChangelogStalled(changelogLastModified, now),
            entries,
            selection.ToProcess,
            selection.Unclosed,
            selection.Blocked,
            selection.BlockReason,
            analysis.EligibleForPurge.Count,
            appSettings.SourceSnapshotRetentionDryRun,
            selection.BlockReasonKey,
            selection.BlockReasonArgs);
    }

    public async Task<SnapshotRetentionPreviewResult> GetRetentionPreviewAsync(
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var pipeline = _pipelines.Get(pipelineId)
            ?? throw new InvalidOperationException($"Pipeline '{pipelineId}' not found.");

        var adls = await _adlsFactory.CreateAsync(pipeline.SourceId, cancellationToken);
        var parquetStore = await _adlsFactory.CreateParquetStoreAsync(pipeline.SourceId, cancellationToken);
        var allSnapshots = await adls.ListSnapshotFoldersAsync(cancellationToken);
        var currentFolder = await adls.GetSettledSnapshotAsync(cancellationToken);
        var state = _pipelines.GetState(pipelineId);
        var retentionFloor = SnapshotRetentionService.SharedLastProcessed(_pipelines, pipeline.SourceId);
        var selection = SnapshotSelector.Select(allSnapshots, retentionFloor, currentFolder);
        var appSettings = _settings.Get();
        var analysis = SnapshotRetentionService.Analyze(
            DateTimeOffset.UtcNow,
            appSettings,
            allSnapshots,
            retentionFloor,
            selection,
            SnapshotRetentionService.SharedBackfillFrom(_pipelines, pipeline.SourceId));
        var cert = await SnapshotRetentionService.CertifyParquetAsync(
            pipelineId,
            parquetStore,
            state,
            cancellationToken);

        return new SnapshotRetentionPreviewResult(
            analysis.EligibleForPurge.Count,
            analysis.EligibleForPurge,
            analysis.DispositionBySnapshot,
            appSettings.SourceSnapshotRetentionDryRun,
            cert.IsComplete,
            cert.MissingEntities);
    }

    // true si algún pipeline que comparte fuente con este tiene un estado ilegible (lo deja en el log).
    private bool HasUnreadableSiblingState(Models.Pipeline pipeline)
    {
        foreach (var sibling in _pipelines.List().Where(p => p.SourceId == pipeline.SourceId && p.Id != pipeline.Id))
        {
            try
            {
                _pipelines.GetState(sibling.Id);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex,
                    "No se pudo leer el estado del pipeline {SiblingId}, que comparte origen con {PipelineId}: el catálogo se muestra sin carpetas elegibles para la retención.",
                    sibling.Id,
                    pipeline.Id);
                return true;
            }
        }

        return false;
    }

    private static string ResolveStatus(
        string name,
        string? lastSettled,
        string? lastProcessed,
        SnapshotSelector.SelectionResult selection)
    {
        if (!string.IsNullOrWhiteSpace(lastSettled) && string.Equals(name, lastSettled, StringComparison.Ordinal))
        {
            return "settled";
        }

        if (selection.Unclosed.Contains(name))
        {
            return "unclosed";
        }

        if (selection.ToProcess.Contains(name))
        {
            return "pending";
        }

        if (!string.IsNullOrWhiteSpace(lastProcessed)
            && string.CompareOrdinal(name, lastProcessed) <= 0)
        {
            return "processed";
        }

        return "available";
    }
}