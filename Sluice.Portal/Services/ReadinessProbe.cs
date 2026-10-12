using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Services;

public sealed record PipelineReadiness(string PipelineId, bool SqlOk, bool AdlsOk);

public sealed record ReadinessReport(bool DataDirOk, IReadOnlyList<PipelineReadiness> Pipelines)
{
    public bool Healthy => DataDirOk && Pipelines.All(p => p.SqlOk && p.AdlsOk);
}

// Readiness para /health/ready, que es anónimo:
// - solo devuelve booleanos: los mensajes de excepción de SQL/ADLS (que pueden incluir servidor,
//   usuario o fragmentos de credenciales) van al log, no a la respuesta;
// - el resultado se reutiliza unos segundos, para que peticiones repetidas no abran conexiones
//   SQL/ADLS en cada llamada.
public sealed class ReadinessProbe
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly AppOptions _options;
    private readonly IPipelineService _pipelines;
    private readonly ISqlConnectionFactory _sqlFactory;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly ILogger<ReadinessProbe> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, ReadinessReport Report)? _cached;

    public ReadinessProbe(
        IOptions<AppOptions> options,
        IPipelineService pipelines,
        ISqlConnectionFactory sqlFactory,
        IAdlsClientFactory adlsFactory,
        ILogger<ReadinessProbe> logger)
    {
        _options = options.Value;
        _pipelines = pipelines;
        _sqlFactory = sqlFactory;
        _adlsFactory = adlsFactory;
        _logger = logger;
    }

    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - cached.At < CacheFor)
            {
                return cached.Report;
            }

            var dataDirError = DataDirProbe.CheckWritable(_options.DataDir);
            if (dataDirError is not null)
            {
                _logger.LogWarning("Readiness: {Error}", dataDirError);
            }

            var pipelines = await Task.WhenAll(_pipelines.List()
                .Where(p => p.Enabled)
                .Select(p => CheckPipelineAsync(p.Id, p.SourceId, p.DestinationId, cancellationToken)));

            var report = new ReadinessReport(dataDirError is null, pipelines);
            _cached = (DateTimeOffset.UtcNow, report);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PipelineReadiness> CheckPipelineAsync(string pipelineId, string sourceId, string destinationId, CancellationToken cancellationToken)
    {
        var sqlOk = await ProbeAsync(pipelineId, "SQL", () => _sqlFactory.TestAsync(destinationId, cancellationToken));
        var adlsOk = await ProbeAsync(pipelineId, "ADLS", () => _adlsFactory.TestAsync(sourceId, cancellationToken));
        return new PipelineReadiness(pipelineId, sqlOk, adlsOk);
    }

    private async Task<bool> ProbeAsync(string pipelineId, string target, Func<Task<(bool Ok, string Message)>> probe)
    {
        try
        {
            var (ok, message) = await probe();
            if (!ok)
            {
                _logger.LogWarning("Readiness: {Target} del pipeline {PipelineId} no disponible: {Message}", target, pipelineId, message);
            }

            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Readiness: {Target} del pipeline {PipelineId} no disponible.", target, pipelineId);
            return false;
        }
    }
}

public static class DataDirProbe
{
    // null si DATA_DIR es escribible; si no, el motivo (solo para el log, no para la respuesta).
    public static string? CheckWritable(string dataDir)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var probe = Path.Combine(dataDir, $".health-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return $"DATA_DIR not writable: {ex.Message}";
        }
    }
}
