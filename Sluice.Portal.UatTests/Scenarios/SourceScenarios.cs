using Sluice.Portal.UatTests.Infrastructure;
using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Scenarios;

[Trait("Area", "Sources")]
public sealed class SourceScenarios(UatAppFixture fx) : UatTest(fx)
{
    // Origen con un snapshot ya cerrado, para que "Probar" tenga algo que leer.
    private SynapseLinkFolder PublishedFolder(string name)
    {
        var folder = Fx.NewSourceFolder(name);
        folder.DefineEntities("account");
        folder.PublishSnapshot(new EntityRows("account", 0, 3));
        return folder;
    }

    [Fact(DisplayName = "UAT-SRC-01 Crear un origen local")]
    public Task Create_local_source() => RunAsync("UAT-SRC-01", async page =>
    {
        var folder = PublishedFolder("src01");
        var name = Unique("Origen SRC01");

        var id = await new SourcesPage(page).CreateLocalAsync(name, folder.Container, "parquet-src01");

        var row = page.Row(name);
        await Assertions.Expect(row).ToContainTextAsync("local");
        Assert.Equal(SourcesPage.IdFor(name), id);
    });

    [Fact(DisplayName = "UAT-SRC-02 Probar un origen correcto")]
    public Task Test_reachable_source() => RunAsync("UAT-SRC-02", async page =>
    {
        var folder = PublishedFolder("src02");
        var sources = new SourcesPage(page);
        var id = await sources.CreateLocalAsync(Unique("Origen SRC02"), folder.Container, "parquet-src02");

        await sources.TestAsync(id);

        await page.ExpectMessageAsync("ADLS reachable.");
    });

    [Fact(DisplayName = "UAT-SRC-03 Probar un origen con carpeta inexistente")]
    public Task Test_missing_folder() => RunAsync("UAT-SRC-03", async page =>
    {
        var sources = new SourcesPage(page);
        var id = await sources.CreateLocalAsync(Unique("Origen SRC03"), "carpeta-que-no-existe", "parquet-src03");

        await sources.TestAsync(id);

        await page.ExpectMessageAsync("does not exist");
    });

    [Fact(DisplayName = "UAT-SRC-04 El formulario local oculta lo de Azure")]
    public Task Local_form_hides_azure_fields() => RunAsync("UAT-SRC-04", async page =>
    {
        var sources = new SourcesPage(page);
        await sources.OpenAsync();
        await sources.StartCreateAsync();
        await sources.AccountField.ExpectPresentAsync();

        await sources.ChooseTypeAsync(SourcesPage.LocalType);

        await sources.AccountField.ExpectGoneAsync();
        await sources.FolderInput.ExpectPresentAsync();
        await sources.ParquetFolderInput.ExpectPresentAsync();
        await sources.CancelAsync();
    });

    [Fact(DisplayName = "UAT-SRC-05 Editar A y luego B muestra los datos de cada uno")]
    public Task Editing_two_sources_shows_each_one() => RunAsync("UAT-SRC-05", async page =>
    {
        var sources = new SourcesPage(page);
        var nameA = Unique("Origen A");
        var nameB = Unique("Origen B");
        var idA = await sources.CreateLocalAsync(nameA, "carpeta-a", "parquet-a");
        var idB = await sources.CreateLocalAsync(nameB, "carpeta-b", "parquet-b");

        await sources.EditAsync(idA);
        await Assertions.Expect(sources.NameInput).ToHaveValueAsync(nameA);
        await sources.CancelAsync();

        await sources.EditAsync(idB);
        await Assertions.Expect(sources.NameInput).ToHaveValueAsync(nameB);
        await Assertions.Expect(sources.FolderInput).ToHaveValueAsync("carpeta-b");
        await Assertions.Expect(sources.ParquetFolderInput).ToHaveValueAsync("parquet-b");
        await sources.CancelAsync();
    });

    [Fact(DisplayName = "UAT-SRC-06 Renombrar un origen")]
    public Task Rename_source() => RunAsync("UAT-SRC-06", async page =>
    {
        var sources = new SourcesPage(page);
        var id = await sources.CreateLocalAsync(Unique("Origen SRC06"), "carpeta-src06", "parquet-src06");
        var renamed = Unique("Origen renombrado");

        await sources.EditAsync(id);
        await sources.SetNameAsync(renamed);
        await sources.SaveAsync();

        await page.RevealAsync(page.Row(renamed));
    });

    [Fact(DisplayName = "UAT-SRC-07 No se puede borrar un origen en uso")]
    public Task Source_in_use_cannot_be_deleted() => RunAsync("UAT-SRC-07", async page =>
    {
        var sourceName = Unique("Origen SRC07");
        var destName = Unique("Destino SRC07");
        var pipelineName = Unique("Pipeline SRC07");
        var sources = new SourcesPage(page);
        var sourceId = await sources.CreateLocalAsync(sourceName, "carpeta-src07", "parquet-src07");
        await new DestinationsPage(page).CreateAsync(destName, "sql-inexistente.invalid", "Db", "sa", "x");
        await new PipelinesPage(page).CreateAsync(pipelineName, sourceName, destName);

        await sources.OpenAsync();
        await sources.DeleteAsync(sourceId);

        await page.ExpectMessageAsync("Connection is in use by pipeline(s)");
        await page.ExpectMessageAsync(pipelineName);
        await page.RevealAsync(sources.ActionsOf(sourceId));
    });

    // Un origen ADLS con secreto que pasa a local no se queda con la cuenta ni el secreto de Azure
    // (el formulario local los oculta): al volver a ADLS en la edición, los campos salen vacíos.
    [Fact(DisplayName = "UAT-SRC-09 Pasar un origen a local no guarda las credenciales de Azure")]
    public Task Switching_to_local_drops_azure_credentials() => RunAsync("UAT-SRC-09", async page =>
    {
        var sources = new SourcesPage(page);
        var name = Unique("Origen SRC09");
        await sources.OpenAsync();
        await sources.StartCreateAsync();
        await sources.SetNameAsync(name);
        await sources.SetAccountAsync("cuentaazure");
        await sources.SetSecretAsync("secreto-de-prueba");
        await sources.SetFolderAsync("carpeta-src09");
        await sources.SaveAsync();
        var id = SourcesPage.IdFor(name);
        await page.RevealAsync(sources.ActionsOf(id));

        await sources.EditAsync(id);
        await sources.ChooseTypeAsync(SourcesPage.LocalType);
        await sources.SaveAsync();

        await sources.EditAsync(id);
        await sources.ChooseTypeAsync("ADLS Gen2");
        await Assertions.Expect(sources.AccountInput).ToHaveValueAsync("");
        await Assertions.Expect(sources.SecretInput).ToHaveValueAsync("");
        await sources.CancelAsync();
    });

    [Fact(DisplayName = "UAT-SRC-08 Borrar un origen libre")]
    public Task Delete_unused_source() => RunAsync("UAT-SRC-08", async page =>
    {
        var sources = new SourcesPage(page);
        var id = await sources.CreateLocalAsync(Unique("Origen SRC08"), "carpeta-src08", "parquet-src08");

        await sources.DeleteAsync(id);

        await sources.ActionsOf(id).ExpectGoneAsync();
    });
}
