using System.Globalization;
using System.Resources;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Sluice.Portal.Models;

namespace Sluice.Portal.Services.Reporting;

public interface IJobReportPdfService
{
    // Genera el PDF del reporte "Equity" para un job. reconcile/diff pueden ser null. Textos y
    // números en la cultura indicada (por defecto, la actual) y fechas en la zona indicada (la del
    // navegador; por defecto, la del servidor).
    byte[] GenerateEquityReport(Job job, ReconcileReport? reconcile, DiffReport? diff,
        CultureInfo? cultura = null, TimeZoneInfo? zona = null);
}

public sealed class JobReportPdfService : IJobReportPdfService
{
    // --- Paleta: fondo blanco, un único acento de marca y grises para la jerarquía ---
    private const string Marca = "#1B365D";         // wordmark, sobretítulo, líneas, cabecera de tabla
    private const string Texto = "#1F2328";         // texto principal
    private const string Secundario = "#4B5563";    // subtítulo, "—" de la tabla, estado vacío
    private const string Terciario = "#5B6472";     // etiquetas, notas, pie, cabecera corrida
    private const string Superficie = "#F4F6F9";    // caja de metadatos
    private const string Blanco = "#FFFFFF";        // fondo de página y filas pares
    private const string Cebra = "#F7F9FB";         // filas alternas
    private const string CabeceraTabla = "#EDF1F6"; // fondo del encabezado de tabla
    private const string Filete = "#D9DEE5";        // bordes, pie, base de la banda KPI
    private const string FileteFila = "#E4E8EE";    // separadores de filas y divisores KPI

    // Tono semántico: color de texto, fondo, barra lateral y borde de pill.
    private sealed record Tono(string Texto, string Fondo, string Barra, string Borde);

    private static readonly Tono Verde = new("#1E6B34", "#E7F3EB", "#2E7D4F", "#B7D9C2");
    private static readonly Tono Ambar = new("#8A5300", "#FDF3E1", "#C98A12", "#EBCB8B");
    private static readonly Tono Rojo = new("#A4161A", "#FBEAEA", "#C62828", "#EDB5B5");
    private static readonly Tono Neutro = new("#4B5563", "#EEF0F3", "#9AA3AF", "#D5D9DF");

    // Única familia: Lato va embebida en QuestPDF, así que Windows y el contenedor Linux
    // generan exactamente el mismo PDF.
    private const string Fuente = "Lato";
    private const string Tnum = "tnum"; // cifras tabulares en todo texto numérico

    // A4 (595,28 pt) menos 51 pt por lado.
    private const float AnchoUtil = 595.28f - 2 * 51;

    // Desplazamiento de la pill en filas alineadas arriba para que su base cuadre con la del nombre.
    private const float PillAjusteArriba = 1f;

    // Textos del resx compartido (Report.*, Common.JobType.*, Common.JobStatus.*) en la cultura
    // del hilo, que GenerateEquityReport fija mientras genera: así los helpers siguen siendo
    // estáticos. Los números también salen con esa cultura.
    private static readonly ResourceManager Recursos =
        new("Sluice.Portal.Resources.SharedResource", typeof(SharedResource).Assembly);

    private static string T(string clave) => Recursos.GetString(clave, CultureInfo.CurrentUICulture) ?? clave;

    private static string T(string clave, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(clave), args);

    // Configuración global de QuestPDF; debe fijarse antes de generar cualquier documento.
    // Licencia Community (gratuita para ingresos < $1M USD/año). Solo la Lato embebida: un
    // FontFamily erróneo debe romper los tests, pero un glifo raro en datos externos (Message
    // de SQL) no debe tumbar el PDF.
    public static void ConfigureQuestPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = false;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = true;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    public byte[] GenerateEquityReport(Job job, ReconcileReport? reconcile, DiffReport? diff,
        CultureInfo? cultura = null, TimeZoneInfo? zona = null)
    {
        // Nunca lanzar por null: normalizamos las entradas.
        ArgumentNullException.ThrowIfNull(job);

        // La generación es síncrona: la cultura del hilo se fija solo mientras dura.
        var (culturaPrevia, culturaUiPrevia) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        if (cultura is not null)
        {
            CultureInfo.CurrentCulture = cultura;
            CultureInfo.CurrentUICulture = cultura;
        }

        try
        {
            return Generar(job, reconcile, diff, zona ?? TimeZoneInfo.Local);
        }
        finally
        {
            CultureInfo.CurrentCulture = culturaPrevia;
            CultureInfo.CurrentUICulture = culturaUiPrevia;
        }
    }

