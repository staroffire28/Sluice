using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public interface IConnectionService
{
    IReadOnlyList<Connection> List(ConnectionRole? role = null);
    Connection? Get(string id);
    Connection Create(Connection connection);
    Connection? Update(string id, Connection connection);

    // Como Update; si un origen cambia dónde guarda el Parquet (parquet_storage), copia antes al
    // sitio nuevo los consolidados de sus pipelines. CopiedTables: cuántos se copiaron.
    Task<(Connection? Connection, int CopiedTables)> UpdateAsync(string id, Connection connection, CancellationToken cancellationToken = default);
    bool Delete(string id, out string? error);
    Task<ConnectionTestResult> TestAsync(string id, CancellationToken cancellationToken = default);

    // Prueba un destino SQL sin guardarlo (el diálogo de Destinos) y devuelve la versión detectada.
    // existingId: al editar, los secretos que llegan vacíos o enmascarados se toman de la guardada.
    Task<SqlVersionDetection> DetectSqlVersionAsync(Connection draft, string? existingId, CancellationToken cancellationToken = default);
}

// Code: error conocido (KnownErrors) cuando falla; null si no se reconoce.
public sealed record SqlVersionDetection(bool Ok, string Message, SqlServerVersion? Version = null, int? Major = null, string? Code = null);

public sealed record ConnectionTestResult(bool Ok, string Message, string? Code = null)
{
    // Para los llamadores que solo usan (ok, mensaje).
    public void Deconstruct(out bool ok, out string message)
    {
        ok = Ok;
        message = Message;
    }
}

public sealed class ConnectionService : IConnectionService
{
    private readonly ConnectionStore _store;
    private readonly PipelineStore _pipelines;
    private readonly IAdlsClientFactory _adlsFactory;
    private readonly ISqlConnectionFactory _sqlFactory;

    public ConnectionService(
        ConnectionStore store,
        PipelineStore pipelines,
        IAdlsClientFactory adlsFactory,
        ISqlConnectionFactory sqlFactory)
    {
        _store = store;
        _pipelines = pipelines;
        _adlsFactory = adlsFactory;
        _sqlFactory = sqlFactory;
    }

    public IReadOnlyList<Connection> List(ConnectionRole? role = null)
    {
        var items = _store.ListAll().Select(SecretMasker.Mask);
        return role is null ? items.ToList() : items.Where(c => c.Role == role).ToList();
    }

    public Connection? Get(string id)
    {
        var item = _store.GetById(id);
        return item is null ? null : SecretMasker.Mask(item);
    }

    public Connection Create(Connection connection)
    {
        if (!EntityIds.IsValid(connection.Id))
        {
            throw new InvalidOperationException($"Id de conexión no válido: '{connection.Id}'.");
        }

        _store.SaveNew(connection);
        return SecretMasker.Mask(connection);
    }

    public Connection? Update(string id, Connection connection)
    {
        if (!_store.Exists(id))
        {
            return null;
        }

        var existing = _store.GetById(id)!;
        var merged = Merge(existing, connection);
        _store.Save(merged);
        return SecretMasker.Mask(merged);
    }

    // Si la edición cambia el tipo, la configuración anterior es de otro tipo (p. ej. la cuenta y
    // el client_secret de Azure en un origen que pasa a local) y no se arrastra.
    private static Connection Merge(Connection existing, Connection connection) =>
        connection with
        {
            Id = existing.Id,
            Config = SameType(existing, connection)
                ? MergeConfig(existing.Config, connection.Config)
                : new Dictionary<string, string?>(connection.Config, StringComparer.OrdinalIgnoreCase),
        };

