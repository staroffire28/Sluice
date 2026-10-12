using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

public sealed class ApiIntegrationTests : IClassFixture<ApiWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly ApiWebApplicationFactory _factory;

    public ApiIntegrationTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task GetStatus_ReturnsReady()
    {
        var response = await _client.GetAsync("/api/status");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetJobs_ReturnsPaginationMetadata()
    {
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        for (var i = 0; i < 3; i++)
        {
            jobs.Create(new Job { Id = $"job-pagination-{i}", PipelineId = "pipe-pagination", Type = JobType.Incremental });
        }

        var response = await _client.GetAsync("/api/jobs?pipelineId=pipe-pagination&page=1&pageSize=2");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(2, json.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, json.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, json.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetJobProgress_IncludesStepsAndStageTimings()
    {
        var started = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        jobs.Create(new Job
        {
            Id = "job-progress-api",
            PipelineId = "pipe-progress-api",
            Status = JobStatus.Running,
            CurrentStage = PipelineStage.Ingest,
            StageStatus = new() { ["ingest"] = "running", ["sql"] = "pending", ["reconcile"] = "pending" },
            StageTimings = new() { ["ingest"] = new StageTiming { StartedAt = started } },
        });
        var progress = _factory.Services.GetRequiredService<Sluice.Portal.Services.Pipeline.JobProgressStore>()
            .Begin("pipe-progress-api", "job-progress-api", "ingest");
        progress.StartStep(JobStepCatalog.Snapshots, total: 4);
        progress.Report(JobStepCatalog.Snapshots, 2, force: true);

        var response = await _client.GetAsync("/api/jobs/job-progress-api/progress");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 70 · (78 · 2/4) / 100 ≈ 27
        Assert.Equal(27, json.GetProperty("percent").GetInt32());
        Assert.Equal(JobStepCatalog.Snapshots, json.GetProperty("currentStep").GetString());
        var snapshots = json.GetProperty("steps").GetProperty(JobStepCatalog.Snapshots);
        Assert.Equal(2, snapshots.GetProperty("done").GetInt32());
        Assert.Equal(4, snapshots.GetProperty("total").GetInt32());
        Assert.Equal(started, json.GetProperty("stageTimings").GetProperty("ingest").GetProperty("startedAt").GetDateTimeOffset());
        // Campos previos intactos.
        Assert.Equal("job-progress-api", json.GetProperty("jobId").GetString());
        Assert.True(json.TryGetProperty("entities", out _));
    }

    [Fact]
    public async Task GetJobs_PageSizeAboveMax_IsClamped()
    {
        var response = await _client.GetAsync("/api/jobs?pageSize=99999");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(200, json.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task CreatePipeline_InvalidSourceId_ReturnsProblemDetails()
    {
        var response = await _client.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "pipe-invalid-source",
            Name = "Invalid",
            SourceId = "does-not-exist",
            DestinationId = "does-not-exist",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task ConnectionCreate_rejects_traversal_id_with_400()
    {
        var response = await _client.PostAsJsonAsync("/api/connections", CreateConnection("../../evil"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConnectionCreate_with_existing_id_returns_409_and_keeps_original()
    {
        var id = $"conn-dup-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/connections", CreateConnection(id, "original"))).StatusCode);

        var second = await _client.PostAsJsonAsync("/api/connections", CreateConnection(id, "impostor"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var loaded = await _client.GetFromJsonAsync<Connection>($"/api/connections/{id}", JsonDefaults.Options);
        Assert.Equal("original", loaded!.Name);
    }

    // %5C = '\': a diferencia de %2F, sí llega decodificado al valor de ruta.
    [Theory]
    [InlineData("/api/connections/..%5C..%5Csettings")]
    [InlineData("/api/pipelines/..%5C..%5Csettings")]
    [InlineData("/api/pipelines/..%5C..%5Csettings/status")]
    public async Task Get_with_traversal_id_returns_404(string url)
    {
        var response = await _client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PipelineCreate_rejects_traversal_id_with_400()
    {
        // Conexiones reales: así solo el id puede provocar el rechazo (no ValidateRefs).
        var suffix = Guid.NewGuid().ToString("N");
        await _client.PostAsJsonAsync("/api/connections", CreateConnection($"conn-src-{suffix}"));
        await _client.PostAsJsonAsync("/api/connections", CreateConnection($"conn-dst-{suffix}") with
        {
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
        });

        var response = await _client.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "..\\..\\evil",
            Name = "evil",
            SourceId = $"conn-src-{suffix}",
            DestinationId = $"conn-dst-{suffix}",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Connection CreateConnection(string id, string name = "Test") => new()
    {
        Id = id,
        Name = name,
        Role = ConnectionRole.Source,
        Type = "adls",
        Config = new Dictionary<string, string?>(),
    };

    [Fact]
    public async Task ConnectionCrud_Works()
    {
        var connection = new Connection
        {
            Id = "conn-test-001",
            Name = "Test ADLS",
            Role = ConnectionRole.Source,
            Type = "adls",
            Config = new Dictionary<string, string?> { ["password"] = "secret" },
        };

        var create = await _client.PostAsJsonAsync("/api/connections", connection);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var get = await _client.GetAsync("/api/connections/conn-test-001");
        get.EnsureSuccessStatusCode();
        var loaded = await get.Content.ReadFromJsonAsync<Connection>(JsonDefaults.Options);
        Assert.NotNull(loaded);
        Assert.Equal("***", loaded!.Config["password"]);

        var delete = await _client.DeleteAsync("/api/connections/conn-test-001");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    // La guarda anti-doble-run era "¿hay un job en curso?" + "créalo" sin atomicidad, y el id
    // del job tenía resolución de segundo: varios Run simultáneos arrancaban (y se pisaban).
    // Instancia propia solo-orquestador (distributed sin WORKER_STAGE): sin workers el job
    // sigue en curso durante la ráfaga, así que debe arrancar exactamente uno.
    [Fact]
    public async Task PipelineRun_concurrentes_solo_arranca_uno()
    {
        using var factory = new OrchestratorOnlyFactory();
        var client = factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/connections", CreateConnection("conn-src-race"));
        await client.PostAsJsonAsync("/api/connections", CreateConnection("conn-dst-race") with
        {
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
        });
        await client.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "pipe-race",
            Name = "Race",
            SourceId = "conn-src-race",
            DestinationId = "conn-dst-race",
        });

        var runs = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.PostAsync("/api/pipelines/pipe-race/run", null)));

        Assert.Equal(1, runs.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.All(runs.Where(r => r.StatusCode != HttpStatusCode.Accepted),
            r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }

    private sealed class OrchestratorOnlyFactory : ApiWebApplicationFactory
    {
        protected override IReadOnlyDictionary<string, string?> Settings =>
            new Dictionary<string, string?>(base.Settings) { ["EXECUTION_MODE"] = "distributed" };
    }

    [Fact]
    public async Task PipelineRun_EnqueuesIngestTask()
    {
        var src = new Connection
        {
            Id = "conn-src-api",
            Name = "Src",
            Role = ConnectionRole.Source,
            Type = "adls",
        };
        var dst = new Connection
        {
            Id = "conn-dst-api",
            Name = "Dst",
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
        };
        await _client.PostAsJsonAsync("/api/connections", src);
        await _client.PostAsJsonAsync("/api/connections", dst);

        var pipeline = new Pipeline
        {
            Id = "pipe-api-001",
            Name = "API Pipeline",
            SourceId = "conn-src-api",
            DestinationId = "conn-dst-api",
            Enabled = true,
        };
        await _client.PostAsJsonAsync("/api/pipelines", pipeline);

        var run = await _client.PostAsync("/api/pipelines/pipe-api-001/run", null);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);

        var queue = await _client.GetFromJsonAsync<JsonElement>("/api/orchestration/queue");
        // El worker local puede reclamar la tarea en cualquier momento, así que el reparto entre
        // estados es indeterminado; pero el ingest recién encolado tiene que figurar en alguno.
        var ingest = queue.GetProperty("ingest");
        var total = 0;
        foreach (var state in new[] { "pending", "claimed", "done", "error" })
        {
            var count = ingest.GetProperty(state);
            Assert.Equal(JsonValueKind.Number, count.ValueKind);
            total += count.GetInt32();
        }

        Assert.True(total >= 1, "La tarea de ingest encolada por el run no aparece en ningún estado de la cola.");
    }

    [Fact]
    public async Task RetryJob_NonExistent_ReturnsBadRequest()
    {
        var response = await _client.PostAsync("/api/jobs/job-does-not-exist/retry", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RetryJob_ErrorJob_CreatesNewRunAndKeepsHistory()
    {
        await _client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-src-retry", Name = "Src", Role = ConnectionRole.Source, Type = "adls",
        });
        await _client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-dst-retry", Name = "Dst", Role = ConnectionRole.Destination, Type = "sqlserver",
        });
        await _client.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "pipe-retry", Name = "Retry Pipeline",
            SourceId = "conn-src-retry", DestinationId = "conn-dst-retry",
        });

        // Inyecta un job fallido directamente en el store (el API no expone crear jobs en Error).
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        jobs.Create(new Job
        {
            Id = "job-failed-retry",
            PipelineId = "pipe-retry",
            Type = JobType.Incremental,
            Status = JobStatus.Error,
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "error", ["sql"] = "pending", ["reconcile"] = "pending",
            },
        });

        var response = await _client.PostAsync("/api/jobs/job-failed-retry/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Se crea una NUEVA ejecución (id distinto, mismo pipeline); el job fallido se conserva.
        var newJob = await response.Content.ReadFromJsonAsync<Job>(JsonDefaults.Options);
        Assert.NotNull(newJob);
        Assert.NotEqual("job-failed-retry", newJob!.Id);
        Assert.Equal("pipe-retry", newJob.PipelineId);
        Assert.NotNull(jobs.Get("job-failed-retry"));
    }

    [Fact]
    public async Task RetryJob_RunningJob_ReturnsBadRequest()
    {
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        jobs.Create(new Job
        {
            Id = "job-running-retry",
            PipelineId = "pipe-running-retry",
            Type = JobType.Incremental,
            Status = JobStatus.Running,
        });

        // Un job en ejecución no es reintentable.
        var response = await _client.PostAsync("/api/jobs/job-running-retry/retry", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task StorageOverview_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/storage/overview");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("pipelines", out _));
    }

    [Fact]
    public async Task RetentionPreview_MissingPipeline_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/pipelines/pipe-missing/snapshots/retention-preview");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReloadFromParquet_NoParquet_ReturnsBadRequest()
    {
        await _client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-src-reload-api", Name = "Src", Role = ConnectionRole.Source, Type = "adls",
        });
        await _client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-dst-reload-api", Name = "Dst", Role = ConnectionRole.Destination, Type = "sqlserver",
        });
        await _client.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "pipe-reload-api", Name = "Reload API",
            SourceId = "conn-src-reload-api", DestinationId = "conn-dst-reload-api",
        });

        var response = await _client.PostAsync("/api/pipelines/pipe-reload-api/reload-from-parquet", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RetryJob_ReloadFromParquet_CreatesReloadJobNotIncremental()
    {
        using var factory = new ParquetStubWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-src-retry-reload", Name = "Src", Role = ConnectionRole.Source, Type = "adls",
        });
        await client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-dst-retry-reload", Name = "Dst", Role = ConnectionRole.Destination, Type = "sqlserver",
        });
        await client.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "pipe-retry-reload", Name = "Retry Reload",
            SourceId = "conn-src-retry-reload", DestinationId = "conn-dst-retry-reload",
        });

        var jobs = factory.Services.GetRequiredService<IJobService>();
        jobs.Create(new Job
        {
            Id = "job-failed-reload-parquet",
            PipelineId = "pipe-retry-reload",
            Type = JobType.ReloadFromParquet,
            Status = JobStatus.Error,
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "skipped", ["sql"] = "error", ["reconcile"] = "pending",
            },
        });

        var response = await client.PostAsync("/api/jobs/job-failed-reload-parquet/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var newJob = await response.Content.ReadFromJsonAsync<Job>(JsonDefaults.Options);
        Assert.NotNull(newJob);
        Assert.Equal(JobType.Force, newJob!.Type);
        Assert.NotEqual("job-failed-reload-parquet", newJob.Id);
    }

    // Ingest terminó (el snapshot ya cuenta como procesado) pero SQL falló: un Incremental no
    // encontraría nada nuevo y reportaría Ok sin cargar los datos. El retry recarga SQL desde el
    // parquet consolidado, que ya contiene lo ingerido.
    [Fact]
    public async Task RetryJob_SqlFailedAfterIngest_ReloadsFromParquet()
    {
        using var factory = new ParquetStubWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-src-retry-sql", Name = "Src", Role = ConnectionRole.Source, Type = "adls",
        });
        await client.PostAsJsonAsync("/api/connections", new Connection
        {
            Id = "conn-dst-retry-sql", Name = "Dst", Role = ConnectionRole.Destination, Type = "sqlserver",
        });
        await client.PostAsJsonAsync("/api/pipelines", new Pipeline
        {
            Id = "pipe-retry-sql", Name = "Retry Sql",
            SourceId = "conn-src-retry-sql", DestinationId = "conn-dst-retry-sql",
        });

        var jobs = factory.Services.GetRequiredService<IJobService>();
        jobs.Create(new Job
        {
            Id = "job-failed-sql-stage",
            PipelineId = "pipe-retry-sql",
            Type = JobType.Incremental,
            Status = JobStatus.Error,
            StageStatus = new Dictionary<string, string>
            {
                ["ingest"] = "done", ["sql"] = "error", ["reconcile"] = "pending",
            },
        });

        var response = await client.PostAsync("/api/jobs/job-failed-sql-stage/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var newJob = await response.Content.ReadFromJsonAsync<Job>(JsonDefaults.Options);
        Assert.Equal(JobType.Force, newJob!.Type);
        Assert.NotEqual("job-failed-sql-stage", newJob.Id);
    }

}