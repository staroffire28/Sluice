using System.Net;
using System.Net.Http.Json;
using Sluice.Portal.Models;

namespace Sluice.Portal.Tests;

public sealed class MonolithPipelineFlowTests : IClassFixture<MonolithE2EWebApplicationFactory>
{
    private readonly MonolithE2EWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MonolithPipelineFlowTests(MonolithE2EWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task IncrementalRun_CompletesIngestSqlReconcile_InMonolith()
    {
        await SeedPipelineAsync();

        var run = await _client.PostAsync("/api/pipelines/pipe-e2e-001/run", null);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var job = await run.Content.ReadFromJsonAsync<Job>();
        Assert.NotNull(job);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        Job? finalJob = null;
        while (DateTime.UtcNow < deadline)
        {
            finalJob = await _client.GetFromJsonAsync<Job>($"/api/jobs/{job!.Id}");
            if (finalJob?.Status is JobStatus.Ok or JobStatus.Partial or JobStatus.Error or JobStatus.Blocked)
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.NotNull(finalJob);
        Assert.Equal(JobStatus.Ok, finalJob!.Status);
        Assert.Equal("done", finalJob.StageStatus["ingest"]);
        Assert.Equal("done", finalJob.StageStatus["sql"]);
        Assert.Equal("done", finalJob.StageStatus["reconcile"]);
        Assert.NotNull(finalJob.FinishedAt);
    }

    // Reconcile solo verifica conteos (parquet consolidado vs SQL): una sola etapa, sin ingest
    // ni MERGE.
    [Fact]
    public async Task ReconcileRun_RunsOnlyTheReconcileStage_InMonolith()
    {
        await SeedPipelineAsync("pipe-e2e-recon");

        var run = await _client.PostAsync("/api/pipelines/pipe-e2e-recon/reconcile", null);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var job = await run.Content.ReadFromJsonAsync<Job>();

        var finalJob = await WaitFinishedAsync(job!.Id);

        Assert.Equal(JobStatus.Ok, finalJob!.Status);
        Assert.Equal("skipped", finalJob.StageStatus["ingest"]);
        Assert.Equal("skipped", finalJob.StageStatus["sql"]);
        Assert.Equal("done", finalJob.StageStatus["reconcile"]);
        Assert.DoesNotContain(finalJob.Events, e => e.Message.StartsWith("Fake ingest", StringComparison.Ordinal));
    }

    private async Task<Job?> WaitFinishedAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        Job? finalJob = null;
        while (DateTime.UtcNow < deadline)
        {
            finalJob = await _client.GetFromJsonAsync<Job>($"/api/jobs/{jobId}");
            if (finalJob?.FinishedAt is not null)
            {
                break;
            }

            await Task.Delay(500);
        }

        return finalJob;
    }

    private async Task SeedPipelineAsync(string pipelineId = "pipe-e2e-001")
    {
        var src = new Connection
        {
            Id = "conn-e2e-src",
            Name = "E2E Source",
            Role = ConnectionRole.Source,
            Type = "adls",
        };
        var dst = new Connection
        {
            Id = "conn-e2e-dst",
            Name = "E2E Destination",
            Role = ConnectionRole.Destination,
            Type = "sqlserver",
        };
        await _client.PostAsJsonAsync("/api/connections", src);
        await _client.PostAsJsonAsync("/api/connections", dst);

        var pipeline = new Pipeline
        {
            Id = pipelineId,
            Name = "E2E Pipeline",
            SourceId = "conn-e2e-src",
            DestinationId = "conn-e2e-dst",
            Enabled = true,
        };
        await _client.PostAsJsonAsync("/api/pipelines", pipeline);
    }
}