using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public sealed class AlertService
{
    // Cliente propio sin los loggers de IHttpClientFactory: redactan la query, pero escriben la ruta,
    // y hay webhooks (Slack, Teams, Discord) con el token en la ruta.
    public const string HttpClientName = "alert-webhook";

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);
    private const int MaxResponseBodyInDetail = 300;

    // Reenvíos de la cola por sondeo del scheduler: con el timeout de 10 s, una cola larga sin tope
    // dejaría el ciclo parado (y el scheduler "stale").
    public const int OutboxMaxPerPoll = 20;

    private readonly AppOptions _options;
    private readonly SettingsStore _settings;
    private readonly ILogger<AlertService> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AlertDeliveryStatusStore _status;
    private readonly IActivityLog? _activity;
    private readonly AlertOutboxStore _outbox;

    public AlertService(
        IOptions<AppOptions> options,
        SettingsStore settings,
        ILogger<AlertService> logger,
        IHttpClientFactory httpFactory,
        AlertDeliveryStatusStore? status = null,
        IActivityLog? activity = null,
        AlertOutboxStore? outbox = null)
    {
        _options = options.Value;
        _settings = settings;
        _logger = logger;
        _httpFactory = httpFactory;
        _status = status ?? new AlertDeliveryStatusStore(options);
        _activity = activity;
        _outbox = outbox ?? new AlertOutboxStore(options);
    }

    // Espera antes del único reintento (5xx o timeout). internal: los tests no esperan.
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    // queueOnFailure: si no llega, se guarda en la cola y el scheduler la reintenta hasta 24 h. Lo que
    // quien llama ya reintenta por su cuenta (las alertas deduplicadas del scheduler, que vuelven a
    // intentarse en cada sondeo hasta llegar) pasa false: si no, cada sondeo fallido añadiría otra copia.
    public async Task<AlertDeliveryResult> SendAsync(string message, CancellationToken cancellationToken = default, bool queueOnFailure = true)
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
        }, queueOnFailure, cancellationToken);
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
        }, outbox: true, cancellationToken);
    }

    // Snapshot cerrado pero incompleto que frena el pipeline: es un aviso (el pipeline se reanuda solo
    // cuando Synapse completa el volcado), no un fallo de un job. No se encola: sus dos llamantes (el
    // scheduler y el ingest) solo lo dan por avisado si llega, y lo repiten.
    public async Task<AlertDeliveryResult> SendSnapshotIncompleteAsync(
        string pipelineId, string? pipelineName, string snapshot, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("ALERT: {Message}", message);

        var url = ResolveWebhookUrl();
        if (url is null)
        {
            return AlertDeliveryResult.NotConfigured;
        }

        return await DeliverAsync(url, "snapshot.incomplete", new
        {
            eventType = "snapshot.incomplete",
            timestamp = DateTimeOffset.UtcNow,
            pipelineId,
            pipelineName,
            snapshot,
            status = "warning",
            text = message,
            message,
        }, outbox: false, cancellationToken);
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

        // scheduled = la corrección se lanzó al cerrar el job; failed = no se pudo lanzar (el motivo va en
        // el mensaje); off = desactivada; unresolved = este job ya era la corrección y no bastó;
        // none = no aplica (p. ej. re-sync manual).
        var autoRemediation = alert.AutoFixAttempted ? "unresolved"
            : !alert.AutoFixEligible ? "none"
            : alert.AutoFixLaunched switch { true => "scheduled", false => "failed", null => "off" };

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
        }, outbox: true, cancellationToken);
    }

    // No reintenta. Solo toca el estado de entrega para registrar la recuperación: si la entrega está
    // fallando y la prueba llega al webhook al que van ahora las alertas, el arreglo se comprueba sin
    // esperar a la siguiente alerta real. Una prueba fallida, o a otra URL (escrita y sin guardar), no
    // cambia nada: no debe tapar una caída real ni sumar al recuento.
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

        if (result.Success && !cancellationToken.IsCancellationRequested && FailingDelivery() is not null)
        {
            _settings.TryGet(out var settings);
            if (url == ConfiguredWebhookUrl(settings))
            {
                RecordDelivery(url, "test.ping", result);
            }
        }

        return result;
    }

    // La entrega al webhook está fallando: hay uno configurado ahora y la última entrega real falló.
    // Devuelve ese estado (null si no falla, no hay estado o no se puede leer); lo leen el Panel y
    // /health. Sin webhook no se avisa: un fallo de uno que ya se quitó no degrada el portal para siempre.
    public AlertDeliveryStatus? FailingDelivery()
    {
        // Sin log: se llama en cada /health y en cada refresco del Panel. Con settings.json ilegible
        // cuenta solo ALERT_WEBHOOK_URL, como al enviar.
        _settings.TryGet(out var settings);
        return ConfiguredWebhookUrl(settings) is not null && _status.Get() is { LastSucceeded: false } status ? status : null;
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

        return ConfiguredWebhookUrl(settings);
    }

    // El webhook al que van ahora las alertas: el de Ajustes si está activo; si no, ALERT_WEBHOOK_URL.
    private string? ConfiguredWebhookUrl(AppSettings settings)
    {
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
    // outbox: si al final no llega (también si el apagado corta el envío), se guarda en la cola.
    private async Task<AlertDeliveryResult> DeliverAsync(string url, string eventType, object payload, bool outbox, CancellationToken cancellationToken)
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

        if (outbox && !result.Success)
        {
            Enqueue(eventType, payload);
        }

        // Cancelado por quien llama (apagado, job cancelado): no dice nada del webhook.
        if (cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        RecordDelivery(url, eventType, result);
        return result;
    }

    // Guarda el resultado en el estado compartido y anota en actividad solo las transiciones.
    private void RecordDelivery(string url, string eventType, AlertDeliveryResult result)
    {
        try
        {
            var previous = _status.Record(eventType, result, DateTimeOffset.UtcNow);
            var host = WebhookHost(url);
            if (!result.Success && previous?.LastSucceeded != false)
            {
                _activity?.LogSystem("alert.delivery.failed",
                    $"No se pudo entregar la alerta {eventType} al webhook ({host}): {result.Detail}. Las alertas no llegan hasta que se avise de que se ha recuperado.",
                    null, null, "error", "Activity.Alert.DeliveryFailed", eventType, host, result.Detail);
            }
            else if (result.Success && previous is { LastSucceeded: false })
            {
                // Un estado escrito por una versión sin recuento cuenta al menos el último fallo.
                var failed = Math.Max(1, previous.FailedSinceLastSuccess);
                _activity?.LogSystem("alert.delivery.recovered",
                    $"El webhook de alertas ({host}) vuelve a responder (alerta {eventType}). Mientras estaba caído fallaron {failed} intento(s) de entrega: las alertas de un solo disparo de ese periodo (fallo de job, Partial, divergencia...) se guardaron y se reintentan durante 24 h, marcadas como retrasadas; las deduplicadas se reintentan.",
                    null, null, "info", "Activity.Alert.DeliveryRecovered.Queued", host, eventType, failed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El estado es informativo: no poder guardarlo no cambia el resultado del envío.
            _logger.LogWarning(ex, "No se pudo guardar el estado de entrega de alertas.");
        }
    }

    // Cuántas alertas esperan en la cola de reenvío (0 si no se puede leer); lo muestra el Panel.
    public int QueuedAlerts()
    {
        try
        {
            return _outbox.Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Lo llama solo el scheduler del orquestador, una vez por sondeo. Caduca siempre, aunque otra entrega
    // del sondeo ya fallara (si no, con una alerta deduplicada fallando en cada sondeo nada caducaría), y
    // reenvía solo con deliver: como mucho OutboxMaxPerPoll, de la más antigua a la más reciente, un POST
    // por alerta (la espera creciente ya es el reintento). Un 4xx es de ese payload: cuenta el intento y
    // sigue con la siguiente. Devuelve true si se paró porque el webhook no responde (red, timeout, 5xx o 429).
    public async Task<bool> ProcessOutboxAsync(bool deliver, CancellationToken cancellationToken)
    {
        var expired = _outbox.RemoveExpired();
        if (expired > 0)
        {
            _activity?.LogSystem("alert.outbox.expired",
                $"{expired} alerta(s) sin entregar caducaron tras {AlertOutboxStore.MaxAge.TotalHours:0} h en la cola de reenvío y se descartaron.",
                null, null, "error", "Activity.Alert.OutboxExpired", expired, AlertOutboxStore.MaxAge.TotalHours);
        }

        // Sin webhook ahora no se intenta nada: la cola espera (y caduca) sin contar intentos.
        _settings.TryGet(out var settings);
        if (!deliver || ConfiguredWebhookUrl(settings) is not { } url)
        {
            return false;
        }

        foreach (var (path, entry) in _outbox.Due(OutboxMaxPerPoll))
        {
            var (result, _) = await PostOnceAsync(url, Delayed(entry), cancellationToken);
            // Cortado por el apagado: el fichero queda como estaba.
            if (!result.Success && cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (result.Success)
            {
                RecordDelivery(url, entry.EventType, result);
                _outbox.Delete(path);
                continue;
            }

            _outbox.RecordFailedAttempt(path, entry);
            if (result.StatusCode is null or >= 500 or 429)
            {
                RecordDelivery(url, entry.EventType, result);
                return true;
            }

            // Un 4xx es de ese payload, no del webhook: no toca el estado global, o una alerta que siempre
            // lo da haría alternar el Panel y /health en cada reintento. Con la URL mal, todo da 4xx y el
            // estado ya queda en fallo por las entregas reales.
        }

        return false;
    }

    // Nunca lanza: las alertas salen en caminos de fallo (un job que se cierra, un barrido que falla), y
    // no poder guardar la cola no debe tumbarlos. Se guarda el payload tal como se envió (mismas opciones
    // que PostAsJsonAsync), con su timestamp original; nunca la URL.
    private void Enqueue(string eventType, object payload)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), JsonSerializerOptions.Web)!;
            var dropped = _outbox.Enqueue(eventType, node);
            if (dropped > 0)
            {
                _activity?.LogSystem("alert.outbox.dropped",
                    $"La cola de reenvío de alertas llegó al tope de {AlertOutboxStore.MaxEntries}: se descartaron {dropped} alerta(s), las más antiguas.",
                    null, null, "warning", "Activity.Alert.OutboxDropped", AlertOutboxStore.MaxEntries, dropped);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la alerta {EventType} en la cola de reenvío: no se reintentará.", eventType);
        }
    }

    // Copia del payload guardado con la marca de entrega tardía; el fichero no cambia, así que un
    // reenvío que vuelve a fallar no acumula prefijos.
    private JsonObject Delayed(AlertOutboxEntry entry)
    {
        var minutes = (int)Math.Max(0, (_outbox.Now - entry.FirstFailedAt).TotalMinutes);
        var payload = (JsonObject)entry.Payload!.DeepClone();
        payload["delayed"] = true;
        payload["delayedMinutes"] = minutes;
        foreach (var field in new[] { "message", "text" })
        {
            if (payload[field] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                payload[field] = $"[Entrega retrasada: {minutes} min] {text}";
            }
        }

        return payload;
    }

    // Un POST con su propio timeout. Distingue la respuesta HTTP (con el cuerpo recortado), el
    // timeout y el error de red; retryable = 5xx o timeout.
    private async Task<(AlertDeliveryResult Result, bool Retryable)> PostOnceAsync(string url, object payload, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HttpTimeout);

            using var response = await _httpFactory.CreateClient(HttpClientName).PostAsJsonAsync(url, payload, cts.Token);
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

    // La URL lleva un secreto (la firma sig=... en la query, o un token en la ruta): en los logs solo el host.
    private static string WebhookHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid URL)";
}
