using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

public class AlertServiceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly TestHttpHandler _handler = new();

    public AlertServiceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sluice-portal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private AlertService CreateService(AppOptions? options = null, AppSettings? settings = null, ILogger<AlertService>? logger = null)
    {
        var store = new SettingsStore(Options.Create(new AppOptions { DataDir = _dataDir }));
        if (settings is not null)
        {
            store.Save(settings);
        }

        var factory = new TestHttpClientFactory(_handler);
        return new AlertService(
            Options.Create(options ?? new AppOptions()),
            store,
            logger ?? NullLogger<AlertService>.Instance,
            factory);
    }

    // La URL del webhook lleva la firma (sig=...) en la query: es un secreto y no debe acabar
    // en los logs; basta con el host para diagnosticar.
    [Fact]
    public async Task WebhookFallido_RegistraElHostPeroNoLaFirma()
    {
        _handler.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var logger = new CapturingLogger();
        var service = CreateService(
            settings: new AppSettings { AlertEnabled = true, AlertWebhookUrl = "https://hooks.example/run?sig=SECRETO123" },
            logger: logger);

        await service.SendAsync("boom");

        Assert.Contains(logger.Messages, m => m.Contains("hooks.example"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SECRETO123"));
    }

    // El "enviar prueba" de Settings postea a la URL escrita a mano: solo https.
    [Theory]
    [InlineData("http://10.0.0.5/admin")]
    [InlineData("file:///etc/passwd")]
    [InlineData("no-es-una-url")]
    public async Task SendTestAlertAsync_SinHttps_NoPostea(string url)
    {
        var result = await CreateService().SendTestAlertAsync(url);

        Assert.False(result.Success);
        Assert.Equal(0, _handler.RequestCount);
    }

    [Fact]
    public async Task SendTestAlertAsync_RecortaElCuerpoDeLaRespuesta()
    {
        _handler.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(new string('x', 5000)),
        };

        var result = await CreateService().SendTestAlertAsync("https://logic.example/test");

        Assert.False(result.Success);
        Assert.True(result.Detail!.Length < 400, $"Detail de {result.Detail.Length} caracteres");
    }

    private sealed class CapturingLogger : ILogger<AlertService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task SendPipelineFailureAsync_WhenDisabledAndNoEnv_DoesNotPost()
    {
        var service = CreateService();

        await service.SendPipelineFailureAsync(new PipelineFailureAlert
        {
            PipelineId = "p1",
            JobId = "j1",
            JobType = "Incremental",
            Stage = "ingest",
            Message = "boom",
        });

        Assert.Equal(0, _handler.RequestCount);
    }

    [Fact]
    public async Task SendPipelineFailureAsync_WhenEnabled_PostsStructuredPayload()
    {
        _handler.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK);
        var service = CreateService(settings: new AppSettings
        {
            AlertEnabled = true,
            AlertWebhookUrl = "https://logic.example/webhook",
        });

        await service.SendPipelineFailureAsync(new PipelineFailureAlert
        {
            PipelineId = "pipe-1",
            PipelineName = "Sales",
            JobId = "job-1",
            JobType = "Incremental",
            Stage = "sql",
            Message = "merge failed",
        });

        Assert.Equal(1, _handler.RequestCount);
        var body = _handler.LastBody;
        Assert.NotNull(body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("pipeline.failure", doc.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("pipe-1", doc.RootElement.GetProperty("pipelineId").GetString());
        Assert.Equal("job-1", doc.RootElement.GetProperty("jobId").GetString());
        Assert.Equal("sql", doc.RootElement.GetProperty("stage").GetString());
        Assert.Equal("merge failed", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SendPipelineFailureAsync_FallsBackToEnvWebhook()
    {
        _handler.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.Accepted);
        var service = CreateService(new AppOptions { AlertWebhookUrl = "https://env.example/hook" });

        await service.SendPipelineFailureAsync(new PipelineFailureAlert
        {
            PipelineId = "p1",
            JobId = "j1",
            JobType = "Full",
            Stage = "ingest",
            Message = "err",
        });

        Assert.Equal(1, _handler.RequestCount);
        Assert.Equal("https://env.example/hook", _handler.LastUrl);
    }

    [Fact]
    public async Task SendTestAlertAsync_ReturnsSuccessOnOk()
    {
        _handler.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK);
        var service = CreateService();

        var result = await service.SendTestAlertAsync("https://logic.example/test");

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        using var doc = JsonDocument.Parse(_handler.LastBody!);
        Assert.Equal("test.ping", doc.RootElement.GetProperty("eventType").GetString());
    }

    // Contrato con la Logic App de correo: tablas accionables y estado de la corrección automática.
    [Theory]
    [InlineData(true, false, true, "scheduled")]
    [InlineData(true, false, false, "off")]
    [InlineData(false, true, true, "unresolved")]
    [InlineData(false, false, true, "none")]
    public async Task AlertaDeConciliacion_LlevaLasTablasYElEstadoDeLaCorreccion(bool eligible, bool attempted, bool autoRemediate, string expected)
    {
        var service = CreateService(settings: new AppSettings
        {
            AlertEnabled = true,
            AlertWebhookUrl = "https://hooks.example/run",
            AutoRemediate = autoRemediate,
        });

        await service.SendReconcileAlertAsync(new ReconcileAlert
        {
            EventType = ReconcileAlert.Values,
            PipelineId = "pipe-1",
            PipelineName = "Lean",
            JobId = "job-1",
            Message = "diferencias",
            Entities = ["custtable", "vendtable"],
            AutoFixEligible = eligible,
            AutoFixAttempted = attempted,
        });

        using var doc = JsonDocument.Parse(_handler.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("reconcile.values", root.GetProperty("eventType").GetString());
        Assert.Equal(["custtable", "vendtable"], root.GetProperty("entities").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(expected, root.GetProperty("autoRemediation").GetString());
        Assert.Equal("diferencias", root.GetProperty("text").GetString());
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastUrl { get; private set; }
        public string? LastBody { get; private set; }
        public Func<HttpResponseMessage>? ResponseFactory { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastUrl = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return ResponseFactory?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TestHttpClientFactory(TestHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}