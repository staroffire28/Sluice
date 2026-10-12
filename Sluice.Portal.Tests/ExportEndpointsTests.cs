using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;

namespace Sluice.Portal.Tests;

// Exportación CSV de Logs y Jobs: todos los registros que cumplen los filtros (no solo una página)
// y solo con sesión.
public sealed class ExportEndpointsTests : IClassFixture<ApiWebApplicationFactory>
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ExportEndpointsTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task Jobs_export_returns_every_job_matching_pipeline_and_dates()
    {
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        Create(jobs, "job-exp-a", "pipe-export", "2026-10-07T12:00:00Z");
        Create(jobs, "job-exp-b", "pipe-export", "2026-10-08T12:00:00Z");
        Create(jobs, "job-exp-c", "pipe-export", "2026-10-09T12:00:00Z");
        Create(jobs, "job-exp-other", "pipe-export-other", "2026-10-08T12:00:00Z");

        var response = await _client.GetAsync("/api/jobs/export?pipelineId=pipe-export&from=2026-10-08&to=2026-10-09");

        var lines = await ReadCsvAsync(response, "jobs-");
        Assert.Equal(3, lines.Length); // cabecera + b + c
        Assert.Equal(6, lines[0].Split(',').Length);
        Assert.StartsWith("job-exp-c,pipe-export,", lines[1]);
        Assert.StartsWith("job-exp-b,pipe-export,", lines[2]);
    }

    [Fact]
    public async Task Logs_export_applies_search_and_date_range()
    {
        var log = _factory.Services.GetRequiredService<IActivityLog>();
        log.LogUser("export.test", "mensaje exportable, con coma");
        log.LogUser("export.test", "otro mensaje sin la palabra");
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var yesterday = DateOnly.FromDateTime(DateTime.Now.AddDays(-1)).ToString("yyyy-MM-dd");

        var hit = await ReadCsvAsync(await _client.GetAsync($"/api/logs/export?search=exportable&from={today}&to={today}"), "logs-");
        var miss = await ReadCsvAsync(await _client.GetAsync($"/api/logs/export?search=exportable&to={yesterday}"), "logs-");

        Assert.Equal(2, hit.Length);
        Assert.Contains("\"mensaje exportable, con coma\"", hit[1]);
        Assert.Single(miss); // solo la cabecera
    }

    [Theory]
    [InlineData("/api/logs/export")]
    [InlineData("/api/jobs/export")]
    public async Task Export_without_session_is_unauthorized(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string[]> ReadCsvAsync(HttpResponseMessage response, string filePrefix)
    {
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith(filePrefix, response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]); // BOM para Excel
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private static void Create(IJobService jobs, string id, string pipelineId, string startedAt) =>
        jobs.Create(new Job
        {
            Id = id,
            PipelineId = pipelineId,
            Type = JobType.Incremental,
            Status = JobStatus.Ok,
            StartedAt = DateTimeOffset.Parse(startedAt, System.Globalization.CultureInfo.InvariantCulture),
        });
}
