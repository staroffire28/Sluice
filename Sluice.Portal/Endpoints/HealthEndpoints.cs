using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services;

namespace Sluice.Portal.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: rápido a propósito (polling cada 60s del HealthPill, healthcheck de Docker
        // cada 30s, y arranque de e2e tests que esperan /health en un loop ajustado). Solo
        // confirma que el proceso responde y que DATA_DIR es escribible — nunca toca SQL/ADLS.
        app.MapLivenessEndpoint();
        app.MapGet("/internal/health", (IOptions<AppOptions> options) => LivenessResult(options.Value));

        // Readiness: además de la escritura en DATA_DIR, prueba conectividad real de ADLS/SQL
        // para cada pipeline habilitado. Es anónimo: devuelve solo booleanos (el detalle va al
        // log) y reutiliza el resultado unos segundos (ver ReadinessProbe).
        app.MapGet("/health/ready", async (ReadinessProbe probe, CancellationToken cancellationToken) =>
        {
            var report = await probe.CheckAsync(cancellationToken);
            return Results.Json(
                new
                {
                    status = report.Healthy ? "ok" : "degraded",
                    dataDir = report.DataDirOk ? "ok" : "error",
                    pipelines = report.Pipelines,
                },
                statusCode: report.Healthy ? 200 : 503);
        });

        return app;
    }

    // Lo único que mapea un worker: el healthcheck de Docker.
    public static IEndpointRouteBuilder MapLivenessEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", (IOptions<AppOptions> options) => LivenessResult(options.Value));
        return app;
    }

    private static IResult LivenessResult(AppOptions options) =>
        DataDirProbe.CheckWritable(options.DataDir) is null
            ? Results.Ok(new { status = "ok" })
            : Results.Json(new { status = "error", reason = "DATA_DIR not writable" }, statusCode: 503);
}