    private static byte[] Generar(Job job, ReconcileReport? reconcile, DiffReport? diff, TimeZoneInfo zona)
    {
        // Un Diff no tiene reconcile propio: cualquiera que llegue es de OTRO job y pisaría filas
        // y KPI. Su detalle es su diff.
        if (job.Type == JobType.Diff)
        {
            reconcile = null;
        }

        // Indexamos por entidad las tres fuentes (job/reconcile/diff), ignorando vacíos.
        var jobPorEntidad = (job.Entities ?? new())
            .Where(e => !string.IsNullOrWhiteSpace(e.Entity))
            .GroupBy(e => e.Entity!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var reconcilePorEntidad = (reconcile?.Entities ?? new())
            .Where(e => !string.IsNullOrWhiteSpace(e.Entity))
            .GroupBy(e => e.Entity!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var diffPorEntidad = (diff?.Entities ?? new())
            .Where(e => !string.IsNullOrWhiteSpace(e.Entity))
            .GroupBy(e => e.Entity!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Las filas se construyen desde la UNIÓN de entidades del job y de los reportes
        // reconcile/diff. Clave: un job de tipo Reconcile deja job.Entities vacío (solo
        // ingest/sql lo llenan), así que el detalle tiene que venir del reporte de reconcile.
        var nombres = jobPorEntidad.Keys
            .Union(reconcilePorEntidad.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(diffPorEntidad.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filas = nombres
            .Select(n => BuildFila(n, jobPorEntidad, reconcilePorEntidad, diffPorEntidad))
            .ToList();

        // KPIs agregados.
        var totalFilasOrigen = filas.Sum(f => f.FilasOrigen ?? 0); // solo filas con dato
        var totalFilasSql = filas.Sum(f => f.FilasSql ?? 0);
        var divergenciaMax = reconcile is not null
            ? reconcile.MaxDivergencePct
            : (filas.Count > 0 ? filas.Max(f => f.DivergenciaPct ?? 0) : 0d);
        var divergenciaMaxTexto = reconcile is not null || filas.Any(f => f.DivergenciaPct.HasValue)
            ? Pct(divergenciaMax)
            : "—";

        // Entidades con diferencias (para el callout).
        var conDiferencias = filas.Count(f => (f.DivergenciaPct ?? 0) > 0 || !EsOk(f.Estado));

        // Origen del detalle: decide el texto de metadatos y la nota al pie de la tabla.
        var fuente = reconcile is not null ? FuenteDetalle.Conciliacion
            : diff is not null ? FuenteDetalle.Diff
            : FuenteDetalle.Job;
        var fuenteTexto = (reconcile, diff) switch
        {
            (not null, not null) => T("Report.Source.Both"),
            (not null, null) => reconcile.IsPartial ? T("Report.Source.ReconcilePartial") : T("Report.Source.Reconcile"),
            (null, not null) => T("Report.Source.Diff"),
            _ => T("Report.Source.Job"),
        };

        var informe = new Informe(
            job, TipoTexto(job.Type), fuenteTexto, fuente, job.Type == JobType.Diff, filas,
            totalFilasOrigen, totalFilasSql, divergenciaMax, divergenciaMaxTexto,
            reconcile?.IsPartial == true, conDiferencias, zona);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Portrait());
                page.MarginHorizontal(51); // 18 mm redondeado a pt enteros: evita costuras en fondos contiguos
                page.MarginTop(14, Unit.Millimetre);
                page.MarginBottom(12, Unit.Millimetre);
                page.PageColor(Blanco);
                page.DefaultTextStyle(t => t.FontFamily(Fuente).FontSize(9).FontColor(Texto).LineHeight(1.25f));

                page.Header().Element(c => ComponerCabeceraCorrida(c, informe));
                page.Content().Element(c => ComponerContenido(c, informe));
                page.Footer().Element(ComponerPie);
            });
        })
        // Constante por job: el PDF de un Diff no puede depender de otra cosa que su propio diff.
        .WithMetadata(new DocumentMetadata { Title = $"Equity Report · {job.Id}", Author = "Sluice Portal" });

        return document.GeneratePdf();
    }

    // -------------------- CABECERA CORRIDA (todas las páginas) --------------------
    private static void ComponerCabeceraCorrida(IContainer container, Informe informe)
    {
        container.PaddingBottom(14).Column(col =>
        {
            col.Item().Row(row =>
            {
                row.AutoItem().AlignBottom().Text("SLUICE")
                    .FontSize(11).Black().FontColor(Marca).LetterSpacing(0.12f).LineHeight(1f); // 0,12: la extracción sigue dando "SLUICE"
                row.AutoItem().PaddingLeft(8).AlignBottom().PaddingBottom(0.5f).Text("EQUITY REPORT")
                    .FontSize(7.5f).SemiBold().FontColor(Terciario).LetterSpacing(0.08f).LineHeight(1f);

                // Página 1: fecha de generación. Resto: tipo e id del job para no perder el
                // contexto al imprimir hojas sueltas.
                row.RelativeItem().AlignRight().AlignBottom().PaddingBottom(0.5f).Column(der =>
                {
                    der.Item().ShowOnce().AlignRight()
                        .Text(T("Report.Generated", $"{Fecha(DateTimeOffset.UtcNow, informe.Zona)} ({Desfase(DateTimeOffset.UtcNow, informe.Zona)})"))
                        .FontSize(7.5f).FontColor(Terciario).LineHeight(1f).EnableFontFeature(Tnum);
                    der.Item().SkipOnce().AlignRight()
                        .Text($"{informe.TipoTexto} · {informe.Job.Id}")
                        .FontSize(7.5f).FontColor(Terciario).LineHeight(1f).EnableFontFeature(Tnum);
                });
            });

            col.Item().PaddingTop(5).LineHorizontal(1).LineColor(Marca);
        });
    }

    // -------------------- CONTENIDO --------------------
    private static void ComponerContenido(IContainer container, Informe informe)
    {
        container.Column(col =>
        {
            col.Item().Element(c => ComponerTitulo(c, informe));

            // Banda KPI + callout se leen juntos: nunca se parten entre páginas.
            col.Item().PaddingTop(12).ShowEntire().Column(resumen =>
            {
                resumen.Item().Element(c => ComponerBandaKpi(c, informe));
                resumen.Item().PaddingTop(10).Element(c => ComponerCallout(c, informe));
            });

            // El hueco superior va en el título de la sección y no aquí: un Padding sobre la tabla
            // se repetiría al principio de cada página.
            col.Item().EnsureSpace(90).Element(c => ComponerSeccionDetalle(c, informe));
        });
    }

