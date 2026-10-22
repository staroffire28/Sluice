using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Reporting;

// Genera el reporte PDF de un job y lo descarga en el navegador: desde el menú de Jobs y desde el
// detalle del job. Scoped: usa el JS y los toasts del circuito.
public sealed class JobReportDownloader(
    IJobReportPdfService pdf,
    ReportStore reports,
    IJSRuntime js,
    INotificationService notifications,
    IAuditLog audit,
    IStringLocalizer<SharedResource> l,
    BrowserTime time)
{
    public async Task DownloadAsync(AuditActor actor, Job job)
    {
        // Toast de progreso: la generación + descarga ocurre del lado servidor.
        await notifications.ShowInfoToastAsync(l["Page.Jobs.ReportGenerating"].Value);
        try
        {
            // Solo los reportes de ESTE job (recon-/diff-{jobId}). Sin respaldo al último del
            // pipeline: sería de otro job y mezclaría sus filas y KPI en este PDF. Sin reporte
            // propio, el PDF sale de las entidades del job.
            var reconcile = reports.GetReconcileByJob(job.Id);
            var diff = reports.GetDiffByJob(job.Id);
            // La generación con QuestPDF es CPU-bound; fuera del hilo del circuito para
            // no bloquear el render (el toast "Generando…" se pinta y la UI sigue viva).
            await audit.AuditAsync(
                actor,
                "job.report",
                job.PipelineId,
                async () =>
                {
                    // El PDF sale en el idioma y la zona horaria de quien lo pide (desde Jobs la
                    // zona aún no se ha leído: InitAsync solo llama al navegador la primera vez).
                    await time.InitAsync();
                    var culture = CultureInfo.CurrentUICulture;
                    var bytes = await Task.Run(() => pdf.GenerateEquityReport(job, reconcile, diff, culture, time.Zone));
                    var base64 = Convert.ToBase64String(bytes);
                    await js.InvokeVoidAsync("sluiceUi.downloadFileBase64", l["Report.FileName", job.Id].Value, base64, "application/pdf");
                    return true;
                },
                _ => new AuditResult($"Generó el reporte PDF del job {job.Id}."),
                $"No se pudo generar el reporte PDF del job {job.Id}",
                jobId: job.Id);
            await notifications.ShowSuccessToastAsync(l["Page.Jobs.ReportReady"].Value);
        }
        catch (Exception ex)
        {
            await notifications.ShowErrorToastAsync(ex.Message);
        }
    }
}
