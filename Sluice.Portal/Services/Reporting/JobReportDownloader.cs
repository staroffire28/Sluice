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
    IActivityLog activityLog,
    IStringLocalizer<SharedResource> l)
{
    public async Task DownloadAsync(Job job)
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
            var bytes = await Task.Run(() => pdf.GenerateEquityReport(job, reconcile, diff));
            var base64 = Convert.ToBase64String(bytes);
            await js.InvokeVoidAsync("sluiceUi.downloadFileBase64", $"reporte-{job.Id}.pdf", base64, "application/pdf");
            await notifications.ShowSuccessToastAsync(l["Page.Jobs.ReportReady"].Value);
            activityLog.LogUser("job.report", $"Generó el reporte PDF del job {job.Id}.", job.PipelineId, job.Id);
        }
        catch (Exception ex)
        {
            await notifications.ShowErrorToastAsync(ex.Message);
        }
    }
}
