using System.Net;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;

namespace Sluice.Portal.Tests.Fakes;

// Dobles compartidos por los tests de los servicios en segundo plano (scheduler, retención, worker host).

// Log de actividad en memoria; ThrowOnAction permite forzar un fallo en una acción concreta.
internal sealed class RecordingActivityLog : IActivityLog
{
    private readonly object _lock = new();
    private readonly List<(string Action, string Message, string Level)> _entries = [];

    public string? ThrowOnAction { get; init; }

    public IReadOnlyList<(string Action, string Message, string Level)> Entries
    {
        get { lock (_lock) { return [.. _entries]; } }
    }

    public void LogSystem(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info")
    {
        if (action == ThrowOnAction)
        {
            throw new IOException($"activity log forzado a fallar en '{action}'");
        }

        lock (_lock)
        {
            _entries.Add((action, message, level));
        }
    }

    public void LogUser(string action, string message, string? pipelineId = null, string? jobId = null, string level = "info") =>
        LogSystem(action, message, pipelineId, jobId, level);

    public ActivityLogPage QueryPage(ActivityLogFilter filter, int page, int pageSize) =>
        new([], 0, page);

    public IReadOnlyList<ActivityLogEntry> Query(ActivityLogFilter filter) => [];
}

// Webhook falso: guarda los cuerpos recibidos y permite esperar a que lleguen N peticiones.
internal sealed class RecordingWebhookHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<string> _bodies = [];

    public IReadOnlyList<string> Bodies
    {
        get { lock (_lock) { return [.. _bodies]; } }
    }

    public async Task WaitForRequestsAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Bodies.Count < expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Solo llegaron {Bodies.Count} de {expected} peticiones al webhook.");
            await Task.Delay(10);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(CancellationToken.None);
        lock (_lock)
        {
            _bodies.Add(body);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

internal sealed class RecordingHttpClientFactory(RecordingWebhookHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

