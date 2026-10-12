using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;

using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Pipeline;

public sealed class IngestManifestStore
{
    private readonly AppOptions _options;

    public IngestManifestStore(IOptions<AppOptions> options) => _options = options.Value;

    public string GetRunDirectory(string pipelineId, string jobId) =>
        Path.Combine(_options.DataDir, pipelineId, "runs", jobId);

    // Ruta del blob parquet en el container ADLS de destino (canónico, durable).
    // Forward-slash: es una ruta de ADLS, no del filesystem local.
    public static string GetParquetBlobPath(string pipelineId, string jobId, string entity) =>
        $"{pipelineId}/{jobId}/{entity}.parquet";

    // El schema CDM viaja junto al parquet en ADLS para que el stage SQL pueda
    // leerlo sin depender del volumen local compartido (durabilidad simétrica con
    // el parquet: si el volumen se purga, el parquet sigue siendo legible).
    public static string GetSchemaBlobPath(string pipelineId, string jobId, string entity) =>
        $"{pipelineId}/{jobId}/{entity}.schema.json";

    // Parquet consolidado: artefacto durable, ESTABLE por (pipeline, tabla). Se actualiza
    // incrementalmente en cada corrida (independiente de SQL: la "doble confianza").
    public static string GetConsolidatedBlobPath(string pipelineId, string entity) =>
        $"{pipelineId}/{entity}.parquet";

    public static string GetConsolidatedSchemaBlobPath(string pipelineId, string entity) =>
        $"{pipelineId}/{entity}.schema.json";

    // Staging LOCAL (volumen) para los parquet transitorios por snapshot: efímeros, se
    // consumen al consolidar y se borran. No van a ADLS.
    public string GetStagingRoot(string jobId) =>
        Path.Combine(_options.DataDir, "staging", jobId);

    public string GetStagingEntityDirectory(string jobId, string entity) =>
        Path.Combine(GetStagingRoot(jobId), entity);

    public string GetStagingTransientPath(string jobId, string entity, int index) =>
        Path.Combine(GetStagingEntityDirectory(jobId, entity), $"{index:D5}.parquet");

    public string GetStagingConsolidatedPath(string jobId, string entity) =>
        Path.Combine(GetStagingRoot(jobId), $"{entity}.consolidated.parquet");

    // Delta de un Incremental: solo las filas de los snapshots de esta corrida, deduplicadas. Va al
    // volumen compartido (no a ADLS: lo lee una vez la etapa SQL del mismo job y luego se borra).
    public string GetDeltaDirectory(string pipelineId, string jobId) =>
        Path.Combine(GetRunDirectory(pipelineId, jobId), "delta");

    public string GetDeltaPath(string pipelineId, string jobId, string entity) =>
        Path.Combine(GetDeltaDirectory(pipelineId, jobId), $"{entity}.parquet");

    public string GetManifestPath(string pipelineId, string jobId) =>
        Path.Combine(GetRunDirectory(pipelineId, jobId), "ingest_manifest.json");

    public void Save(IngestManifest manifest)
    {
        var path = GetManifestPath(manifest.PipelineId, manifest.JobId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicJsonFile.Write(path, manifest);
    }

    public IngestManifest? Load(string pipelineId, string jobId)
    {
        var path = GetManifestPath(pipelineId, jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<IngestManifest>(stream, JsonDefaults.Options);
    }
}