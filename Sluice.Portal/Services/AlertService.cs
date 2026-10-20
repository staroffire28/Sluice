using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public sealed class AlertService
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);
    private const int MaxResponseBodyInDetail = 300;

    private readonly AppOptions _options;
    private readonly SettingsStore _settings;
    private readonly ILogger<AlertService> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AlertDeliveryStatusStore _status;
    private readonly IActivityLog? _activity;

    public AlertService(
        IOptions<AppOptions> options,
        SettingsStore settings,
        ILogger<AlertService> logger,
        IHttpClientFactory httpFactory,
        AlertDeliveryStatusStore? status = null,
        IActivityLog? activity = null)
    {
        _options = options.Value;
        _settings = settings;
        _logger = logger;
        _httpFactory = httpFactory;
        _status = status ?? new AlertDeliveryStatusStore(options);
        _activity = activity;
    }

    // Espera antes del único reintento (5xx o timeout). internal: los tests no esperan.
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<AlertDeliveryResult> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("ALERT: {Message}", message);

        var url = ResolveWebhookUrl();
        if (url is null)
        {
            return AlertDeliveryResult.NotConfigured;
        }

        return await DeliverAsync(url, "system.alert", new
        {
            eventType = "system.alert",
            timestamp = DateTimeOffset.UtcNow,
            text = message,
            message,
        }, cancellationToken);
    }

    public async Task<AlertDeliveryResult> SendPipelineFailureAsync(PipelineFailureAlert alert, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "PIPELINE FAILURE: pipeline={PipelineId} job={JobId} stage={Stage} message={Message}",
            alert.PipelineId, alert.JobId, alert.Stage, alert.Message);

        var url = ResolveWebhookUrl();
        if (url is null)
        {
            return AlertDeliveryResult.NotConfigured;
        }

        return await DeliverAsync(url, "pipeline.failure", new
        {
            eventType = "pipeline.failure",
            timestamp = DateTimeOffset.UtcNow,
            pipelineId = alert.PipelineId,
            pipelineName = alert.PipelineName,
            jobId = alert.JobId,
            jobType = alert.JobType,
            stage = alert.Stage,
            status = alert.Status,
            message = alert.Message,
        }, cancellationToken);
    }

    // Conciliación: estructurado para que el receptor (p. ej. la Logic App de correo) pinte la tabla
    // de entidades y el estado de la corrección automática. text/message se conservan para los
    // consumidores que solo leen el texto.
    public async Task<AlertDeliveryResult> SendReconcileAlertAsync(ReconcileAlert alert, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("ALERT: {Message}", alert.Message);

        var url = ResolveWebhookUrl();
        if (url is null)
        {
            return AlertDeliveryResult.NotConfigured;
        }

        // off = corrección automática desactivada; scheduled = se lanzará al cerrar el job;
        // unresolved = este job ya era la corrección y no bastó; none = no aplica (p. ej. re-sync manual).
        var autoRemediation = alert.AutoFixAttempted ? "unresolved"
            : !alert.AutoFixEligible ? "none"
            : _settings.Get().AutoRemediate ? "scheduled" : "off";

        return await DeliverAsync(url, alert.EventType, new
        {
            eventType = alert.EventType,
            timestamp = DateTimeOffset.UtcNow,
            pipelineId = alert.PipelineId,
            pipelineName = alert.PipelineName,
            jobId = alert.JobId,
            status = "warning",
            text = alert.Message,
            message = alert.Message,
            maxDivergencePct = alert.MaxDivergencePct,
            entities = alert.Entities,
            autoRemediation,
        }, cancellationToken);
    }

    // No reintenta ni toca el estado de entrega: una prueba a mano no debe tapar una caída real.
    public async Task<AlertDeliveryResult> SendTestAlertAsync(string? webhookUrl, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(webhookUrl) ? ResolveWebhookUrl() : webhookUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return new AlertDeliveryResult(false, "Webhook URL is required.", null);
        }

        // Postea a una URL escrita a mano desde Settings: solo https, para no usar el portal
        // como trampolín hacia servicios internos por http (SSRF).
        if (!SettingsStore.IsHttpsUrl(url))
        {
            return new AlertDeliveryResult(false, "Webhook URL must be an absolute https:// URL.", null);
        }

        var (result, _) = await PostOnceAsync(url, new
        {
            eventType = "test.ping",
            timestamp = DateTimeOffset.UtcNow,
            message = "Sluice portal alert test",
        }, cancellationToken);
        return result;
    }

    private string? ResolveWebhookUrl()
    {
        // Con settings.json ilegible no se sabe si el webhook de Ajustes está activo: queda solo el de
        // ALERT_WEBHOOK_URL, y se dice, porque sin él la alerta solo llega a este log.
        if (!_settings.TryGet(out var settings))
        {
            if (string.IsNullOrWhiteSpace(_options.AlertWebhookUrl))
            {
                _logger.LogError("settings.json no se puede leer y no hay ALERT_WEBHOOK_URL: la alerta solo queda en este log.");
            }
            else
            {
                _logger.LogWarning("settings.json no se puede leer: la alerta va a ALERT_WEBHOOK_URL, no al webhook de Ajustes.");
            }
        }

        if (settings.AlertEnabled && !string.IsNullOrWhiteSpace(settings.AlertWebhookUrl))
        {
            return settings.AlertWebhookUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_options.AlertWebhookUrl))
        {
            return _options.AlertWebhookUrl.Trim();
        }

        return null;
    }

    // Un reintento ante 5xx o timeout (un 4xx o un fallo de red/DNS no se arreglan reintentando al
    // momento). El resultado queda en el estado compartido; el activity log solo registra las
    // transiciones (empieza a fallar / se recupera), no cada alerta perdida.
    private async Task<AlertDeliveryResult> DeliverAsync(string url, string eventType, object payload, CancellationToken cancellationToken)
    {
        var (result, retryable) = await PostOnceAsync(url, payload, cancellationToken);
        if (retryable)
        {
            try
            {
                await Task.Delay(RetryDelay, cancellationToken);
                (result, _) = await PostOnceAsync(url, payload, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        // Cancelado por quien llama (apagado, job cancelado): no dice nada del webhook.
        if (cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        try
        {
            var previous = _status.Record(eventType, result, DateTimeOffset.UtcNow);
            var host = WebhookHost(url);
            if (!result.Success && previous?.LastSucceeded != false)
            {
                _activity?.LogSystem("alert.delivery.failed",
                    $"No se pudo entregar la alerta {eventType} al webhook ({host}): {result.Detail}. Las alertas no llegan hasta que se avise de que se ha recuperado.",
                    level: "error");
            }
            else if (result.Success && previous is { LastSucceeded: false })
            {
                _activity?.LogSystem("alert.delivery.recovered",
                    $"El webhook de alertas ({host}) vuelve a responder (alerta {eventType}).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El estado es informativo: no poder guardarlo no cambia el resultado del envío.
            _logger.LogWarning(ex, "No se pudo guardar el estado de entrega de alertas.");
        }

        return result;
    }

    // Un POST con su propio timeout. Distingue la respuesta HTTP (con el cuerpo recortado), el
    // timeout y el error de red; retryable = 5xx o timeout.
    private async Task<(AlertDeliveryResult Result, bool Retryable)> PostOnceAsync(string url, object payload, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HttpTimeout);

            using var response = await _httpFactory.CreateClient().PostAsJsonAsync(url, payload, cts.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return (new AlertDeliveryResult(true, $"HTTP {status}", status), false);
            }

            _logger.LogWarning("Alert webhook returned HTTP {Status} for host {Host}", status, WebhookHost(url));

            // Basta un fragmento para diagnosticar (p. ej. "invalid_payload"); no se guarda ni se
            // muestra la respuesta entera del servidor remoto.
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (body.Length > MaxResponseBodyInDetail)
            {
                body = body[..MaxResponseBodyInDetail] + "…";
            }

            var detail = string.IsNullOrWhiteSpace(body) ? $"HTTP {status}" : $"HTTP {status}: {body}";
            return (new AlertDeliveryResult(false, detail, status), status >= 500);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Alert webhook timed out after {Seconds} s for host {Host}", HttpTimeout.TotalSeconds, WebhookHost(url));
            return (new AlertDeliveryResult(false, $"Timeout ({HttpTimeout.TotalSeconds:0} s)", null), true);
        }
        catch (OperationCanceledException)
        {
            return (new AlertDeliveryResult(false, "Cancelled.", null), false);
        }
        catch (Exception ex)
        {
            // HttpRequestException = DNS, conexión rechazada, TLS: la red, no el receptor.
            _logger.LogWarning(ex, "Alert webhook POST failed for host {Host}", WebhookHost(url));
            var detail = ex is HttpRequestException ? $"Network error: {ex.Message}" : ex.Message;
            return (new AlertDeliveryResult(false, detail, null), false);
        }
    }

    // La URL lleva la firma (sig=...) en la query: es un secreto, en los logs solo el host.
    private static string WebhookHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid URL)";
}
