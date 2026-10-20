using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services;

public sealed class DeadmanService
{
    // Cliente propio sin los loggers de IHttpClientFactory, que escriben la URL completa (con el token).
    public const string HttpClientName = "deadman";

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private readonly AppOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeadmanService> _logger;

    public DeadmanService(IOptions<AppOptions> options, IHttpClientFactory httpFactory, ILogger<DeadmanService> logger)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.DeadmanPingUrl);

    // Último ping de este proceso (el orquestador, que es también el que sirve Ajustes). Null hasta el
    // primer poll completo tras arrancar.
    public DeadmanPing? LastPing { get; private set; }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return;
        }

        // La URL puede llevar el token del monitor: en los logs solo el host.
        var host = Uri.TryCreate(_options.DeadmanPingUrl, UriKind.Absolute, out var uri) ? uri.Host : "(invalid URL)";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(HttpTimeout);
            using var response = await _httpFactory.CreateClient(HttpClientName).GetAsync(_options.DeadmanPingUrl, cts.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                LastPing = new DeadmanPing(DateTimeOffset.UtcNow, true, $"HTTP {status}");
                return;
            }

            _logger.LogWarning("Dead-man ping returned HTTP {Status} for host {Host}.", status, host);
            LastPing = new DeadmanPing(DateTimeOffset.UtcNow, false, $"HTTP {status}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Dead-man ping timed out after {Seconds} s for host {Host}.", HttpTimeout.TotalSeconds, host);
            LastPing = new DeadmanPing(DateTimeOffset.UtcNow, false, $"Timeout ({HttpTimeout.TotalSeconds:0} s)");
        }
        catch (OperationCanceledException)
        {
            // Apagado: no dice nada del monitor.
        }
        catch (Exception ex)
        {
            // Sin la excepción entera: su texto puede llevar la URL completa.
            _logger.LogWarning("Dead-man ping failed for host {Host}: {Error}", host, ex.GetType().Name);
            LastPing = new DeadmanPing(DateTimeOffset.UtcNow, false, ex is HttpRequestException ? "Network error" : ex.GetType().Name);
        }
    }
}

public sealed record DeadmanPing(DateTimeOffset At, bool Success, string Detail);
