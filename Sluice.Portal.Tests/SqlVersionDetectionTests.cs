using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Adls;
using Sluice.Portal.Services.Connections;
using Sluice.Portal.Services.Pipeline;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Tests;

// Detección de la versión de SQL Server al probar un destino: desde el diálogo (sin guardar, con
// la contraseña guardada si llega enmascarada) y desde la fila (actualiza la versión guardada).
public sealed class SqlVersionDetectionTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-sqlversion-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionSecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly ConnectionStore _store;
    private readonly RecordingSqlFactory _sql = new();
    private readonly ConnectionService _service;

    public SqlVersionDetectionTests()
    {
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new AppOptions { DataDir = _dataDir });
        _store = new ConnectionStore(options, _protector);
        _service = new ConnectionService(_store, new PipelineStore(options), new UnusedAdlsFactory(), _sql);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Desde_el_dialogo_detecta_la_version_sin_guardar_nada()
    {
        _sql.Major = 15;

        var result = await _service.DetectSqlVersionAsync(Draft(password: "secreta", version: "2022"), existingId: null);

        Assert.True(result.Ok);
        Assert.Equal(SqlServerVersion.SqlServer2019, result.Version);
        Assert.Equal(15, result.Major);
        Assert.Equal("secreta", _sql.LastPassword);
        Assert.Empty(_store.ListAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData(SecretMasker.MaskValue)]
    public async Task Al_editar_con_la_contrasena_enmascarada_usa_la_guardada(string typed)
    {
        _store.SaveNew(Draft(password: "guardada", version: "2022") with { Id = "conn-dst-sql" });

        var result = await _service.DetectSqlVersionAsync(Draft(password: typed, version: "2022"), "conn-dst-sql");

        Assert.True(result.Ok);
        // El servicio pasa el secreto guardado (cifrado); la factoría real lo descifra antes de conectar.
        Assert.Equal("guardada", _protector.Unprotect(_sql.LastPassword));
    }

    [Fact]
    public async Task Si_no_conecta_devuelve_el_error_sin_version()
    {
        _sql.Error = "Login failed for user 'sa'.";

        var result = await _service.DetectSqlVersionAsync(Draft(password: "mala", version: "2022"), existingId: null);

        Assert.False(result.Ok);
        Assert.Null(result.Version);
        Assert.Contains("Login failed", result.Message);
    }

    [Fact]
    public async Task Probar_desde_la_fila_actualiza_la_version_guardada_si_no_coincide()
    {
        _store.SaveNew(Draft(password: "x", version: "2022") with { Id = "conn-dst-sql" });
        _sql.Major = 14;

        var (ok, message) = await _service.TestAsync("conn-dst-sql");

        Assert.True(ok);
        Assert.Contains("detectado (major 14)", message);
        Assert.Contains("actualizada", message);
        Assert.Equal(SqlServerVersion.SqlServer2017, ConnectionSettingsFactory.GetSqlVersion(_store.GetById("conn-dst-sql")!));
        Assert.Equal("x", _protector.Unprotect(_store.GetById("conn-dst-sql")!.Config["password"]));
    }

    [Fact]
    public async Task Probar_desde_la_fila_con_la_version_correcta_no_cambia_nada()
    {
        _store.SaveNew(Draft(password: "x", version: "2022") with { Id = "conn-dst-sql" });
        _sql.Major = 16;

        var (ok, message) = await _service.TestAsync("conn-dst-sql");

        Assert.True(ok);
        Assert.DoesNotContain("actualizada", message);
        Assert.Equal(SqlServerVersion.SqlServer2022, ConnectionSettingsFactory.GetSqlVersion(_store.GetById("conn-dst-sql")!));
    }

    private static Connection Draft(string password, string version) => new()
    {
        Id = "conn-dst-draft",
        Name = "Destino",
        Role = ConnectionRole.Destination,
        Type = "sqlserver",
        Config = new Dictionary<string, string?>
        {
            ["host"] = "sql.example",
            ["database"] = "SynapseLink",
            ["user"] = "sa",
            ["password"] = password,
            [SqlServerVersions.ConfigKey] = version,
        },
    };

    private sealed class RecordingSqlFactory : ISqlConnectionFactory
    {
        public int Major { get; set; } = 16;
        public string? Error { get; set; }
        public string? LastPassword { get; private set; }

        public Task<int> DetectMajorVersionAsync(Connection connection, CancellationToken cancellationToken = default)
        {
            LastPassword = connection.Config.TryGetValue("password", out var p) ? p : null;
            return Error is null ? Task.FromResult(Major) : throw new InvalidOperationException(Error);
        }

        public Task<SqlConnection> OpenAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string GetSchema(string connectionId) => throw new NotSupportedException();
        public SqlServerVersion GetSqlVersion(string connectionId) => throw new NotSupportedException();
    }

    private sealed class UnusedAdlsFactory : IAdlsClientFactory
    {
        public Task<IAdlsClient> CreateAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IParquetStore> CreateParquetStoreAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Ok, string Message)> TestAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