    // Sobretítulo, H1 con tipo y pipeline, subtítulo y caja de metadatos del job.
    private static void ComponerTitulo(IContainer container, Informe informe)
    {
        var job = informe.Job;
        container.Column(col =>
        {
            col.Item().Text(T("Report.Overline"))
                .FontSize(7.5f).SemiBold().FontColor(Marca).LetterSpacing(0.08f);
            col.Item().PaddingTop(2).Text($"{informe.TipoTexto} · {job.PipelineId}")
                .FontSize(17).Bold().LineHeight(1.15f);
            col.Item().PaddingTop(3).Text(T("Report.Subtitle")).FontColor(Secundario);

            col.Item().PaddingTop(12)
                .Background(Superficie).Border(0.5f).BorderColor(Filete)
                .PaddingHorizontal(12).PaddingVertical(9)
                .Column(meta =>
                {
                    // El id del job (job-{pipeline}-{fecha}-{sufijo}) va solo, a todo el ancho,
                    // para que nunca se parta.
                    meta.Item().Row(row =>
                    {
                        row.ConstantItem(96).PaddingTop(1).Element(EtiquetaMeta).Text("Job");
                        row.RelativeItem().Text(job.Id).Medium().EnableFontFeature(Tnum);
                    });
                    meta.Item().PaddingTop(5).LineHorizontal(0.5f).LineColor(Filete);
                    meta.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(cols =>
                        {
                            cols.ConstantColumn(96);
                            cols.RelativeColumn();
                            cols.ConstantColumn(96);
                            cols.RelativeColumn();
                        });

                        var pares = Metadatos(informe);
                        foreach (var (etiqueta, valor, color) in pares)
                        {
                            table.Cell().PaddingVertical(2.5f).PaddingTop(1).Element(EtiquetaMeta).Text(etiqueta);
                            table.Cell().PaddingVertical(2.5f).Text(valor).FontColor(color).EnableFontFeature(Tnum);
                        }

                        // Número impar de pares: la última celda queda vacía.
                        if (pares.Count % 2 == 1)
                        {
                            table.Cell();
                            table.Cell();
                        }
                    });
                });
        });
    }

    private static IContainer EtiquetaMeta(IContainer c) =>
        c.DefaultTextStyle(s => s.FontSize(8).SemiBold().FontColor(Terciario));

    // Pares etiqueta/valor de la caja de metadatos, omitiendo los que no tienen dato. Summary se
    // lee siempre con JobSummary: un job leído de disco trae JsonElement, no int/long.
    private static List<(string Etiqueta, string Valor, string Color)> Metadatos(Informe informe)
    {
        var job = informe.Job;
        var pares = new List<(string, string, string)>
        {
            (T("Report.Meta.Start"), Fecha(job.StartedAt, informe.Zona), Texto),
            (T("Report.Meta.End"), job.FinishedAt is { } fin ? Fecha(fin, informe.Zona) : "—", Texto),
            // Sin fin no hay duración: medir contra "ahora" daría un número que cambia en cada descarga.
            (T("Report.Meta.Duration"), job.FinishedAt is { } f ? Duracion(f - job.StartedAt) : "—", Texto),
            (T("Report.Meta.Source"), informe.FuenteTexto, Texto),
            // Inicio y fin van en la zona de quien descarga el PDF: queda escrita una vez.
            (T("Report.Meta.TimeZone"), Desfase(job.StartedAt, informe.Zona), Texto),
        };

        if (JobSummary.GetLong(job, "snapshotsProcessed") is { } procesados)
        {
            var seleccionados = JobSummary.GetLong(job, "snapshotsSelected") ?? 0;
            var snapshots = seleccionados > 0 ? $"{Entero(procesados)} / {Entero(seleccionados)}" : Entero(procesados);
            if (JobSummary.GetString(job, "stoppedAtIncompleteSnapshot") is not null)
            {
                snapshots = T("Report.Meta.SnapshotsStopped", snapshots);
            }

            pares.Add(("Snapshots", snapshots, Texto));
        }

        if (JobSummary.GetLong(job, "csvRows") is { } csvRows)
        {
            pares.Add((T("Report.Meta.CsvRows"), Entero(csvRows), Texto));
        }

        if (JobSummary.GetLong(job, "sqlMergedRows") is { } fusionadas)
        {
            pares.Add((T("Report.Meta.MergedRows"), Entero(fusionadas), Texto));
        }

        if (JobSummary.GetLong(job, "entityFailures") is { } fallidas && fallidas > 0)
        {
            pares.Add((T("Report.Meta.FailedEntities"), Entero(fallidas), Rojo.Texto));
        }

        return pares;
    }

    // -------------------- BANDA KPI --------------------
    private static void ComponerBandaKpi(IContainer container, Informe informe)
    {
        var filas = informe.Filas;
        var n = filas.Count;

        // 1. Entidades. Un error no es una "diferencia": se cuentan aparte.
        var conError = filas.Count(f => EsError(f.Estado));
        var soloDiferencias = informe.ConDiferencias - conError;
        var partes = new List<string>();
        if (conError > 0) partes.Add(T("Report.Kpi.WithErrors", conError));
        if (soloDiferencias > 0) partes.Add(T("Report.Kpi.WithDifferences", soloDiferencias));
        var (notaEntidades, colorEntidades) = partes.Count > 0
            // Una línea por parte: "1 con error · 2 con diferencias" no cabe en la celda.
            ? (string.Join("\n", partes), conError > 0 ? Rojo.Texto : Ambar.Texto)
            : n > 0 ? (T("Report.Kpi.NoDifferences"), Verde.Texto) : (T("Report.Kpi.NoneCompared"), Terciario);

        // 2-3. Filas origen / SQL, con la diferencia neta como nota.
        var origen = n > 0 && filas.Any(f => f.FilasOrigen.HasValue) ? Entero(informe.TotalFilasOrigen) : "—";
        var haySql = n > 0 && filas.Any(f => f.FilasSql.HasValue);
        var sql = haySql ? Entero(informe.TotalFilasSql) : "—";
        (string? Texto, string Color) notaSql = (null, Terciario);
        if (haySql)
        {
            var neto = informe.TotalFilasOrigen - informe.TotalFilasSql;
            notaSql = neto > 0 ? (T("Report.Kpi.MissingInSql", Entero(neto)), Ambar.Texto)
                : neto < 0 ? (T("Report.Kpi.ExtraInSql", Entero(-neto)), Ambar.Texto)
                : (T("Report.Kpi.NoNetDifference"), Terciario);
        }

        // 4. Divergencia máxima (o delta total en un Diff).
        string etiqueta4, valor4, color4;
        (string? Texto, string Color) nota4 = (null, Terciario);
        if (informe.EsDiff)
        {
            var deltaTotal = JobSummary.GetLong(informe.Job, "diffTotalDelta")
                ?? (n > 0 ? filas.Sum(f => Math.Abs(f.Delta ?? 0)) : (long?)null);
            etiqueta4 = T("Report.Kpi.TotalDelta");
            valor4 = deltaTotal is { } d ? Entero(d) : "—";
            color4 = deltaTotal > 0 ? Ambar.Texto : Texto;
            if (n > 0)
            {
                // En dos líneas: en una sola no cabía y se recortaba.
                nota4 = ($"Σ|Delta|\n{T("Report.Kpi.MaxDivergenceShort", informe.DivergenciaMaxTexto)}", Terciario);
            }
        }
        else
        {
            etiqueta4 = T("Report.Kpi.MaxDivergence");
            valor4 = informe.DivergenciaMaxTexto;
            color4 = informe.DivergenciaMax > 0 && valor4 != "—" ? Ambar.Texto : Texto;
            if (informe.ConciliacionParcial)
            {
                nota4 = (T("Report.Kpi.PartialReconcile"), Ambar.Texto);
            }
            else if (informe.DivergenciaMax > 0 && n > 0)
            {
                nota4 = (filas.MaxBy(f => f.DivergenciaPct ?? 0)!.Entidad, Terciario);
            }
        }

        var valores = new[] { n.ToString(CultureInfo.InvariantCulture), origen, sql, valor4 };

        // Un solo tamaño para toda la banda, según el valor más largo; nunca se abrevia.
        var largo = valores.Max(v => v.Length);
        var tamano = largo <= 9 ? 16f : largo <= 11 ? 14f : 12f;

        var estadoKpi = TextoEstadoKpi(n, informe.Job.Status);

        container.Column(col =>
        {
            col.Item().LineHorizontal(1.25f).LineColor(Marca);
            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => CeldaKpi(c, false, T("Report.Kpi.Entities"),
                    v => ValorKpi(v, valores[0], tamano, Texto), notaEntidades, colorEntidades, lineasNota: 2));
                row.RelativeItem().Element(c => CeldaKpi(c, true, T("Report.Col.SourceRows"),
                    v => ValorKpi(v, origen, tamano, Texto), null, Terciario));
                row.RelativeItem().Element(c => CeldaKpi(c, true, T("Report.Col.SqlRows"),
                    v => ValorKpi(v, sql, tamano, Texto), notaSql.Texto, notaSql.Color));
                row.RelativeItem().Element(c => CeldaKpi(c, true, etiqueta4,
                    v => ValorKpi(v, valor4, tamano, color4), nota4.Texto, nota4.Color, lineasNota: informe.EsDiff ? 2 : 1));

                // Estado: pill con el estado del job; "—" cuando no hubo nada que comparar.
                // 92 pt (no 84): la pill a 9 pt de "En ejecución" no cabía.
                row.ConstantItem(92).Element(c =>
                {
                    if (estadoKpi == "—")
                    {
                        CeldaKpi(c, true, T("Report.Kpi.JobStatus"), v => ValorKpi(v, "—", 16, Terciario), T("Report.Kpi.NoComparison"), Terciario);
                    }
                    else
                    {
                        CeldaKpi(c, true, T("Report.Kpi.JobStatus"),
                            v => v.AlignLeft().AlignBottom().Element(p =>
                                Pill(p, estadoKpi, TonoJob(informe.Job.Status), tamano: 9, paddingH: 8, paddingV: 2.5f)),
                            null, Terciario);
                    }
                });
            });
            col.Item().LineHorizontal(0.75f).LineColor(Filete);
        });
    }

    private static void CeldaKpi(IContainer container, bool divisor, string etiqueta,
        Action<IContainer> valor, string? nota, string colorNota, int lineasNota = 1)
    {
        if (divisor)
        {
            container = container.BorderLeft(0.5f).BorderColor(FileteFila);
        }

        container.PaddingHorizontal(10).PaddingVertical(9).Column(col =>
        {
            col.Item().Text(etiqueta).FontSize(8).SemiBold().FontColor(Terciario).ClampLines(1, "…");
            col.Item().PaddingTop(3).Height(20).Element(valor);
            if (nota is not null)
            {
                col.Item().PaddingTop(2).Text(nota).FontSize(7.5f).FontColor(colorNota).EnableFontFeature(Tnum).ClampLines(lineasNota, "…");
            }
        });
    }

    // ScaleToFit: si aun así un valor no cabe, se reduce en vez de partirse en dos líneas.
    // "—" (sin dato) va en Regular y terciario para no competir con los valores reales.
    private static void ValorKpi(IContainer container, string valor, float tamano, string color)
    {
        var texto = container.ScaleToFit().AlignLeft().AlignBottom()
            .Text(valor).FontSize(tamano).LineHeight(1f).EnableFontFeature(Tnum);
        if (valor == "—")
        {
            texto.NormalWeight().FontColor(Terciario);
        }
        else
        {
            texto.Bold().FontColor(color);
        }
    }

    // -------------------- CALLOUT --------------------
    private static void ComponerCallout(IContainer container, Informe informe)
    {
        var n = informe.Filas.Count;
        var estado = informe.Job.Status;
        var tono = estado is JobStatus.Error or JobStatus.Blocked ? Rojo
            : informe.ConDiferencias > 0 ? Ambar
            : n == 0 ? Neutro
            : Verde;

        container.Row(row =>
        {
            row.ConstantItem(3).Background(tono.Barra);
            row.RelativeItem().Background(tono.Fondo).PaddingVertical(7).PaddingHorizontal(10)
                .Text((estado is JobStatus.Error or JobStatus.Blocked ? T("Report.Callout.JobFailed") + " " : "")
                    + TextoCallout(n, informe.ConDiferencias, estado))
                .LineHeight(1.3f).FontColor(tono.Texto);
        });
    }

    // Sin entidades no hubo comparación: decir "coinciden" afirmaría una comprobación que no se
    // hizo. En un job Ok es que no había datos nuevos; en uno fallido, que no llegó a comparar.
    internal static string TextoCallout(int entidades, int conDiferencias, JobStatus estadoJob)
    {
        if (conDiferencias > 0)
        {
            return conDiferencias == 1 ? T("Report.Callout.DifferencesOne") : T("Report.Callout.DifferencesMany", conDiferencias);
        }

        if (entidades == 0)
        {
            return estadoJob == JobStatus.Ok
                ? T("Report.Callout.NoNewData")
                : T("Report.Callout.NothingCompared");
        }

        return T("Report.Callout.AllMatch");
    }

    // Un job Ok sin entidades no validó nada: "OK" en el KPI se leería como "todo cuadra".
    internal static string TextoEstadoKpi(int entidades, JobStatus estadoJob) =>
        entidades == 0 && estadoJob == JobStatus.Ok ? "—" : EstadoTexto(estadoJob);

    // -------------------- DETALLE POR ENTIDAD --------------------
    private static void ComponerSeccionDetalle(IContainer container, Informe informe)
    {
        var filas = informe.Filas;
        container.Column(col =>
        {
            col.Item().PaddingTop(18).PaddingBottom(6).Row(row =>
            {
                row.AutoItem().Text(T("Report.Detail.Title")).FontSize(11).Bold();
                if (filas.Count > 0)
                {
                    row.RelativeItem().AlignRight().AlignBottom()
                        .Text(filas.Count == 1 ? T("Report.Detail.CountOne") : T("Report.Detail.CountMany", filas.Count))
                        .FontSize(8).FontColor(Terciario).EnableFontFeature(Tnum);
                }
            });

            // Sin entidades no hay tabla: una cabecera sobre nada sugiere datos perdidos.
            if (filas.Count == 0)
            {
                col.Item().Border(0.5f).BorderColor(Filete).Padding(14).AlignCenter()
                    .Text(T("Report.Detail.Empty")).FontColor(Secundario);
                return;
            }

            col.Item().Element(c => ComponerTabla(c, informe));
            col.Item().PaddingTop(8).Text(NotaAlPie(informe.Fuente))
                .FontSize(7.5f).LineHeight(1.35f).FontColor(Terciario);
        });
    }

    private static void ComponerTabla(IContainer container, Informe informe)
    {
        var esDiff = informe.EsDiff;

        // Tamaño de las cifras decidido para toda la tabla: si algún número largo no cabe a 9 pt,
        // todas las columnas numéricas bajan a 8 pt y siguen alineadas.
        var tamanoNum = informe.Filas.Any(f =>
            (f.FilasOrigen is { } o && Entero(o).Length > 11)
            || (f.FilasSql is { } s && Entero(s).Length > 11)
            || (f.Delta is { } d && ConSigno(d).Length > 11)
            || (f.DivergenciaPct is { } p && Pct(p).Length > 11)) ? 8f : 9f;

        // Igual con los nombres: si alguno no cabe a 9 pt en la columna Entidad, todos a 8,5 pt.
        // ScaleToFit queda solo como red para nombres que ni así caben (nunca se parten).
        var anchoEntidad = AnchoUtil - (esDiff ? 324 : 278) - 10;
        var tamanoNombre = informe.Filas.All(f => AnchoLato(f.Entidad, 9) <= anchoEntidad) ? 9f : 8.5f;

        container.Table(table =>
        {
            // Entidad ocupa el resto; las demás columnas tienen ancho fijo.
            var columnas = esDiff
                ? new (string Titulo, float Ancho, Alineacion Alineacion)[]
                {
                    (T("Report.Col.SourceRows"), 66, Alineacion.Derecha), (T("Report.Col.SqlRows"), 66, Alineacion.Derecha),
                    ("Delta", 66, Alineacion.Derecha), (T("Report.Col.Divergence"), 60, Alineacion.Derecha),
                    (T("Report.Col.Status"), 66, Alineacion.Centro),
                }
                : new (string Titulo, float Ancho, Alineacion Alineacion)[]
                {
                    (T("Report.Col.SourceRows"), 72, Alineacion.Derecha), (T("Report.Col.SqlRows"), 72, Alineacion.Derecha),
                    (T("Report.Col.Divergence"), 64, Alineacion.Derecha), (T("Report.Col.Status"), 70, Alineacion.Centro),
                };

            table.ColumnsDefinition(cols =>
            {
                cols.RelativeColumn();
                foreach (var columna in columnas)
                {
                    cols.ConstantColumn(columna.Ancho);
                }
            });

            // Se repite en cada página.
            // Se repite en cada página. Una sola celda con un único fondo: celdas contiguas con
            // fondo propio dejan costuras claras entre columnas al rasterizar.
            table.Header(header =>
            {
                header.Cell().ColumnSpan((uint)columnas.Length + 1)
                    .Background(CabeceraTabla).BorderBottom(0.75f).BorderColor(Marca)
                    .Row(row =>
                    {
                        row.RelativeItem().Element(c => TituloColumna(c, T("Report.Col.Entity"), Alineacion.Izquierda));
                        foreach (var columna in columnas)
                        {
                            row.ConstantItem(columna.Ancho).Element(c => TituloColumna(c, columna.Titulo, columna.Alineacion));
                        }
                    });
            });

            var indice = 0;
            foreach (var fila in informe.Filas)
            {
                var fondo = indice++ % 2 == 0 ? Blanco : Cebra;
                var tono = TonoEntidad(fila.Estado);

                // Con mensaje la fila crece: todo se alinea a la primera línea (el nombre).
                var conMensaje = !EsOk(fila.Estado) && !string.IsNullOrWhiteSpace(fila.Mensaje);

                CeldaCuerpo(table, fondo, conMensaje).Column(c =>
                {
                    // Siempre una línea: el nombre se busca y se copia tal cual (sin cortes ocultos).
                    c.Item().Height(tamanoNombre * 1.25f).ScaleToFit()
                        .Text(fila.Entidad).FontSize(tamanoNombre);
                    if (conMensaje)
                    {
                        c.Item().PaddingTop(1).Text(fila.Mensaje!)
                            .FontSize(7.5f).FontColor(tono.Texto).ClampLines(2, "…");
                    }
                });

                // Lo que hace que la entidad cuente "con diferencias" se marca en ámbar.
                CeldaNumero(table, fondo, conMensaje, fila.FilasOrigen is { } origen ? Entero(origen) : "—", tamanoNum);
                CeldaNumero(table, fondo, conMensaje, fila.FilasSql is { } sql ? Entero(sql) : "—", tamanoNum);
                if (esDiff)
                {
                    CeldaNumero(table, fondo, conMensaje, fila.Delta is { } delta ? ConSigno(delta) : "—", tamanoNum,
                        destacado: fila.Delta is { } dl && dl != 0);
                }

                CeldaNumero(table, fondo, conMensaje, fila.DivergenciaPct is { } pct ? Pct(pct) : "—", tamanoNum,
                    destacado: fila.DivergenciaPct > 0);

                var estadoTexto = EstadoEntidadTexto(fila.Estado);
                var celdaEstado = CeldaCuerpo(table, fondo, conMensaje).AlignCenter();
                if (estadoTexto == "—")
                {
                    celdaEstado.Text("—").FontColor(Secundario);
                }
                else
                {
                    // Arriba, la pill baja un poco para que su línea base cuadre con la del nombre.
                    celdaEstado.PaddingTop(conMensaje ? PillAjusteArriba : 0).Element(p => Pill(p, estadoTexto, tono));
                }
            }
        });
    }

    private enum Alineacion { Izquierda, Derecha, Centro }

    private static void TituloColumna(IContainer container, string texto, Alineacion alineacion)
    {
        var celda = container.PaddingVertical(5).PaddingHorizontal(5);
        celda = alineacion switch
        {
            Alineacion.Derecha => celda.AlignRight(),
            Alineacion.Centro => celda.AlignCenter(),
            _ => celda,
        };
        celda.Text(texto).FontSize(8).SemiBold().FontColor(Marca);
    }

    private static IContainer CeldaCuerpo(TableDescriptor table, string fondo, bool arriba = false)
    {
        var celda = table.Cell().Background(fondo)
            .BorderBottom(0.5f).BorderColor(FileteFila)
            .PaddingVertical(4).PaddingHorizontal(5);
        return arriba ? celda.AlignTop() : celda.AlignMiddle();
    }

    private static void CeldaNumero(TableDescriptor table, string fondo, bool arriba, string valor, float tamano,
        bool destacado = false) =>
        CeldaCuerpo(table, fondo, arriba).AlignRight().Text(valor)
            .FontSize(tamano)
            .FontColor(valor == "—" ? Secundario : destacado ? Ambar.Texto : Texto)
            .EnableFontFeature(Tnum);

    // Etiqueta de estado: fondo y borde suaves del tono, texto del tono. En la tabla 7,5 pt; en el
    // KPI de estado, más grande.
    private static void Pill(IContainer container, string texto, Tono tono,
        float tamano = 7.5f, float paddingH = 6, float paddingV = 1.5f) =>
        container.AlignCenter().AlignMiddle()
            .Background(tono.Fondo).Border(0.5f).BorderColor(tono.Borde).CornerRadius(2.5f)
            .PaddingHorizontal(paddingH).PaddingVertical(paddingV)
            .Text(texto).FontSize(tamano).SemiBold().LineHeight(1f).FontColor(tono.Texto);

    // Qué significa la columna Divergencia depende de dónde salen los recuentos.
    private static string NotaAlPie(FuenteDetalle fuente) => fuente switch
    {
        FuenteDetalle.Conciliacion => T("Report.Note.Reconcile"),
        FuenteDetalle.Diff => T("Report.Note.Diff"),
        _ => T("Report.Note.Job"),
    };

    // -------------------- PIE --------------------
    private static void ComponerPie(IContainer container)
    {
        // Aire sobre el filete para que la última fila de la tabla no lo toque.
        container.PaddingTop(8).Column(col =>
        {
            col.Item().LineHorizontal(0.5f).LineColor(Filete);
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text("Sluice Portal · Equity Report")
                    .FontSize(7.5f).FontColor(Terciario);
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(Terciario).EnableFontFeature(Tnum));
                    text.Span(T("Report.Footer.Page") + " ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });
    }

    // -------------------- Helpers de datos --------------------

    // Calcula la fila de detalle para una entidad combinando job + reconcile + diff. La entidad
    // puede NO estar en job.Entities (p. ej. job de tipo Reconcile): en ese caso los conteos y el
    // estado salen del reporte de reconcile/diff.
    private static FilaEntidad BuildFila(
        string nombre,
        IReadOnlyDictionary<string, JobEntityResult> jobPorEntidad,
        IReadOnlyDictionary<string, ReconcileEntityResult> reconcilePorEntidad,
        IReadOnlyDictionary<string, DiffEntityResult> diffPorEntidad)
    {
        jobPorEntidad.TryGetValue(nombre, out var e); // puede ser null
        long filasOrigen = e?.CsvRows ?? 0;        // filas de origen (CSV)
        long? filasSql = e?.SqlRows;               // filas en SQL destino (puede ser null)
        var estado = e?.Status ?? string.Empty;

        double? divergencia = null;
        long? delta = null;
        if (reconcilePorEntidad.TryGetValue(nombre, out var rec))
        {
            // Si hay reconcile, usamos su divergencia y sus conteos (más fiables).
            divergencia = rec.DivergencePct;
            filasOrigen = rec.SourceRows;
            filasSql = rec.DestinationRows;
            if (string.IsNullOrWhiteSpace(estado))
            {
                estado = rec.DivergencePct > 0 ? "diferencia" : "ok";
            }
        }
        else if (diffPorEntidad.TryGetValue(nombre, out var df))
        {
            // Sin reconcile: calculamos un delta porcentual simple desde el diff.
            filasOrigen = df.SourceRows;
            filasSql = df.DestinationRows;
            delta = df.Delta;
            divergencia = df.SourceRows != 0
                ? Math.Abs(df.Delta) * 100d / df.SourceRows
                : (df.Delta != 0 ? 100d : 0d);
            if (string.IsNullOrWhiteSpace(estado))
            {
                estado = divergencia > 0 ? "diferencia" : "ok";
            }
        }
        else if (filasSql.HasValue)
        {
            // Fallback final: delta simple entre origen y SQL del propio job.
            var diferencia = Math.Abs(filasOrigen - filasSql.Value);
            divergencia = filasOrigen != 0
                ? diferencia * 100d / filasOrigen
                : (diferencia != 0 ? 100d : 0d);
        }

        // Una entidad en error sin recuento SQL no llegó a contarse: su "0" de origen no es un dato.
        long? origenMostrado = EsError(estado) && filasSql is null ? null : filasOrigen;

        return new FilaEntidad(string.IsNullOrWhiteSpace(nombre) ? "—" : nombre,
            origenMostrado, filasSql, divergencia, estado, e?.Message, delta);
    }

    // 1.682.160 (es) / 1,682,160 (en).
    internal static string Entero(long n) => n.ToString("#,0", CultureInfo.CurrentCulture);

    // +5.950 / −120 / 0 (signo menos tipográfico U+2212).
    internal static string ConSigno(long n) => n.ToString("+#,0;−#,0;0", CultureInfo.CurrentCulture);

    // 11,63 % / 8.076,92 %; por debajo de 0,005 se muestra "<0,01 %" para no presentar como 0 una diferencia real.
    internal static string Pct(double p) =>
        (p > 0 && p < 0.005 ? "<" + 0.01.ToString("0.00", CultureInfo.CurrentCulture) : p.ToString("#,0.00", CultureInfo.CurrentCulture)) + " %";

    // Mismo patrón que JobFlowCanvas.FormatDuration: "7 s", "6 min 02 s", "1 h 05 min".
    internal static string Duracion(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
        {
            d = TimeSpan.Zero;
        }

        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours} h {d.Minutes:00} min"
            : d.TotalMinutes >= 1
                ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s"
                : $"{(int)d.TotalSeconds} s";
    }

    // Fechas en la zona de quien descarga el PDF; la zona se escribe aparte con Desfase.
    private static string Fecha(DateTimeOffset fecha, TimeZoneInfo zona) =>
        TimeZoneInfo.ConvertTime(fecha, zona).ToString("g", CultureInfo.CurrentCulture);

    // "UTC+09:00": la zona tal como queda en esa fecha (con su horario de verano, si lo hay).
    private static string Desfase(DateTimeOffset fecha, TimeZoneInfo zona) =>
        "UTC" + TimeZoneInfo.ConvertTime(fecha, zona).ToString("zzz", CultureInfo.InvariantCulture);

    private static bool EsOk(string estado) =>
        estado.Equals("ok", StringComparison.OrdinalIgnoreCase);

    private static bool EsError(string estado) =>
        estado.Equals("error", StringComparison.OrdinalIgnoreCase);

    // Ancho aproximado de un texto en Lato Regular (avances de la propia fuente, en milésimas de
    // em, ASCII 32..126; sin kerning). Fuera de ASCII se asume un carácter ancho.
    private static readonly short[] AvancesLato =
    [
        256, 269, 370, 580, 580, 802, 712, 204, 267, 267, 425, 580, 227, 372, 236, 452,
        580, 580, 580, 580, 580, 580, 580, 580, 580, 580, 250, 262, 580, 580, 580, 448,
        836, 677, 646, 668, 760, 578, 566, 730, 764, 280, 422, 662, 514, 928, 764, 800,
        600, 800, 626, 542, 590, 736, 677, 1036, 649, 624, 602, 306, 452, 306, 580, 459,
        400, 497, 560, 478, 560, 528, 350, 520, 558, 240, 240, 508, 236, 822, 558, 567,
        560, 560, 364, 433, 358, 558, 516, 786, 498, 516, 452, 301, 250, 301, 580,
    ];

    internal static float AnchoLato(string texto, float tamano) =>
        texto.Sum(c => c is >= ' ' and <= '~' ? AvancesLato[c - ' '] : 800) * tamano / 1000f;

    // Mismas etiquetas que la UI (Common.JobType.* y Common.JobStatus.*); un valor sin clave sale
    // con su nombre.
    private static string TipoTexto(JobType tipo) =>
        Recursos.GetString($"Common.JobType.{tipo}", CultureInfo.CurrentUICulture) ?? tipo.ToString();

    private static string EstadoTexto(JobStatus estado) =>
        Recursos.GetString($"Common.JobStatus.{estado}", CultureInfo.CurrentUICulture) ?? estado.ToString();

    private static Tono TonoJob(JobStatus estado) => estado switch
    {
        JobStatus.Ok => Verde,
        JobStatus.Partial => Ambar,
        JobStatus.Error or JobStatus.Blocked => Rojo,
        _ => Neutro,
    };

    // Etiqueta legible para el estado de cada entidad (string libre del job).
    private static string EstadoEntidadTexto(string estado)
    {
        if (string.IsNullOrWhiteSpace(estado)) return "—";
        return estado switch
        {
            _ when estado.Equals("ok", StringComparison.OrdinalIgnoreCase) => EstadoTexto(JobStatus.Ok),
            _ when estado.Equals("partial", StringComparison.OrdinalIgnoreCase) => EstadoTexto(JobStatus.Partial),
            _ when estado.Equals("error", StringComparison.OrdinalIgnoreCase) => EstadoTexto(JobStatus.Error),
            _ when estado.Equals("diferencia", StringComparison.OrdinalIgnoreCase)
                || estado.Equals("diff", StringComparison.OrdinalIgnoreCase) => T("Report.EntityStatus.Difference"),
            _ => char.ToUpperInvariant(estado[0]) + estado[1..],
        };
    }

    private static Tono TonoEntidad(string estado)
    {
        if (string.IsNullOrWhiteSpace(estado)) return Neutro;
        if (EsOk(estado)) return Verde;
        if (estado.Equals("error", StringComparison.OrdinalIgnoreCase)) return Rojo;
        // Cualquier otro estado distinto de "ok" se trata como diferencia (ámbar).
        return Ambar;
    }

    private enum FuenteDetalle { Conciliacion, Diff, Job }

    // Datos ya calculados que comparten las secciones del documento.
    private sealed record Informe(
        Job Job,
        string TipoTexto,
        string FuenteTexto,
        FuenteDetalle Fuente,
        bool EsDiff,
        List<FilaEntidad> Filas,
        long TotalFilasOrigen,
        long TotalFilasSql,
        double DivergenciaMax,
        string DivergenciaMaxTexto,
        bool ConciliacionParcial,
        int ConDiferencias,
        TimeZoneInfo Zona);

    // Estructura interna para representar una fila del detalle por entidad.
    private sealed record FilaEntidad(
        string Entidad,
        long? FilasOrigen,
        long? FilasSql,
        double? DivergenciaPct,
        string Estado,
        string? Mensaje,
        long? Delta);
}
