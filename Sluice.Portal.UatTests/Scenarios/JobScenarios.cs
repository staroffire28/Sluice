using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

// Ejecución de punta a punta: origen local simulado → parquet → SQL Server → conciliación.
[Trait("Area", "Jobs")]
public sealed class JobScenarios(UatAppFixture fx) : UatTest(fx)
{
    private static Task ExpectRowsAsync(IPage page, Journey journey, int first, int second) =>
        ExpectRowsAsync(page, journey, (journey.Entities[0], first), (journey.Entities[1], second));

    private static async Task ExpectRowsAsync(IPage page, Journey journey, params (string Entity, int Rows)[] expected)
    {
        var tables = new TablesPage(page);
        await tables.OpenAsync(journey.PipelineName);
        foreach (var (entity, rows) in expected)
        {
            await Assertions.Expect(tables.RowCount(entity)).ToHaveTextAsync(rows.ToString(), new LocatorAssertionsToHaveTextOptions { Timeout = Ui.Long });
        }
    }

    [UatSqlFact(DisplayName = "UAT-JOB-01 Primera carga completa")]
    public Task First_full_load() => RunAsync("UAT-JOB-01", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job01", Journeys.TenRows);

        await Journeys.RunAsync(page, journey);

        var detail = new JobDetailPage(page);
        await Assertions.Expect(detail.Metric(1)).ToHaveTextAsync("10");
        await Assertions.Expect(detail.Metric(2)).ToHaveTextAsync("10");
        await ExpectRowsAsync(page, journey, 6, 4);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-02 El lienzo recorre las etapas")]
    public Task Canvas_shows_every_stage_done() => RunAsync("UAT-JOB-02", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job02", Journeys.TenRows);

        await Journeys.RunAsync(page, journey);

        var detail = new JobDetailPage(page);
        foreach (var stage in new[] { "ingest", "sql", "reconcile" })
        {
            await Assertions.Expect(detail.Stage(stage)).ToHaveAttributeAsync("data-status", "done");
        }
    });

    [UatSqlFact(DisplayName = "UAT-JOB-03 Dónde se publica el parquet")]
    public Task Canvas_tells_where_the_parquet_is_published() => RunAsync("UAT-JOB-03", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job03", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        var detail = new JobDetailPage(page);
        await detail.SelectStepAsync("consolidate");

        await Assertions.Expect(detail.ParquetDetail).ToContainTextAsync($"{journey.ParquetFolder}/{journey.PipelineId}/");
        await Assertions.Expect(detail.ParquetDetail.Locator($"a[href='/parquet?pipeline={journey.PipelineId}']")).ToBeVisibleAsync();
    });

    [UatSqlFact(DisplayName = "UAT-JOB-04 Incremental con snapshot nuevo usa el delta")]
    public Task Incremental_with_new_snapshot_uses_the_delta() => RunAsync("UAT-JOB-04", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job04", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        // Segundo snapshot: actualiza las filas 4 y 5 y da de alta la 6 y la 7 de la primera entidad.
        journey.Folder.PublishSnapshot(new EntityRows(journey.Entities[0], 4, 4));
        await Journeys.RunAsync(page, journey);

        var detail = new JobDetailPage(page);
        await detail.ExpandTimelineAsync();
        await Assertions.Expect(detail.Timeline).ToContainTextAsync($"{journey.Entities[0]}: merged 4 (delta)");
        await ExpectRowsAsync(page, journey, 8, 4);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-05 Incremental sin datos nuevos")]
    public Task Incremental_without_new_data() => RunAsync("UAT-JOB-05", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job05", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        await Journeys.RunAsync(page, journey);

        await ExpectRowsAsync(page, journey, 6, 4);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-06 Forzar recarga SQL")]
    public Task Force_reloads_sql_from_parquet() => RunAsync("UAT-JOB-06", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job06", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        await Journeys.RunAsync(page, journey, RunKind.Force);

        await ExpectRowsAsync(page, journey, 6, 4);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-07 Reconciliar sin divergencia")]
    public Task Reconcile_without_divergence() => RunAsync("UAT-JOB-07", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job07", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        await Journeys.RunAsync(page, journey, RunKind.Reconcile);

        var detail = new JobDetailPage(page);
        await Assertions.Expect(detail.Metric(3)).ToHaveTextAsync("0.00 %");
        await Assertions.Expect(detail.Comparison).ToContainTextAsync(journey.Entities[0]);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-08 Diff")]
    public Task Diff_compares_every_entity() => RunAsync("UAT-JOB-08", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job08", Journeys.TenRows);
        await Journeys.RunAsync(page, journey);

        await Journeys.RunAsync(page, journey, RunKind.Diff);

        var detail = new JobDetailPage(page);
        await Assertions.Expect(detail.Comparison).ToContainTextAsync(journey.Entities[0]);
        await Assertions.Expect(detail.Comparison).ToContainTextAsync(journey.Entities[1]);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-09 Snapshot sin sellar no se procesa")]
    public Task Unsealed_snapshot_is_not_processed() => RunAsync("UAT-JOB-09", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job09", (folder, entities) =>
        {
            Journeys.TenRows(folder, entities);
            // Filas en la carpeta que el changelog marca como "en escritura".
            folder.WriteInProgressRows(new EntityRows(entities[0], 100, 5));
        });

        await Journeys.RunAsync(page, journey);

        await ExpectRowsAsync(page, journey, 6, 4);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-10 Entidad con filas irregulares")]
    public Task Ragged_rows_are_loaded() => RunAsync("UAT-JOB-10", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job10", (folder, entities) =>
            folder.PublishSnapshot(new EntityRows(entities[0], 0, 6, Ragged: true), new EntityRows(entities[1], 0, 4)));

        await Journeys.RunAsync(page, journey);

        await ExpectRowsAsync(page, journey, 6, 4);
    });

    [UatSqlFact(DisplayName = "UAT-JOB-11 Destino inalcanzable")]
    public Task Unreachable_destination_fails_with_a_visible_cause() => RunAsync("UAT-JOB-11", async page =>
    {
        var journey = await Journeys.CreateAsync(page, Fx, "job11", Journeys.TenRows, sqlHost: "sql-inexistente.invalid");

        await Journeys.RunAsync(page, journey, expected: "Error");

        var detail = new JobDetailPage(page);
        await Assertions.Expect(detail.Alert).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("retry-job")).ToBeVisibleAsync();
    });

    [UatSqlFact(DisplayName = "UAT-JOB-12 Reintentar tras arreglar el destino")]
    public Task Retry_after_fixing_the_destination() => RunAsync("UAT-JOB-12", async page =>
    {
        var sql = Fx.RequireSql();
        var journey = await Journeys.CreateAsync(page, Fx, "job12", Journeys.TenRows, sqlHost: "sql-inexistente.invalid");
        var failedJob = await Journeys.RunAsync(page, journey, expected: "Error");

        var destinations = new DestinationsPage(page);
        await destinations.OpenAsync();
        await destinations.EditAsync(journey.DestinationId);
        await page.FillFieldAsync("dst-host", sql.Host);
        await destinations.SaveAsync();

        var detail = new JobDetailPage(page);
        await detail.OpenAsync(failedJob);
        await detail.RetryAsync();
        await page.WaitForURLAsync(url => !url.EndsWith(failedJob, StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = Ui.Long });
        await detail.ExpectFinishedAsync("OK");
        await ExpectRowsAsync(page, journey, 6, 4);
    });
}