    private static bool SameType(Connection a, Connection b) =>
        string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);

    // Tras la purga de los CSV de Synapse Link, el Parquet consolidado puede ser la única copia de la
    // historia: al cambiar de sitio se copia (sin borrar el original) antes de guardar la conexión y,
    // si la copia falla, no se guarda nada. Un cambio de tipo no mueve el Parquet.
    public async Task<(Connection? Connection, int CopiedTables)> UpdateAsync(string id, Connection connection, CancellationToken cancellationToken = default)
    {
        var existing = _store.GetById(id);
        if (existing is null)
        {
            return (null, 0);
        }

        var merged = Merge(existing, connection);
        var copied = existing.Role == ConnectionRole.Source
            && SameType(existing, merged)
            && ParquetStorageOf(existing) != ParquetStorageOf(merged)
            && HasParquet(existing)
                ? await CopyConsolidatedParquetAsync(existing, merged, cancellationToken)
                : 0;
        _store.Save(merged);
        return (SecretMasker.Mask(merged), copied);
    }

    private static string ParquetStorageOf(Connection connection) =>
        AdlsClientFactory.KeepsParquetOnPortal(connection)
            ? AdlsClientFactory.PortalParquetStorage
            : AdlsClientFactory.AdlsParquetStorage;

    // En ADLS sin container de Parquet no hay nada que copiar.
    private static bool HasParquet(Connection connection) =>
        ParquetStorageOf(connection) == AdlsClientFactory.PortalParquetStorage
        || !string.IsNullOrWhiteSpace(connection.Config.GetValueOrDefault("parquet_container"))
        || !string.IsNullOrWhiteSpace(connection.Config.GetValueOrDefault("destination_container"));

    // Solo los consolidados ({pipeline}/{entidad}.parquet y su .schema.json): los Parquet de cada
    // job son copias de trabajo que se regeneran.
    private async Task<int> CopyConsolidatedParquetAsync(Connection from, Connection to, CancellationToken cancellationToken)
    {
        var source = await _adlsFactory.CreateParquetStoreAsync(from, cancellationToken);
        var target = await _adlsFactory.CreateParquetStoreAsync(to, cancellationToken);
        var copied = 0;
        foreach (var pipeline in _pipelines.ListAll().Where(p => p.SourceId == from.Id))
        {
            foreach (var artifact in await source.ListParquetAsync($"{pipeline.Id}/", cancellationToken))
            {
                if (artifact.Path.Split('/').Length != 2)
                {
                    continue;
                }

                await using (var stream = await source.OpenParquetReadAsync(artifact.Path, cancellationToken))
                {
                    await target.UploadParquetAsync(artifact.Path, stream, cancellationToken);
                }

                var schemaPath = artifact.Path[..^".parquet".Length] + ".schema.json";
                if (await source.ReadTextAsync(schemaPath, cancellationToken) is { } schema)
                {
                    await target.UploadTextAsync(schemaPath, schema, cancellationToken);
                }

                copied++;
            }
        }

        return copied;
    }

    private static Dictionary<string, string?> MergeConfig(
        IReadOnlyDictionary<string, string?> existing,
        IReadOnlyDictionary<string, string?> incoming)
    {
        var merged = new Dictionary<string, string?>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in incoming)
        {
            if (SecretMasker.IsSecretKey(key)
                && (string.IsNullOrWhiteSpace(value) || value == SecretMasker.MaskValue))
            {
                continue;
            }

            merged[key] = value;
        }

        return merged;
    }

    public bool Delete(string id, out string? error)
    {
        error = null;
        var usedBy = _pipelines.ListAll()
            .Where(p => p.SourceId == id || p.DestinationId == id)
            .Select(p => p.Name)
            .ToList();
        if (usedBy.Count > 0)
        {
            // Nombra los pipelines que la usan para que el usuario sepa qué desasociar. Si solo
            // quería arreglar la conexión (p. ej. re-cifrar el secreto), basta editarla, no borrarla.
            error = $"Connection is in use by pipeline(s): {string.Join(", ", usedBy)}. " +
                "Edit or delete that pipeline first, or just edit the connection instead of deleting it.";
            return false;
        }

        return _store.Delete(id);
    }

    public Task<ConnectionTestResult> TestAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = _store.GetById(id);
        if (connection is null)
        {
            return Task.FromResult(new ConnectionTestResult(false, "Connection not found."));
        }

        return connection.Type.ToLowerInvariant() switch
        {
            "adls" or AdlsClientFactory.LocalType => TestAdlsAsync(id, cancellationToken),
            "sqlserver" => TestSqlAsync(connection, cancellationToken),
            _ => Task.FromResult(new ConnectionTestResult(false, $"Unknown connection type: {connection.Type}")),
        };
    }

    // Los mensajes de ADLS y de Entra ID no se traducen: el código se reconoce por el texto.
    private async Task<ConnectionTestResult> TestAdlsAsync(string id, CancellationToken cancellationToken)
    {
        var (ok, message) = await _adlsFactory.TestAsync(id, cancellationToken);
        return new ConnectionTestResult(ok, message, ok ? null : KnownErrors.CodeFromText(message));
    }

    // Probar un destino SQL detecta la versión del servidor y, si no coincide con la guardada, la
    // actualiza: el T-SQL que genera el pipeline depende de ella.
    private async Task<ConnectionTestResult> TestSqlAsync(Connection connection, CancellationToken cancellationToken)
    {
        var detection = await DetectAsync(connection, cancellationToken);
        if (!detection.Ok || detection.Version is not { } detected)
        {
            return new ConnectionTestResult(false, detection.Message, detection.Code);
        }

        var configured = ConnectionSettingsFactory.GetSqlVersion(connection);
        if (detected == configured)
        {
            return new ConnectionTestResult(true, detection.Message);
        }

        var config = new Dictionary<string, string?>(connection.Config, StringComparer.OrdinalIgnoreCase)
        {
            [SqlServerVersions.ConfigKey] = ((int)detected).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        _store.Save(connection with { Config = config });
        return new ConnectionTestResult(true, $"{detection.Message} Versión del destino actualizada (antes: {SqlServerVersions.DisplayName(configured)}).");
    }

    public Task<SqlVersionDetection> DetectSqlVersionAsync(Connection draft, string? existingId, CancellationToken cancellationToken = default)
    {
        var existing = existingId is null ? null : _store.GetById(existingId);
        var connection = existing is not null && string.Equals(existing.Type, draft.Type, StringComparison.OrdinalIgnoreCase)
            ? draft with { Config = MergeConfig(existing.Config, draft.Config) }
            : draft;
        return DetectAsync(connection, cancellationToken);
    }

    private async Task<SqlVersionDetection> DetectAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            var major = await _sqlFactory.DetectMajorVersionAsync(connection, cancellationToken);
            var version = SqlServerVersions.FromMajorVersion(major);
            return new SqlVersionDetection(true, $"{SqlServerVersions.DisplayName(version)} detectado (major {major}).", version, major);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new SqlVersionDetection(false, ex.Message, Code: KnownErrors.CodeFrom(ex));
        }
    }
}