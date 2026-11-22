using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: rápido a propósito (polling cada 60s del HealthPill, healthcheck de Docker
        // cada 30s, y arranque de e2e tests que esperan /health en un loop ajustado). Solo
        // confirma que el proceso responde y que DATA_DIR y su logs/ son escribibles — nunca toca SQL/ADLS. En el
        // orquestador añade, solo como indicadores, si el scheduler sigue sondeando y si queda poco
        // disco: "degraded" sigue siendo 200 (el contenedor no está caído, hay que mirarlo). También si
        // hay un pipeline o una conexión cuyo fichero no se puede leer (lo que dejaron los últimos
        // listados en UnreadableFileRegistry: no lee nada aquí); un job ilegible no degrada. Y si la
        // última entrega al webhook de alertas configurado falló (lee alerts/status.json, un fichero
        // pequeño): sin URL ni detalle, que llevan secretos o la respuesta del servidor remoto.
        app.MapGet("/health", (IOptions<AppOptions> options, SettingsStore settings, SchedulerHeartbeat heartbeat, IDiskSpaceProbe disk, UnreadableFileRegistry unreadable, AlertService alerts) =>
        {
            if (DataDirProbe.CheckWritable(options.Value.DataDir) is not null)
            {
                return LivenessResult(options.Value);
            }

            var schedulerStale = heartbeat.IsStale(settings.Get().SchedulerPollMinutes);
            var space = disk.Get(options.Value.DataDir);
            var diskLow = space?.IsLow == true;
            var configUnreadable = unreadable.Confirmed().Any(f => f.AffectsScheduling);
            var alertsFailing = alerts.FailingDelivery() is not null;
            return Results.Ok(new
            {
                status = schedulerStale || diskLow || configUnreadable || alertsFailing ? "degraded" : "ok",
                scheduler = schedulerStale ? "stale" : "ok",
                disk = space is null ? "unknown" : diskLow ? "low" : "ok",
                config = configUnreadable ? "unreadable" : "ok",
                alerts = alertsFailing ? "failing" : "ok",
            });
        });
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
