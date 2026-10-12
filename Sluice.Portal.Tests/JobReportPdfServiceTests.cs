using Sluice.Portal.Models;
using Sluice.Portal.Services.Reporting;

namespace Sluice.Portal.Tests;

// Smoke de runtime del generador de PDF: QuestPDF puede compilar y aun así lanzar
// al generar (uso indebido de la API de layout, licencia sin configurar, etc.).
// Estos tests ejecutan GenerateEquityReport de verdad y validan que produce un PDF.
public class JobReportPdfServiceTests
{
    static JobReportPdfServiceTests()
    {
        // En el proceso de test Program.cs no se ejecuta; sin esta configuración (licencia y
        // fuentes) QuestPDF lanza al generar.
        JobReportPdfService.ConfigureQuestPdf();
    }

    private static Job SampleJob() => new()
    {
        Id = "job-123",
        PipelineId = "pipe-demo",
        Type = JobType.Force,
        Status = JobStatus.Partial,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        FinishedAt = DateTimeOffset.UtcNow,
        Entities =
        [
            new JobEntityResult { Entity = "account", CsvRows = 1000, SqlRows = 1000, Status = "ok", Watermark = DateTimeOffset.UtcNow },
            new JobEntityResult { Entity = "contact", CsvRows = 500, SqlRows = 480, Status = "partial" },
            new JobEntityResult { Entity = "lead", CsvRows = 0, SqlRows = null, Status = "error", Message = "boom" },
        ],
        Summary = new() { ["sqlMergedRows"] = 1480 },
    };

    [Fact]
    public void GenerateEquityReport_ConDatos_ProducePdfNoVacio()
    {
        var service = new JobReportPdfService();

        var pdf = service.GenerateEquityReport(SampleJob(), reconcile: null, diff: null);

        Assert.NotNull(pdf);
        Assert.True(pdf.Length > 1000, $"PDF demasiado pequeño: {pdf.Length} bytes");
        // Cabecera de archivo PDF: "%PDF".
        Assert.Equal((byte)'%', pdf[0]);
        Assert.Equal((byte)'P', pdf[1]);
        Assert.Equal((byte)'D', pdf[2]);
        Assert.Equal((byte)'F', pdf[3]);
    }

    [Fact]
    public void GenerateEquityReport_SinEntidades_NoLanza()
    {
        var service = new JobReportPdfService();
        var job = SampleJob() with { Entities = [], Summary = new() };

        var pdf = service.GenerateEquityReport(job, reconcile: null, diff: null);

        Assert.True(pdf.Length > 0);
    }

    // Un Incremental sin datos nuevos no compara nada: el PDF decía "Sin diferencias. Origen y
    // SQL coinciden." con Estado OK, afirmando una comprobación que no se hizo.
    [Fact]
    public void TextoCallout_sin_entidades_en_un_job_Ok_no_afirma_que_coinciden()
    {
        var texto = JobReportPdfService.TextoCallout(entidades: 0, conDiferencias: 0, JobStatus.Ok);

        Assert.DoesNotContain("coinciden", texto);
        Assert.Contains("no tuvo datos nuevos", texto);
        Assert.Equal("—", JobReportPdfService.TextoEstadoKpi(entidades: 0, JobStatus.Ok));
    }

    // Un job que falló antes de comparar no debe presentarse como "sin datos nuevos" ni ocultar su
    // estado en el KPI.
    [Fact]
    public void TextoCallout_sin_entidades_en_un_job_fallido_conserva_el_estado()
    {
        var texto = JobReportPdfService.TextoCallout(entidades: 0, conDiferencias: 0, JobStatus.Error);

        Assert.DoesNotContain("coinciden", texto);
        Assert.DoesNotContain("datos nuevos", texto);
        Assert.Equal("Error", JobReportPdfService.TextoEstadoKpi(entidades: 0, JobStatus.Error));
    }

    [Fact]
    public void TextoCallout_con_entidades_mantiene_los_mensajes_de_siempre()
    {
        Assert.Contains("coinciden", JobReportPdfService.TextoCallout(entidades: 2, conDiferencias: 0, JobStatus.Ok));
        Assert.Contains("diferencias en 1", JobReportPdfService.TextoCallout(entidades: 2, conDiferencias: 1, JobStatus.Partial));
        Assert.Equal("OK", JobReportPdfService.TextoEstadoKpi(entidades: 2, JobStatus.Ok));
    }

    // Formato es-ES fijo (sin depender de ICU ni de la cultura del proceso).
    [Fact]
    public void Formatos_numericos_son_es_ES()
    {
        Assert.Equal("1.682.160", JobReportPdfService.Entero(1682160));
        Assert.Equal("0", JobReportPdfService.Entero(0));
        Assert.Equal("+5.950", JobReportPdfService.ConSigno(5950));
        Assert.Equal("−120", JobReportPdfService.ConSigno(-120));
        Assert.Equal("0", JobReportPdfService.ConSigno(0));
        Assert.Equal("11,63 %", JobReportPdfService.Pct(11.63));
        Assert.Equal("0,01 %", JobReportPdfService.Pct(0.01));
        Assert.Equal("<0,01 %", JobReportPdfService.Pct(0.004));
        Assert.Equal("0,00 %", JobReportPdfService.Pct(0));
        Assert.Equal("8.076,92 %", JobReportPdfService.Pct(8076.92));
    }

