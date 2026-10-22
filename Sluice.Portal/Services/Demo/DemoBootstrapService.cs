using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Demo;

// Modo demo (DEMO_MODE): crea la base del SQL de la demo, genera los datos sintéticos de F&O,
// siembra el origen, el destino y el pipeline (con el scheduler activo) y lanza la primera carga.
// Después cierra un snapshot nuevo cada DEMO_SNAPSHOT_MINUTES y lo procesa. Solo en el nodo
// orquestador; registrado únicamente con DEMO_MODE=true.
public sealed class DemoBootstrapService : BackgroundService
{
    public const string SourceId = "conn-demo-source";
    public const string DestinationId = "conn-demo-sql";
    public const string PipelineId = "pipe-demo";
    public const string Database = "SluiceDemo";
    private const string SqlHost = "sql";
    private const string SqlUser = "sa";

    private static readonly TimeSpan SqlWait = TimeSpan.FromMinutes(5);

    private readonly AppOptions _options;
    private readonly ConnectionStore _connections;
    private readonly IPipelineService _pipelines;
    private readonly IJobService _jobs;
    private readonly IOrchestrationService _orchestration;
    private readonly IActivityLog _activity;
    private readonly ILogger<DemoBootstrapService> _logger;

    public DemoBootstrapService(
        IOptions<AppOptions> options,
        ConnectionStore connections,
        IPipelineService pipelines,
        IJobService jobs,
        IOrchestrationService orchestration,
        IActivityLog activity,
        ILogger<DemoBootstrapService> logger)
    {
        _options = options.Value;
        _connections = connections;
        _pipelines = pipelines;
        _jobs = jobs;
        _orchestration = orchestration;
        _activity = activity;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsOrchestrator)
        {
            return;
        }

        var generator = new DemoDataGenerator(_options.LocalSourceRoot!);
        try
        {
            await EnsureDatabaseAsync(stoppingToken);
            generator.Initialize(DateTimeOffset.UtcNow);
            Seed();
            if (_jobs.List(pageSize: 1, pipelineId: PipelineId).Count == 0)
            {
                await RunAsync(stoppingToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin esto el portal arrancaría vacío y sin explicación: se deja en /logs.
            _logger.LogCritical(ex, "Demo: no se pudo preparar la demo.");
            _activity.LogSystem("demo.failed", $"No se pudo preparar la demo: {ex.Message}", null, null, "error", "Activity.Demo.Failed", ex.Message);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.DemoSnapshotMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var sealedFolder = generator.Seal(DateTimeOffset.UtcNow);
                _logger.LogInformation("Demo: snapshot {Snapshot} cerrado.", sealedFolder);
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Demo: no se pudo generar el snapshot.");
            }
        }
    }

    // El SQL de la demo tarda en arrancar y el portal no crea bases: se espera y se crea.
    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        var master = new SqlConnectionStringBuilder
        {
            DataSource = SqlHost,
            UserID = SqlUser,
            Password = _options.DemoSqlPassword,
            InitialCatalog = "master",
            TrustServerCertificate = true,
            ConnectTimeout = 5,
        }.ConnectionString;

        var deadline = DateTimeOffset.UtcNow + SqlWait;
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(master);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"IF DB_ID(N'{Database}') IS NULL CREATE DATABASE [{Database}];";
                await command.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
            catch (SqlException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                _logger.LogInformation("Demo: esperando al SQL Server de la demo ({Message}).", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private void Seed()
    {
        if (_connections.GetById(SourceId) is null)
        {
            _connections.SaveNew(new Connection
            {
                Id = SourceId,
                Name = "Demo · Synapse Link (synthetic F&O data)",
                Role = ConnectionRole.Source,
                Type = "local",
                Config = new() { ["container"] = DemoDataGenerator.Container, ["parquet_container"] = DemoDataGenerator.ParquetContainer },
            });
        }

        if (_connections.GetById(DestinationId) is null)
        {
            _connections.SaveNew(new Connection
            {
                Id = DestinationId,
                Name = "Demo · SQL Server",
                Role = ConnectionRole.Destination,
                Type = "sqlserver",
                Config = new()
                {
                    ["host"] = SqlHost,
                    ["database"] = Database,
                    ["user"] = SqlUser,
                    ["password"] = _options.DemoSqlPassword,
                    ["trust_server_certificate"] = "true",
                },
            });
        }

        if (_pipelines.Get(PipelineId) is null)
        {
            _pipelines.Create(AuditActor.Demo, new Models.Pipeline
            {
                Id = PipelineId,
                Name = "Demo F&O",
                SourceId = SourceId,
                DestinationId = DestinationId,
                Enabled = true,
            });
        }
    }

    // Si el scheduler ya lanzó un job para el pipeline, no se lanza otro.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _orchestration.RunPipelineAsync(AuditActor.Demo, PipelineId, JobType.Incremental, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("Demo: no se lanza la carga ({Message}).", ex.Message);
        }
    }
}
