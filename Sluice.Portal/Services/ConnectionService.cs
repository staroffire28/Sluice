using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

public interface IConnectionService
{
    IReadOnlyList<Connection> List(ConnectionRole? role = null);
    Connection? Get(string id);
    // Lo que muta o prueba contra la red del cliente recibe quién actúa y queda en el registro de
    // actividad (ver AuditActor).
    Connection Create(AuditActor actor, Connection connection);

    // Si un origen cambia dónde guarda el Parquet (parquet_storage), copia antes al sitio nuevo los
    // consolidados de sus pipelines. CopiedTables: cuántos se copiaron. Cambiar el host, la cuenta, la
    // base de datos, el container o el almacenamiento con un job en curso en un pipeline que la usa
    // lanza PipelineBusyException.
    Task<(Connection? Connection, int CopiedTables)> UpdateAsync(AuditActor actor, string id, Connection connection, CancellationToken cancellationToken = default);

    // null si se borró; si no, el motivo.
    string? Delete(AuditActor actor, string id);
    Task<ConnectionTestResult> TestAsync(AuditActor actor, string id, CancellationToken cancellationToken = default);

    // Prueba un destino SQL sin guardarlo (el diálogo de Destinos) y devuelve la versión detectada.
    // existingId: al editar, los secretos que llegan vacíos o enmascarados se toman de la guardada.
    Task<SqlVersionDetection> DetectSqlVersionAsync(AuditActor actor, Connection draft, string? existingId, CancellationToken cancellationToken = default);

    // Prueba un origen del formulario sin guardarlo (los secretos vacíos o enmascarados se toman
    // del guardado con existingId).
    Task<ConnectionTestResult> TestSourceDraftAsync(AuditActor actor, Connection draft, string? existingId, CancellationToken cancellationToken = default);
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
    private readonly IAuditLog _audit;
    private readonly IJobService _jobs;
    private readonly ITaskStore _tasks;
    private readonly string _dataDir;

    public ConnectionService(
        ConnectionStore store,
        PipelineStore pipelines,
        IAdlsClientFactory adlsFactory,
        ISqlConnectionFactory sqlFactory,
        IAuditLog audit,
        IJobService jobs,
        ITaskStore tasks,
        IOptions<AppOptions> options)
    {
        _audit = audit;
        _store = store;
        _pipelines = pipelines;
        _adlsFactory = adlsFactory;
        _sqlFactory = sqlFactory;
        _jobs = jobs;
        _tasks = tasks;
        _dataDir = options.Value.DataDir;
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

    public Connection Create(AuditActor actor, Connection connection) =>
        _audit.Audit(
            actor,
            "connection.create",
            null,
            () => CreateCore(connection),
            created => new AuditResult($"Conexión {RoleLabel(created.Role)} '{created.Name}' creada.",
                MessageKey: "Activity.Connection.Created", MessageArgs: [RolePart(created.Role), created.Name]),
            $"No se pudo crear la conexión {RoleLabel(connection.Role)} '{connection.Name}'",
            failureKey: "Activity.Failed.ConnectionCreate", failureArgs: [RolePart(connection.Role), connection.Name],
            target: connection.Id);

    private Connection CreateCore(Connection connection)
    {
        if (!EntityIds.IsValid(connection.Id))
        {
            throw new InvalidOperationException($"Id de conexión no válido: '{connection.Id}'.");
        }

        _store.SaveNew(connection);
        return SecretMasker.Mask(connection);
    }

    // Si la edición cambia el tipo, la configuración anterior es de otro tipo (p. ej. la cuenta y
    // el client_secret de Azure en un origen que pasa a local) y no se arrastra.
    private static Connection Merge(Connection existing, Connection connection)
    {
        var merged = connection with
        {
            Id = existing.Id,
            Config = SameType(existing, connection)
                ? MergeConfig(existing.Config, connection.Config)
                : new Dictionary<string, string?>(connection.Config, StringComparer.OrdinalIgnoreCase),
        };

        // Al guardar, un secreto que no viaja porque cambió su destino es un error explícito, no una
        // conexión que falla después sin contraseña.
        if (SameType(existing, connection) && ChangedSecretTarget(existing.Config, connection.Config) is { } target)
        {
            var missing = existing.Config
                .Where(e => SecretMasker.IsSecretKey(e.Key) && !string.IsNullOrEmpty(e.Value)
                    && string.IsNullOrEmpty(merged.Config.GetValueOrDefault(e.Key)))
                .Select(e => e.Key)
                .ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Has cambiado '{target}': vuelve a escribir {string.Join(", ", missing)} (el guardado no se envía a otro destino).");
            }
        }

        return merged;
    }

