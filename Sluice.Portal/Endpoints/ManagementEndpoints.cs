using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Localization;
using Sluice.Portal.Models;
using PipelineModel = Sluice.Portal.Models.Pipeline;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Endpoints;

public static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (IOrchestrationService orchestration, IJobService jobs) =>
        {
            var running = jobs.ListRunning();
            return Results.Ok(new
            {
                status = "ready",
                activeJobs = running.Count,
                orchestration = orchestration.GetMode(),
            });
        });

        api.MapGet("/orchestration/mode", (IOrchestrationService o) => Results.Ok(o.GetMode()));
        api.MapGet("/orchestration/queue", (IOrchestrationService o) => Results.Ok(o.GetQueueCounts()));
        api.MapGet("/orchestration/workers", (IOrchestrationService o) => Results.Ok(o.GetWorkers()));
        api.MapGet("/orchestration/active-jobs", (IOrchestrationService o) => Results.Ok(o.GetActiveJobs()));

        api.MapGet("/connections", (IConnectionService svc, ConnectionRole? role) => Results.Ok(svc.List(role)));
        api.MapGet("/connections/{id}", (string id, IConnectionService svc) =>
            svc.Get(id) is { } c ? Results.Ok(c) : Results.NotFound());
        api.MapPost("/connections", (Connection connection, IConnectionService svc) =>
        {
            try
            {
                return Results.Created($"/api/connections/{connection.Id}", svc.Create(connection));
            }
            catch (DuplicateIdException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid connection.");
            }
        });
        api.MapPut("/connections/{id}", (string id, Connection connection, IConnectionService svc) =>
            svc.Update(id, connection) is { } c ? Results.Ok(c) : Results.NotFound());
        api.MapDelete("/connections/{id}", (string id, IConnectionService svc) =>
        {
            if (!svc.Delete(id, out var error))
            {
                return error?.Contains("in use") == true
                    ? Results.Conflict(new { error })
                    : Results.NotFound();
            }

            return Results.NoContent();
        });
        api.MapPost("/connections/{id}/test", async (string id, IConnectionService svc, CancellationToken ct) =>
        {
            var (ok, message) = await svc.TestAsync(id, ct);
            return ok ? Results.Ok(new { ok, message }) : Results.BadRequest(new { ok, message });
        });

        api.MapGet("/pipelines", (IPipelineService svc) => Results.Ok(svc.List()));
        api.MapGet("/pipelines/{id}", (string id, IPipelineService svc) =>
            svc.Get(id) is { } p ? Results.Ok(p) : Results.NotFound());
        api.MapPost("/pipelines", (PipelineModel pipeline, IPipelineService svc) =>
        {
            try
            {
                return Results.Created($"/api/pipelines/{pipeline.Id}", svc.Create(pipeline));
            }
            catch (DuplicateIdException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid pipeline.");
            }
        });
        api.MapPut("/pipelines/{id}", (string id, PipelineModel pipeline, IPipelineService svc) =>
        {
            try
            {
                return svc.Update(id, pipeline) is { } p ? Results.Ok(p) : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid pipeline.");
            }
        });
        api.MapDelete("/pipelines/{id}", (string id, bool wipe, IPipelineService svc) =>
        {
            if (!svc.Delete(id, wipe, out var error))
            {
                return Results.NotFound(new { error });
            }

            return Results.NoContent();
        });
        // Reset para pruebas: limpia estado (LastProcessedSnapshot/tablas) y parquets
        // consolidados en ADLS; con truncateSql=true además vacía las tablas de destino en SQL.
        // No borra la configuración del pipeline (a diferencia de DELETE ?wipe=true).
        api.MapPost("/pipelines/{id}/reset", async (string id, bool truncateSql, IPipelineResetService svc, CancellationToken ct) =>
        {
            try
            {
                var result = await svc.ResetAsync(id, truncateSql, ct);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid operation.");
            }
        });
        api.MapGet("/pipelines/{id}/status", (string id, IPipelineService svc) =>
            svc.Get(id) is null ? Results.NotFound() : Results.Ok(svc.GetState(id)));
        api.MapGet("/pipelines/{id}/tables", (string id, IPipelineService svc) =>
            svc.Get(id) is null ? Results.NotFound() : Results.Ok(svc.GetTables(id)));
        api.MapGet("/pipelines/{id}/snapshots", async (string id, ISnapshotCatalogService snapshots, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await snapshots.GetCatalogAsync(id, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound, title: "Pipeline not found.");
            }
        });
        api.MapGet("/pipelines/{id}/snapshots/retention-preview", async (string id, ISnapshotCatalogService snapshots, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await snapshots.GetRetentionPreviewAsync(id, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound, title: "Pipeline not found.");
            }
        });
        api.MapGet("/storage/overview", async (IStorageOverviewService storage, CancellationToken ct) =>
            Results.Ok(await storage.GetOverviewAsync(ct)));
        api.MapPost("/pipelines/{id}/reload-from-parquet", async (string id, IOrchestrationService o, CancellationToken ct) =>
        {
            try
            {
                var job = await o.ReloadFromParquetAsync(id, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid operation.");
            }
        });
        api.MapPost("/pipelines/{id}/run", async (string id, JobType? type, IOrchestrationService o, CancellationToken ct) =>
        {
            try
            {
                var job = await o.RunPipelineAsync(id, type ?? JobType.Incremental, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid operation.");
            }
        });
        api.MapPost("/pipelines/{id}/reconcile", async (string id, IOrchestrationService o, CancellationToken ct) =>
        {
            try
            {
                var job = await o.RunPipelineAsync(id, JobType.Reconcile, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid operation.");
            }
        });
        api.MapPost("/pipelines/{id}/diff", async (string id, IOrchestrationService o, CancellationToken ct) =>
        {
            try
            {
                var job = await o.RunPipelineAsync(id, JobType.Diff, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid operation.");
            }
        });
        // Comparación de valores (hash por fila parquet vs SQL) de una tabla o, sin entity, de todas.
        api.MapPost("/pipelines/{id}/compare-values", async (string id, string? entity, IOrchestrationService o, CancellationToken ct) =>
        {
            try
            {
                var job = await o.CompareValuesAsync(id, entity, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid operation.");
            }
        });
        api.MapGet("/pipelines/{id}/reports/reconcile", (string id, ReportStore reports) =>
            reports.GetLatestReconcile(id) is { } r ? Results.Ok(r) : Results.NotFound());
        api.MapGet("/pipelines/{id}/reports/diff", (string id, ReportStore reports) =>
            reports.GetLatestDiff(id) is { } r ? Results.Ok(r) : Results.NotFound());
        api.MapGet("/pipelines/{id}/reports/values", (string id, ReportStore reports) =>
            reports.GetLatestValues(id) is { } r ? Results.Ok(r) : Results.NotFound());

        // Gestión del parquet durable en ADLS (Approach B): listar / descargar / borrar.
        api.MapGet("/pipelines/{id}/parquet", async (string id, IParquetArtifactService parquet, CancellationToken ct) =>
            Results.Ok(await parquet.ListAsync(id, ct)));
        api.MapGet("/pipelines/{id}/parquet/download", async (string id, string path, IParquetArtifactService parquet, CancellationToken ct) =>
        {
            var stream = await parquet.OpenAsync(id, path, ct);
            return stream is null
                ? Results.NotFound()
                : Results.Stream(stream, "application/octet-stream", path.Split('/').Last());
        });
        api.MapDelete("/pipelines/{id}/parquet", async (string id, string path, IParquetArtifactService parquet, CancellationToken ct) =>
            await parquet.DeleteAsync(id, path, ct)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "Ruta de parquet inválida para este pipeline." }));

        api.MapGet("/jobs", (int? page, int? pageSize, string? pipelineId, JobStatus? status, IJobService svc) =>
        {
            const int maxPageSize = 200;
            var effectivePage = Math.Max(1, page ?? 1);
            var effectivePageSize = Math.Clamp(pageSize ?? 20, 1, maxPageSize);
            var items = svc.List(effectivePage, effectivePageSize, pipelineId, status);
            var totalCount = svc.Count(pipelineId, status);
            var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)effectivePageSize);

            return Results.Ok(new
            {
                items,
                page = effectivePage,
                pageSize = effectivePageSize,
                totalCount,
                totalPages,
            });
        });
        // Paquete de la Logic App de alertas (Ajustes): Deploy/LogicApp en un .zip con su carpeta.
        api.MapGet("/settings/logic-app-package", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logic-app");
            if (!Directory.Exists(dir))
            {
                return Results.NotFound();
            }

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.GetFiles(dir).Order(StringComparer.Ordinal))
                {
                    zip.CreateEntryFromFile(file, $"{LogicAppPackageName}/{Path.GetFileName(file)}", CompressionLevel.Optimal);
                }
            }

            return Results.File(buffer.ToArray(), "application/zip", $"{LogicAppPackageName}.zip");
        });

        // Exportaciones CSV de Logs y Jobs: todos los registros que cumplen los filtros de la página,
        // no solo la página visible.
        api.MapGet("/logs/export", (string? source, string? level, string? search, DateOnly? from, DateOnly? to, IActivityLog log, IStringLocalizer<SharedResource> l) =>
        {
            var entries = log.Query(new ActivityLogFilter(NullIfBlank(source), NullIfBlank(level), NullIfBlank(search), new DateRange(from, to)));
            return Csv("logs", writer =>
            {
                CsvWriter.WriteRow(writer, [l["Page.Logs.Col.Date"].Value, l["Page.Logs.Col.Source"].Value, l["Page.Logs.Col.Level"].Value, l["Page.Logs.Col.Action"].Value, l["Page.Logs.Col.Message"].Value, l["Page.Logs.Col.Pipeline"].Value, l["Page.Logs.Col.Job"].Value]);
                foreach (var e in entries)
                {
                    var sourceLabel = e.Source == "user" ? l["Page.Logs.Source.User"].Value : l["Page.Logs.Source.System"].Value;
                    CsvWriter.WriteRow(writer, [CsvDate(e.Timestamp), sourceLabel, e.Level.ToLowerInvariant(), e.Action, e.Message, e.PipelineId, e.JobId]);
                }
            });
        });
        api.MapGet("/jobs/export", (string? pipelineId, JobStatus? status, DateOnly? from, DateOnly? to, IJobService svc, IStringLocalizer<SharedResource> l) =>
        {
            var jobs = svc.List(1, int.MaxValue, NullIfBlank(pipelineId), status, new DateRange(from, to));
            return Csv("jobs", writer =>
            {
                CsvWriter.WriteRow(writer, [l["Page.Jobs.ColumnJob"].Value, l["Common.Pipeline"].Value, l["Page.Jobs.ColumnType"].Value, l["Common.Status"].Value, l["Page.Jobs.ColumnStarted"].Value, l["Page.Jobs.ColumnFinished"].Value]);
                foreach (var j in jobs)
                {
                    CsvWriter.WriteRow(writer, [j.Id, j.PipelineId, JobTypeLabels.Get(l, j.Type), JobStatusLabels.Get(l, j.Status), CsvDate(j.StartedAt), j.FinishedAt is { } f ? CsvDate(f) : null]);
                }
            });
        });
        api.MapGet("/jobs/running", (IJobService svc) => Results.Ok(svc.ListRunning()));
        api.MapGet("/jobs/{id}", (string id, IJobService svc) =>
            svc.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());
        api.MapGet("/jobs/{id}/progress", (string id, IJobService svc, JobProgressStore progressStore) =>
        {
            var job = svc.Get(id);
            if (job is null)
            {
                return Results.NotFound();
            }

            // Ponderado por etapa (StageStatus) y por paso (progress.json), no por Job.Entities —
            // ver JobProgressHelper. currentStep/steps/stageTimings son aditivos.
            var progress = progressStore.Load(job.PipelineId, job.Id);
            var pct = JobProgressHelper.GetPercent(job, progress);
            return Results.Ok(new
            {
                jobId = id,
                status = job.Status,
                currentStage = job.CurrentStage,
                percent = pct,
                entities = job.Entities,
                currentStep = JobProgressHelper.IsActive(job) ? progress?.CurrentStep : null,
                steps = progress?.Steps,
                stageTimings = job.StageTimings,
            });
        });
        api.MapGet("/jobs/{id}/download", (string id, IJobService svc) =>
            svc.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());
        api.MapPost("/jobs/{id}/retry", async (string id, IOrchestrationService o, CancellationToken ct) =>
        {
            try
            {
                var job = await o.RetryJobAsync(id, ct);
                return Results.Accepted($"/api/jobs/{job.Id}", job);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid operation.");
            }
        });

        return app;
    }

    public const string LogicAppPackageName = "sluice-alertas-logicapp";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Hora local del servidor, como la muestran las tablas de la UI.
    private static string CsvDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // Se arma en memoria y se devuelve entero: Kestrel no admite escrituras síncronas en la respuesta.
    private static IResult Csv(string name, Action<TextWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = CsvWriter.Create(buffer))
        {
            write(writer);
        }

        return Results.File(buffer.ToArray(), "text/csv; charset=utf-8", $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
    }
}