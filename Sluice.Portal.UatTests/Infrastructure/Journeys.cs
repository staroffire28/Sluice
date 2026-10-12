using Sluice.Portal.UatTests.Pages;

namespace Sluice.Portal.UatTests.Infrastructure;

// Un pipeline completo montado por la UI: origen local con datos, destino SQL y pipeline.
public sealed record Journey(
    SynapseLinkFolder Folder,
    string SourceName,
    string DestinationName,
    string DestinationId,
    string PipelineName,
    string PipelineId,
    string ParquetFolder,
    IReadOnlyList<string> Entities);

public static class Journeys
{
    // Las entidades llevan el prefijo del escenario: todas las tablas viven en la misma base de la
    // ejecución, y dos escenarios con la misma entidad escribirían en la misma tabla.
    public static async Task<Journey> CreateAsync(
        IPage page,
        UatAppFixture fx,
        string tag,
        Action<SynapseLinkFolder, IReadOnlyList<string>> seed,
        string? sqlHost = null)
    {
        var sql = fx.RequireSql();
        var entities = new[] { $"{tag}_account", $"{tag}_contact" };
        var folder = fx.NewSourceFolder(tag);
        folder.DefineEntities(entities);
        seed(folder, entities);

        var suffix = Guid.NewGuid().ToString("N")[..6];
        var sourceName = $"Origen {tag} {suffix}";
        var destinationName = $"Destino {tag} {suffix}";
        var pipelineName = $"Pipeline {tag} {suffix}";
        var parquetFolder = $"parquet-{tag}-{suffix}";

        await new SourcesPage(page).CreateLocalAsync(sourceName, folder.Container, parquetFolder);
        var destinationId = await new DestinationsPage(page).CreateAsync(destinationName, sqlHost ?? sql.Host, sql.Database, sql.User, sql.Password);
        var pipelineId = await new PipelinesPage(page).CreateAsync(pipelineName, sourceName, destinationName);
        return new Journey(folder, sourceName, destinationName, destinationId, pipelineName, pipelineId, parquetFolder, entities);
    }

    // Datos de partida habituales: un snapshot cerrado con 6 filas en la primera entidad y 4 en la segunda.
    public static void TenRows(SynapseLinkFolder folder, IReadOnlyList<string> entities) =>
        folder.PublishSnapshot(new EntityRows(entities[0], 0, 6), new EntityRows(entities[1], 0, 4));

    // Lanza la ejecución desde Pipelines, abre el detalle del job y espera su estado final.
    public static async Task<string> RunAsync(IPage page, Journey journey, RunKind kind = RunKind.Run, string expected = "OK")
    {
        var pipelines = new PipelinesPage(page);
        await pipelines.OpenAsync();
        var jobId = await pipelines.RunAsync(journey.PipelineId, kind);
        var detail = new JobDetailPage(page);
        await detail.OpenAsync(jobId);
        await detail.ExpectFinishedAsync(expected);
        return jobId;
    }
}
