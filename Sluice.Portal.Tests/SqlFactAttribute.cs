namespace Sluice.Portal.Tests;

// Tests que necesitan un SQL Server real (connection string en SLUICE_TEST_SQL).
// En local, sin la variable, salen como Skipped: antes hacían "return" y salían como Passed sin
// ejecutar nada. En CI (CI=true, lo define GitHub Actions) nunca se saltan: si falta la variable
// fallan, para que el MERGE no vuelva a quedar sin cobertura en silencio.
public sealed class SqlFactAttribute : FactAttribute
{
    public const string Variable = "SLUICE_TEST_SQL";

    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) && !IsCi)
        {
            Skip = $"Requiere SQL Server: define {Variable} con una connection string.";
        }
    }

    private static string? ConnectionString => Environment.GetEnvironmentVariable(Variable);

    private static bool IsCi =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    public static string RequireConnectionString() =>
        string.IsNullOrWhiteSpace(ConnectionString)
            ? throw new InvalidOperationException($"{Variable} no está definida y el entorno es CI: estos tests deben ejecutarse.")
            : ConnectionString;
}