    [Theory]
    [InlineData(7, "7 s")]
    [InlineData(362, "6 min 02 s")]
    [InlineData(3900, "1 h 05 min")]
    [InlineData(-5, "0 s")]
    public void Duracion_sigue_el_patron_de_JobFlowCanvas(int segundos, string esperado)
    {
        Assert.Equal(esperado, JobReportPdfService.Duracion(TimeSpan.FromSeconds(segundos)));
    }

    // Regresión: un job de tipo Reconcile deja job.Entities vacío; el detalle del PDF debe
    // construirse desde el reporte de reconcile (antes salía la tabla vacía). El reporte debe
    // generarse sin lanzar y con tamaño consistente con filas renderizadas.
    [Fact]
    public void GenerateEquityReport_JobReconcileSinEntidades_RenderizaDesdeReporte()
    {
        var service = new JobReportPdfService();
        var job = SampleJob() with { Type = JobType.Reconcile, Entities = [], Summary = new() };
        var reconcile = new ReconcileReport
        {
            Id = "recon-job-123",
            PipelineId = "pipe-demo",
            JobId = "job-123",
            MaxDivergencePct = 11.63,
            IsPartial = true,
            Entities =
            [
                new ReconcileEntityResult { Entity = "bankgroup", SourceRows = 43, DestinationRows = 38, DivergencePct = 11.63 },
                new ReconcileEntityResult { Entity = "account", SourceRows = 1000, DestinationRows = 1000, DivergencePct = 0 },
            ],
        };

        var pdfVacio = service.GenerateEquityReport(job, reconcile: null, diff: null);
        var pdfConReporte = service.GenerateEquityReport(job, reconcile, diff: null);

        // Con el reporte de reconcile el PDF trae 2 filas de detalle; sin él, tabla vacía.
        // El PDF con filas debe ser estrictamente mayor que el de "sin entidades".
        Assert.True(pdfConReporte.Length > pdfVacio.Length,
            $"Esperado PDF con detalle > vacío. conReporte={pdfConReporte.Length}, vacio={pdfVacio.Length}");
    }

    // Diff es una sola etapa (sin ingest ni SQL): job.Entities vacío y el detalle sale del DiffReport.
    [Fact]
    public void GenerateEquityReport_JobDiffSinEntidades_RenderizaDesdeElDiff()
    {
        var service = new JobReportPdfService();
        var job = SampleJob() with { Type = JobType.Diff, Status = JobStatus.Ok, Entities = [], Summary = new() };
        var diff = new DiffReport
        {
            Id = "diff-job-123",
            PipelineId = "pipe-demo",
            JobId = "job-123",
            Entities =
            [
                new DiffEntityResult { Entity = "bankgroup", SourceRows = 43, DestinationRows = 38, Delta = 5 },
                new DiffEntityResult { Entity = "account", SourceRows = 1000, DestinationRows = 1000, Delta = 0 },
            ],
        };

        var pdfVacio = service.GenerateEquityReport(job, reconcile: null, diff: null);
        var pdfConDiff = service.GenerateEquityReport(job, reconcile: null, diff);

        Assert.True(pdfConDiff.Length > pdfVacio.Length,
            $"Esperado PDF con detalle > vacío. conDiff={pdfConDiff.Length}, vacio={pdfVacio.Length}");
    }

    // Jobs.razor pasa GetReconcileByJob(id) ?? GetLatestReconcile(pipeline): para un Diff (que no
    // tiene reconcile propio) eso es el reporte de OTRO job. El PDF de un Diff sale de su diff.
    [Fact]
    public void GenerateEquityReport_JobDiff_IgnoraElReconcileDeOtroJob()
    {
        var service = new JobReportPdfService();
        var job = SampleJob() with { Type = JobType.Diff, Status = JobStatus.Ok, Entities = [], Summary = new() };
        var diff = new DiffReport
        {
            Id = "diff-job-123",
            PipelineId = "pipe-demo",
            JobId = "job-123",
            Entities = [new DiffEntityResult { Entity = "bankgroup", SourceRows = 43, DestinationRows = 38, Delta = 5 }],
        };
        var reconcileDeOtroJob = new ReconcileReport
        {
            Id = "recon-job-viejo",
            PipelineId = "pipe-demo",
            JobId = "job-viejo",
            MaxDivergencePct = 99.99,
            IsPartial = true,
            Entities =
            [
                new ReconcileEntityResult { Entity = "bankgroup", SourceRows = 999999, DestinationRows = 1, DivergencePct = 99.99 },
                new ReconcileEntityResult { Entity = "otra-tabla-del-otro-job", SourceRows = 777777, DestinationRows = 1, DivergencePct = 99.99 },
            ],
        };

        var soloDiff = service.GenerateEquityReport(job, reconcile: null, diff);
        var conReconcileViejo = service.GenerateEquityReport(job, reconcileDeOtroJob, diff);

        // Mismo contenido: la fila extra del otro job y sus conteos no deben aparecer.
        Assert.Equal(soloDiff.Length, conReconcileViejo.Length);
    }
}
