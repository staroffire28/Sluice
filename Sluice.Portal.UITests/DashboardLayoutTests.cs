using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;

namespace Sluice.Portal.UITests;

// Panel compacto: todo a la vista sin scroll en escritorio y sin desbordar en móvil.
[Collection("blazor-ui")]
public sealed class DashboardLayoutTests
{
    private const string ScrollSizes =
        "() => { const a = document.querySelector('.page-shell').parentElement; return [a.scrollHeight, a.clientHeight]; }";

    private readonly BlazorAppFixture _fx;

    public DashboardLayoutTests(BlazorAppFixture fx) => _fx = fx;

    private async Task<IPage> OpenAsync(int width, int height)
    {
        var context = await _fx.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(width, height);
        await page.GotoInteractiveAsync("/");
        await page.GetByTestId("dashboard-queue").WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        return page;
    }

    private static async Task AssertNoVerticalScrollAsync(IPage page, string scenario)
    {
        var sizes = await page.EvaluateAsync<int[]>(ScrollSizes);
        Assert.True(sizes[0] <= sizes[1], $"El Panel mide {sizes[0]}px en un área de {sizes[1]}px ({scenario}).");
    }

    // El área de contenido (el contenedor con scroll de FluentLayout) no necesita scroll vertical.
    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public async Task Dashboard_fits_the_screen_without_vertical_scroll(int width, int height)
    {
        var page = await OpenAsync(width, height);

        await AssertNoVerticalScrollAsync(page, $"{width}x{height}");
    }

    // A partir de 1280px el lienzo reservaba 320px para el panel de detalle, que el Panel
    // (modo compacto) no pinta: quedaba una franja vacía a la derecha en monitores anchos.
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task Compact_flow_canvas_uses_the_full_card_width(int width, int height)
    {
        var page = await OpenAsync(width, height);
        await Assertions.Expect(page.Locator(".jfc--compact").First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });

        var gap = await page.EvaluateAsync<int>("""
            () => {
                const canvas = document.querySelector('.jfc--compact');
                const lanes = canvas.querySelector('.jfc__lanes');
                return Math.round(canvas.getBoundingClientRect().right - lanes.getBoundingClientRect().right);
            }
            """);
        Assert.True(gap <= 24, $"Los carriles acaban {gap}px antes del borde del lienzo a {width}x{height}.");
    }

    [Fact]
    public async Task Dashboard_does_not_overflow_horizontally_on_a_phone()
    {
        var page = await OpenAsync(375, 812);

        var pageWidth = await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
        Assert.True(pageWidth <= 375, $"La página mide {pageWidth}px de ancho a 375px.");
    }

    // La cifra "Última ejecución" es un enlace al detalle del último job (el fixture siembra jobs).
    [Fact]
    public async Task Last_run_links_to_the_job_detail()
    {
        var page = await OpenAsync(1366, 768);

        await Assertions.Expect(page.GetByTestId("dashboard-last-run"))
            .ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex("^/jobs/"));
        await page.AssertNoBlazorErrorsAsync();
    }

    // Peor caso razonable a 1366x768: un job activo + 2 más, 4 workers vivos con nombres largos
    // (más el del propio fixture) y el aviso de changelog estancado. Ese aviso necesita ADLS,
    // que el fixture no tiene: se simula clonando una pista ya pintada en la cifra del snapshot.
    [Fact]
    public async Task Worst_case_dashboard_still_fits_without_vertical_scroll()
    {
        var jobFiles = new List<string>();
        var workerIds = new List<string>();
        var registry = new WorkerRegistry(Options.Create(new AppOptions { DataDir = _fx.DataDir }));
        try
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 3; i++)
            {
                // Pipelines inexistentes: no bloquean los pipelines que usan otros tests.
                var job = new Job
                {
                    Id = $"job-ui-busy-{i}",
                    PipelineId = $"pipe-ui-busy-{i}",
                    Type = JobType.Incremental,
                    Status = JobStatus.Running,
                    StartedAt = now.AddMinutes(-i - 1),
                    CurrentStage = PipelineStage.Ingest,
                    StageStatus = new Dictionary<string, string> { ["ingest"] = "running", ["sql"] = "pending", ["reconcile"] = "pending" },
                    Events = [new JobEvent { At = now, Message = "Ingest stage started." }],
                };
                var path = Path.Combine(_fx.DataDir, "jobs", job.Id + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(job, JsonDefaults.Options));
                jobFiles.Add(path);
            }

            foreach (var stage in new[] { PipelineStage.Ingest, PipelineStage.Sql, PipelineStage.Reconcile, PipelineStage.Ingest })
            {
                var id = $"worker-{stage.ToString().ToLowerInvariant()}-container-with-a-very-long-hostname-{workerIds.Count}";
                registry.RegisterHeartbeat(id, stage, "task-x", "pipe-ui-busy-0", "job-ui-busy-0");
                workerIds.Add(id);
            }

            var page = await OpenAsync(1366, 768);
            await Assertions.Expect(page.GetByTestId("dashboard-job")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15000 });
            await Assertions.Expect(page.GetByTestId("dashboard-more-jobs")).ToBeVisibleAsync();
            await AssertNoVerticalScrollAsync(page, "job activo + 2 más + workers largos");

            // Aviso de changelog: una pista más (larga) en la cifra del snapshot no debe añadir alto.
            await page.EvaluateAsync(@"() => {
                const hint = document.querySelector('.dash-stat__hint').cloneNode(true);
                hint.textContent = 'Changelog estancado: la exportación a ADLS puede estar bloqueada y este texto es largo a propósito';
                document.querySelector('[data-testid=dashboard-snapshot]').appendChild(hint);
            }");
            await AssertNoVerticalScrollAsync(page, "peor caso con aviso de changelog");

            // Los nombres largos se recortan dentro de su tarjeta (sin desbordar en horizontal).
            var overflowing = await page.EvaluateAsync<int>(
                "() => [...document.querySelectorAll('.dash-workers code')].filter(c => c.getBoundingClientRect().right > c.closest('section').getBoundingClientRect().right).length");
            Assert.Equal(0, overflowing);
        }
        finally
        {
            foreach (var file in jobFiles)
            {
                File.Delete(file);
            }

            foreach (var id in workerIds)
            {
                File.Delete(Path.Combine(_fx.DataDir, "workers", id + ".json"));
            }
        }
    }
}