    private static bool SameType(Connection a, Connection b) =>
        string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase);

    // Tras la purga de los CSV de Synapse Link, el Parquet consolidado puede ser la única copia de la
    // historia: al cambiar de sitio se copia (sin borrar el original) antes de guardar la conexión y,
    // si la copia falla, no se guarda nada. Un cambio de tipo no mueve el Parquet.
    public Task<(Connection? Connection, int CopiedTables)> UpdateAsync(AuditActor actor, string id, Connection connection, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "connection.update",
            null,
            () => UpdateCoreAsync(id, connection, cancellationToken),
            result => result.CopiedTables > 0 && result.Connection is { } updated
                ? new AuditResult($"Conexión {RoleLabel(updated.Role)} '{updated.Name}' actualizada; Parquet de {result.CopiedTables} tabla(s) copiado al sitio nuevo.",
                    MessageKey: "Activity.Connection.UpdatedCopied", MessageArgs: [RolePart(updated.Role), updated.Name, result.CopiedTables])
                : Updated(id)(result.Connection),
            $"No se pudo guardar la conexión {RoleLabel(connection.Role)} '{connection.Name}'",
            failureKey: "Activity.Failed.ConnectionSave", failureArgs: [RolePart(connection.Role), connection.Name],
            target: id);

    private static Func<Connection?, AuditResult> Updated(string id) => updated => updated is null
        ? new AuditResult($"La conexión {id} no existe.", Outcome: AuditOutcome.Rejected, MessageKey: "Activity.Connection.NotFound", MessageArgs: [id])
        : new AuditResult($"Conexión {RoleLabel(updated.Role)} '{updated.Name}' actualizada.",
            MessageKey: "Activity.Connection.Updated", MessageArgs: [RolePart(updated.Role), updated.Name]);

    private static string RoleLabel(ConnectionRole role) => role == ConnectionRole.Source ? "de origen" : "de destino";

    // El rol en el idioma de quien lee el registro (RoleLabel queda en el texto guardado).
    private static EventPart RolePart(ConnectionRole role) =>
        new(role == ConnectionRole.Source ? "Activity.Connection.RoleSource" : "Activity.Connection.RoleDestination");

    private async Task<(Connection? Connection, int CopiedTables)> UpdateCoreAsync(string id, Connection connection, CancellationToken cancellationToken)
    {
        var existing = _store.GetById(id);
        if (existing is null)
        {
            return (null, 0);
        }

        var merged = Merge(existing, connection);

        // Otro host, cuenta, base de datos, container o almacenamiento con un job en curso: el job
        // seguiría en el sitio viejo y el siguiente en el nuevo. Se toma el lock de ejecución de cada
        // pipeline que la usa (por id, siempre en el mismo orden) y se mantiene hasta guardar, copia del
        // Parquet incluida.
        var locks = new List<IDisposable>();
        try
        {
            if (ChangedSecretTarget(existing.Config, merged.Config) is not null
                || ParquetStorageOf(existing) != ParquetStorageOf(merged)
                || !SameTarget(ParquetContainerOf(existing), ParquetContainerOf(merged))
                || !SameTarget(existing.Config.GetValueOrDefault("database"), merged.Config.GetValueOrDefault("database"))
                || !SameTarget(existing.Config.GetValueOrDefault("container"), merged.Config.GetValueOrDefault("container")))
            {
                foreach (var pipelineId in _pipelines.ListAll()
                    .Where(p => p.SourceId == id || p.DestinationId == id)
                    .Select(p => p.Id)
                    .Order(StringComparer.Ordinal))
                {
                    locks.Add(await PipelineRunLock.AcquireAsync(_dataDir, pipelineId, PipelineRunLock.DefaultTimeout, cancellationToken));
                    await PipelineBusyException.ThrowIfBusyAsync(_jobs, _tasks, pipelineId,
                        $"Hay un job en curso en el pipeline {pipelineId}, que usa esta conexión: espera a que termine o cancélalo antes de cambiar a dónde apunta.",
                        cancellationToken);
                }
            }

            return await SaveMergedAsync(existing, merged, cancellationToken);
        }
        finally
        {
            foreach (var runLock in locks)
            {
                runLock.Dispose();
            }
        }
    }

    private async Task<(Connection? Connection, int CopiedTables)> SaveMergedAsync(Connection existing, Connection merged, CancellationToken cancellationToken)
    {
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

    // Una clave ausente y un valor vacío (lo que envía el formulario) apuntan al mismo sitio.
    private static bool SameTarget(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? ParquetContainerOf(Connection connection) =>
        connection.Config.GetValueOrDefault("parquet_container") ?? connection.Config.GetValueOrDefault("destination_container");

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

    // Claves que dicen a dónde viaja el secreto (alias en el mismo grupo). Si cambian, el secreto
    // guardado no se reutiliza: probar con otro host y la contraseña en blanco mandaba la guardada a
    // ese host (y en ADLS, el token de Entra que se obtiene con el client_secret).
    private static readonly string[][] SecretTargetKeys = [["host", "server"], ["account", "storage_account"], ["tenant_id"], ["client_id"]];

    private static string? ChangedSecretTarget(
        IReadOnlyDictionary<string, string?> existing,
        IReadOnlyDictionary<string, string?> incoming)
    {
        static string? First(IReadOnlyDictionary<string, string?> config, string[] aliases) =>
            aliases.Select(key => config.TryGetValue(key, out var value) ? value?.Trim() : null)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value));

        foreach (var aliases in SecretTargetKeys)
        {
            var after = First(incoming, aliases);
            if (after is not null && !string.Equals(First(existing, aliases), after, StringComparison.OrdinalIgnoreCase))
            {
                return aliases[0];
            }
        }

        return null;
    }

    private static Dictionary<string, string?> MergeConfig(
        IReadOnlyDictionary<string, string?> existing,
        IReadOnlyDictionary<string, string?> incoming)
    {
        var keepSecrets = ChangedSecretTarget(existing, incoming) is null;
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in existing)
        {
            if (keepSecrets || !SecretMasker.IsSecretKey(key))
            {
                merged[key] = value;
            }
        }

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

    public string? Delete(AuditActor actor, string id)
    {
        var name = _store.GetById(id)?.Name ?? id;
        return _audit.Audit(
            actor,
            "connection.delete",
            null,
            () => DeleteCore(id),
            error => error is null
                ? new AuditResult($"Conexión '{name}' eliminada.", MessageKey: "Activity.Connection.Deleted", MessageArgs: [name])
                : new AuditResult($"No se borró la conexión '{name}': {error}", Outcome: AuditOutcome.Rejected,
                    MessageKey: "Activity.Connection.NotDeleted", MessageArgs: [name, error]),
            $"No se pudo borrar la conexión '{name}'",
            failureKey: "Activity.Failed.ConnectionDelete", failureArgs: [name],
            target: id);
    }

    private string? DeleteCore(string id)
    {
        var usedBy = _pipelines.ListAll()
            .Where(p => p.SourceId == id || p.DestinationId == id)
            .Select(p => p.Name)
            .ToList();
        if (usedBy.Count > 0)
        {
            // Nombra los pipelines que la usan para que el usuario sepa qué desasociar. Si solo
            // quería arreglar la conexión (p. ej. re-cifrar el secreto), basta editarla, no borrarla.
            return $"Connection is in use by pipeline(s): {string.Join(", ", usedBy)}. " +
                "Edit or delete that pipeline first, or just edit the connection instead of deleting it.";
        }

        return _store.Delete(id) ? null : "Connection not found.";
    }

    // Una prueba que no conecta es un resultado, no una excepción: queda como Error con su mensaje.
    public Task<ConnectionTestResult> TestAsync(AuditActor actor, string id, CancellationToken cancellationToken = default) =>
        AuditTestAsync(actor, id, () => TestCoreAsync(id, cancellationToken));

    private Task<ConnectionTestResult> AuditTestAsync(AuditActor actor, string target, Func<Task<ConnectionTestResult>> test) =>
        _audit.AuditAsync(
            actor,
            "connection.test",
            null,
            test,
            result => new AuditResult($"Prueba de la conexión {target}: {result.Message}", Outcome: result.Ok ? AuditOutcome.Ok : AuditOutcome.Error,
                MessageKey: "Activity.Connection.Tested", MessageArgs: [target, result.Message]),
            $"No se pudo probar la conexión {target}",
            failureKey: "Activity.Failed.ConnectionTest", failureArgs: [target],
            target: target);

    private Task<ConnectionTestResult> TestCoreAsync(string id, CancellationToken cancellationToken)
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

    // Las pruebas desde el formulario (sin guardar) también salen a la red del cliente.
    public Task<ConnectionTestResult> TestSourceDraftAsync(AuditActor actor, Connection draft, string? existingId, CancellationToken cancellationToken = default) =>
        AuditTestAsync(actor, DraftTarget(draft, existingId), () => TestSourceDraftCoreAsync(draft, existingId, cancellationToken));

    private static string DraftTarget(Connection draft, string? existingId) => existingId ?? $"'{draft.Name}' (sin guardar)";

    private async Task<ConnectionTestResult> TestSourceDraftCoreAsync(Connection draft, string? existingId, CancellationToken cancellationToken)
    {
        var existing = existingId is null ? null : _store.GetById(existingId);
        var connection = existing is not null && string.Equals(existing.Type, draft.Type, StringComparison.OrdinalIgnoreCase)
            ? draft with { Config = MergeConfig(existing.Config, draft.Config) }
            : draft;
        var (ok, message) = await _adlsFactory.TestAsync(connection, cancellationToken);
        return new ConnectionTestResult(ok, message, ok ? null : KnownErrors.CodeFromText(message));
    }

    public Task<SqlVersionDetection> DetectSqlVersionAsync(AuditActor actor, Connection draft, string? existingId, CancellationToken cancellationToken = default) =>
        _audit.AuditAsync(
            actor,
            "connection.test",
            null,
            () => DetectSqlVersionCoreAsync(draft, existingId, cancellationToken),
            result => new AuditResult(
                $"Prueba de la conexión {DraftTarget(draft, existingId)}: {result.Message}",
                Outcome: result.Ok ? AuditOutcome.Ok : AuditOutcome.Error,
                MessageKey: "Activity.Connection.Tested", MessageArgs: [DraftTarget(draft, existingId), result.Message]),
            $"No se pudo probar la conexión {DraftTarget(draft, existingId)}",
            failureKey: "Activity.Failed.ConnectionTest", failureArgs: [DraftTarget(draft, existingId)],
            target: DraftTarget(draft, existingId));

    private Task<SqlVersionDetection> DetectSqlVersionCoreAsync(Connection draft, string? existingId, CancellationToken cancellationToken)
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