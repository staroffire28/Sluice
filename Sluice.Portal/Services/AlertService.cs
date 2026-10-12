using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
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

    public AlertService(
        IOptions<AppOptions> options,
        SettingsStore settings,
        ILogger<AlertService> logger,
        IHttpClientFactory httpFactory)
    {
        _options = options.Value;
        _settings = settings;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("ALERT: {Message}", message);

        var url = ResolveWebhookUrl();
        if (url is not null)
        {
            await TryPostJsonAsync(url, new
            {
                eventType = "system.alert",
                timestamp = DateTimeOffset.UtcNow,
                text = message,
                message,
            }, cancellationToken);
        }
    }

    public async Task SendPipelineFailureAsync(PipelineFailureAlert alert, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "PIPELINE FAILURE: pipeline={PipelineId} job={JobId} stage={Stage} message={Message}",
            alert.PipelineId, alert.JobId, alert.Stage, alert.Message);

        var url = ResolveWebhookUrl();
        if (url is null)
        {
            return;
        }

        await TryPostJsonAsync(url, new
        {
            eventType = "pipeline.failure",
            timestamp = DateTimeOffset.UtcNow,
            pipelineId = alert.PipelineId,
            pipelineName = alert.PipelineName,
            jobId = alert.JobId,
            jobType = alert.JobType,
            stage = alert.Stage,
            status = "error",
            message = alert.Message,
        }, cancellationToken);
    }

    // Conciliación: estructurado para que el receptor (p. ej. la Logic App de correo) pinte la tabla
    // de entidades y el estado de la corrección automática. text/message se conservan para los
    // consumidores que solo leen el texto.
    public async Task SendReconcileAlertAsync(ReconcileAlert alert, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("ALERT: {Message}", alert.Message);

        var url = ResolveWebhookUrl();
        if (url is null)
        {
            return;
        }

        // off = corrección automática desactivada; scheduled = se lanzará al cerrar el job;
        // unresolved = este job ya era la corrección y no bastó; none = no aplica (p. ej. re-sync manual).
        var autoRemediation = alert.AutoFixAttempted ? "unresolved"
            : !alert.AutoFixEligible ? "none"
            : _settings.Get().AutoRemediate ? "scheduled" : "off";

        await TryPostJsonAsync(url, new
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

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HttpTimeout);

            using var response = await PostJsonInternalAsync(url, new
            {
                eventType = "test.ping",
                timestamp = DateTimeOffset.UtcNow,
                message = "Sluice portal alert test",
            }, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                return new AlertDeliveryResult(true, $"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
            }

            // Basta un fragmento para diagnosticar (p. ej. "invalid_payload"); no se devuelve la
            // respuesta entera del servidor remoto a la UI.
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (body.Length > MaxResponseBodyInDetail)
            {
                body = body[..MaxResponseBodyInDetail] + "…";
            }

            var detail = string.IsNullOrWhiteSpace(body)
                ? $"HTTP {(int)response.StatusCode}"
                : $"HTTP {(int)response.StatusCode}: {body}";
            return new AlertDeliveryResult(false, detail, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test alert failed for webhook");
            return new AlertDeliveryResult(false, ex.Message, null);
        }
    }

    private string? ResolveWebhookUrl()
    {
        var settings = _settings.Get();
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

    private async Task TryPostJsonAsync(string url, object payload, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await PostJsonInternalAsync(url, payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Alert webhook returned HTTP {Status} for host {Host}", (int)response.StatusCode, WebhookHost(url));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert webhook POST failed for host {Host}", WebhookHost(url));
        }
    }

    // La URL lleva la firma (sig=...) en la query: es un secreto, en los logs solo el host.
    private static string WebhookHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid URL)";

    private async Task<HttpResponseMessage> PostJsonInternalAsync(string url, object payload, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HttpTimeout);

        var client = _httpFactory.CreateClient();
        return await client.PostAsJsonAsync(url, payload, cts.Token);
    }
}