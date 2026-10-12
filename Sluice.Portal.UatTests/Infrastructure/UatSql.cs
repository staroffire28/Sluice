using Microsoft.Data.SqlClient;

namespace Sluice.Portal.UatTests.Infrastructure;

// SQL Server de pruebas (SLUICE_UAT_SQL, connection string a master). Cada ejecución crea su base
// SluiceUat_<id> y la borra al terminar: el portal no crea bases, solo tablas.
public sealed class UatSql
{
    public const string Variable = "SLUICE_UAT_SQL";

    private readonly SqlConnectionStringBuilder _master;

    private UatSql(SqlConnectionStringBuilder master, string database)
    {
        _master = master;
        Database = database;
    }

    public string Database { get; }

    // Lo que el usuario escribe en el formulario de destino.
    public string Host => _master.DataSource;
    public string User => _master.UserID;
    public string Password => _master.Password;

    public static string? ConnectionString => Environment.GetEnvironmentVariable(Variable);

    public static async Task<UatSql?> CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return null;
        }

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        var sql = new UatSql(master, $"SluiceUat_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..6]}");
        await sql.ExecuteOnMasterAsync($"CREATE DATABASE [{sql.Database}]");
        return sql;
    }

    public async Task DropAsync()
    {
        await ExecuteOnMasterAsync(
            $"IF DB_ID('{Database}') IS NOT NULL BEGIN ALTER DATABASE [{Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Database}]; END");
    }

    // Simula un cambio hecho fuera del portal en el destino (para provocar divergencia).
    public async Task<int> ExecuteAsync(string statement)
    {
        var builder = new SqlConnectionStringBuilder(_master.ConnectionString) { InitialCatalog = Database };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(statement, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteOnMasterAsync(string statement)
    {
        await using var connection = new SqlConnection(_master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(statement, connection);
        await command.ExecuteNonQueryAsync();
    }
}

// Escenario que necesita SQL Server: sin SLUICE_UAT_SQL se omite (salvo en CI, donde debe correr).
public sealed class UatSqlFactAttribute : FactAttribute
{
    public UatSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(UatSql.ConnectionString)
            && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Requiere SQL Server: define {UatSql.Variable} (connection string a master).";
        }
    }
}
