using Microsoft.Extensions.Localization;
using Sluice.Portal.Services.Adls;

namespace Sluice.Portal.Services;

// Nombre legible del tipo de conexión (Connection.Type) en las tablas de Orígenes y Destinos.
public static class ConnectionTypeLabels
{
    public static string Get(IStringLocalizer<SharedResource> localizer, string type) =>
        type.ToLowerInvariant() switch
        {
            "adls" => "ADLS Gen2",
            AdlsClientFactory.LocalType => localizer["Common.ConnectionType.Local"].Value,
            "sqlserver" => "SQL Server",
            _ => type,
        };
}
