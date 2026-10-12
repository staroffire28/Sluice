using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services;

public sealed class DeadmanService
{
    private readonly AppOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DeadmanService> _logger;

    public DeadmanService(IOptions<AppOptions> options, IHttpClientFactory httpFactory, ILogger<DeadmanService> logger)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.DeadmanPingUrl))
        {
            return;
        }

        try
        {
            using var response = await _httpFactory.CreateClient().GetAsync(_options.DeadmanPingUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dead-man ping failed.");
        }
    }
}