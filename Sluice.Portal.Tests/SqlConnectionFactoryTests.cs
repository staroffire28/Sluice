using Microsoft.Data.SqlClient;
using Sluice.Portal.Services.Pipeline;

namespace Sluice.Portal.Tests;

// Timeout de las sentencias del pipeline: sin límite (un MERGE, un borrado de huérfanas o un índice
// sobre una tabla grande pueden pasar de los 30 s por defecto). Un posible bloqueo se avisa aparte
// (SqlStallWatch), no cortando la sentencia.
public sealed class SqlConnectionFactoryTests
{
    private const string Base = "Server=sql;Database=SynapseLink;User Id=sa;Password=x;Encrypt=True;TrustServerCertificate=True";

    [Fact]
    public void Pipeline_connections_run_commands_without_a_time_limit()
    {
        using var connection = new SqlConnection(SqlConnectionFactory.ForPipeline(Base));

        Assert.Equal(0, connection.CommandTimeout);
        Assert.Equal(0, connection.CreateCommand().CommandTimeout);
    }

    // Una connection_string configurada a mano con su propio Command Timeout se respeta.
    [Fact]
    public void An_explicit_command_timeout_in_the_connection_string_is_kept()
    {
        using var connection = new SqlConnection(SqlConnectionFactory.ForPipeline(Base + ";Command Timeout=120"));

        Assert.Equal(120, connection.CommandTimeout);
    }
}
