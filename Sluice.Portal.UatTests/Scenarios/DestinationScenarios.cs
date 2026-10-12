using System.Text.RegularExpressions;
using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

[Trait("Area", "Destinations")]
public sealed class DestinationScenarios(UatAppFixture fx) : UatTest(fx)
{
    private const string Unreachable = "sql-inexistente.invalid";

    [Fact(DisplayName = "UAT-DST-01 Crear un destino SQL")]
    public Task Create_destination() => RunAsync("UAT-DST-01", async page =>
    {
        var name = Unique("Destino DST01");

        await new DestinationsPage(page).CreateAsync(name, Unreachable, "SynapseLink", "sa", "clave");

        await Assertions.Expect(page.Row(name)).ToContainTextAsync("sqlserver");
    });

    [UatSqlFact(DisplayName = "UAT-DST-02 Probar un destino correcto")]
    public Task Test_reachable_destination() => RunAsync("UAT-DST-02", async page =>
    {
        var sql = Fx.RequireSql();
        var destinations = new DestinationsPage(page);
        var id = await destinations.CreateAsync(Unique("Destino DST02"), sql.Host, sql.Database, sql.User, sql.Password);

        await destinations.TestAsync(id);

        await page.ExpectMessageAsync(new Regex(@"detectado \(major \d+\)"));
    });

    // "Probar conexión" del diálogo, sin guardar, ajusta la versión a la del servidor (la UAT usa
    // SQL Server 2022). Al editar, con la contraseña oculta, usa la guardada.
    [UatSqlFact(DisplayName = "UAT-DST-07 Detectar la versión desde el diálogo")]
    public Task Detect_version_from_dialog() => RunAsync("UAT-DST-07", async page =>
    {
        var sql = Fx.RequireSql();
        var name = Unique("Destino DST07");
        var destinations = new DestinationsPage(page);
        await destinations.OpenAsync();
        await destinations.StartCreateAsync();
        await destinations.FillAsync(name, sql.Host, sql.Database, sql.User, sql.Password);
        await destinations.ChooseVersionAsync("SQL Server 2016");

        await destinations.DetectAsync();

        await Assertions.Expect(destinations.DetectResult).ToContainTextAsync("SQL Server 2022", new LocatorAssertionsToContainTextOptions { Timeout = Ui.Long });
        await Assertions.Expect(destinations.SelectedVersion).ToContainTextAsync("SQL Server 2022");
        await destinations.SaveAsync();
        var id = DestinationsPage.IdFor(name);
        await page.RevealAsync(destinations.ActionsOf(id));
        await Assertions.Expect(page.Row(name)).ToContainTextAsync("SQL Server 2022");

        await destinations.EditAsync(id);
        await destinations.ChooseVersionAsync("SQL Server 2016");
        await destinations.DetectAsync();

        await Assertions.Expect(destinations.DetectResult).ToContainTextAsync("SQL Server 2022", new LocatorAssertionsToContainTextOptions { Timeout = Ui.Long });
        await Assertions.Expect(destinations.SelectedVersion).ToContainTextAsync("SQL Server 2022");
        await destinations.CancelAsync();
    });

    // Probar desde la fila un destino guardado con otra versión la corrige sola.
    [UatSqlFact(DisplayName = "UAT-DST-08 Probar corrige la versión guardada")]
    public Task Row_test_updates_stored_version() => RunAsync("UAT-DST-08", async page =>
    {
        var sql = Fx.RequireSql();
        var name = Unique("Destino DST08");
        var destinations = new DestinationsPage(page);
        await destinations.OpenAsync();
        await destinations.StartCreateAsync();
        await destinations.FillAsync(name, sql.Host, sql.Database, sql.User, sql.Password);
        await destinations.ChooseVersionAsync("SQL Server 2016");
        await destinations.SaveAsync();
        var id = DestinationsPage.IdFor(name);
        await page.RevealAsync(destinations.ActionsOf(id));
        await Assertions.Expect(page.Row(name)).ToContainTextAsync("SQL Server 2016");

        await destinations.TestAsync(id);

        await page.ExpectMessageAsync(new Regex(@"detectado \(major 16\).*actualizada"));
        await Assertions.Expect(page.Row(name)).ToContainTextAsync("SQL Server 2022", new LocatorAssertionsToContainTextOptions { Timeout = Ui.Long });
    });

    [Fact(DisplayName = "UAT-DST-03 Probar un destino inalcanzable")]
    public Task Test_unreachable_destination() => RunAsync("UAT-DST-03", async page =>
    {
        var destinations = new DestinationsPage(page);
        var id = await destinations.CreateAsync(Unique("Destino DST03"), Unreachable, "SynapseLink", "sa", "clave");

        await destinations.TestAsync(id);

        await page.ExpectMessageAsync(new Regex("network-related|instance-specific|No such host|could not be found", RegexOptions.IgnoreCase), 60_000);
    });

    [Fact(DisplayName = "UAT-DST-04 La clave no se muestra al editar")]
    public Task Password_is_not_shown_when_editing() => RunAsync("UAT-DST-04", async page =>
    {
        const string password = "Clave-Secreta-DST04";
        var destinations = new DestinationsPage(page);
        var id = await destinations.CreateAsync(Unique("Destino DST04"), Unreachable, "SynapseLink", "sa", password);

        await destinations.EditAsync(id);

        await Assertions.Expect(destinations.HostInput).ToHaveValueAsync(Unreachable);
        Assert.NotEqual(password, await destinations.PasswordInput.InputValueAsync());
        await destinations.CancelAsync();
    });

    [Fact(DisplayName = "UAT-DST-05 Editar A y luego B")]
    public Task Editing_two_destinations_shows_each_one() => RunAsync("UAT-DST-05", async page =>
    {
        var destinations = new DestinationsPage(page);
        var nameA = Unique("Destino A");
        var nameB = Unique("Destino B");
        var idA = await destinations.CreateAsync(nameA, "host-a.invalid", "BaseA", "sa", "x");
        var idB = await destinations.CreateAsync(nameB, "host-b.invalid", "BaseB", "sa", "x");

        await destinations.EditAsync(idA);
        await Assertions.Expect(destinations.NameInput).ToHaveValueAsync(nameA);
        await destinations.CancelAsync();

        await destinations.EditAsync(idB);
        await Assertions.Expect(destinations.NameInput).ToHaveValueAsync(nameB);
        await Assertions.Expect(destinations.HostInput).ToHaveValueAsync("host-b.invalid");
        await Assertions.Expect(destinations.DatabaseInput).ToHaveValueAsync("BaseB");
        await destinations.CancelAsync();
    });

    [Fact(DisplayName = "UAT-DST-06 No se puede borrar un destino en uso")]
    public Task Destination_in_use_cannot_be_deleted() => RunAsync("UAT-DST-06", async page =>
    {
        var sourceName = Unique("Origen DST06");
        var destName = Unique("Destino DST06");
        var pipelineName = Unique("Pipeline DST06");
        await new SourcesPage(page).CreateLocalAsync(sourceName, "carpeta-dst06", "parquet-dst06");
        var destinations = new DestinationsPage(page);
        var destId = await destinations.CreateAsync(destName, Unreachable, "SynapseLink", "sa", "x");
        await new PipelinesPage(page).CreateAsync(pipelineName, sourceName, destName);

        await destinations.OpenAsync();
        await destinations.DeleteAsync(destId);

        await page.ExpectMessageAsync("Connection is in use by pipeline(s)");
        await page.RevealAsync(destinations.ActionsOf(destId));
    });
}
