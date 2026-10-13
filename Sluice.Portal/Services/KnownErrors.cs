using System.Net.Sockets;
using Azure;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Localization;

namespace Sluice.Portal.Services;

// Errores frecuentes con causa y remedio. Se clasifican por un código estable (número de
// SqlException, estado HTTP / ErrorCode de ADLS, AADSTS de Entra ID, SocketError de DNS) y no por el
// texto: SQL Server devuelve sus mensajes en el idioma del servidor. El texto es una red de seguridad
// cuando no hay código: jobs guardados antes de existir el código, la prueba de ADLS y excepciones
// sin código reconocible.
public static class KnownErrors
{
    public const string SqlLogin = "sql.login";
    public const string SqlDatabase = "sql.database";
    public const string SqlNetwork = "sql.network";
    public const string SqlTimeout = "sql.timeout";
    public const string SqlDeadlock = "sql.deadlock";
    public const string SqlLogFull = "sql.logFull";
    public const string SqlDuplicateKey = "sql.duplicateKey";
    public const string SqlTruncated = "sql.truncated";
    public const string SqlMergeDuplicateSource = "sql.mergeDuplicateSource";
    public const string AdlsForbidden = "adls.forbidden";
    public const string AdlsNotFound = "adls.notFound";
    public const string AdlsAuth = "adls.auth";
    public const string AdlsHost = "adls.host";

    public static IReadOnlyList<string> All { get; } =
    [
        SqlLogin, SqlDatabase, SqlNetwork, SqlTimeout, SqlDeadlock, SqlLogFull, SqlDuplicateKey, SqlTruncated,
        SqlMergeDuplicateSource, AdlsForbidden, AdlsNotFound, AdlsAuth, AdlsHost,
    ];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);

    // Los de ADLS se corrigen en la conexión de origen; los de SQL, en la de destino.
    public static bool IsSourceError(string code) => code.StartsWith("adls.", StringComparison.Ordinal);

    public static string? CodeFrom(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var code = current switch
            {
                SqlException sql => sql.Errors.Cast<SqlError>().Select(e => FromSqlNumber(e.Number)).FirstOrDefault(c => c is not null)
                    ?? FromSqlNumber(sql.Number),
                AuthenticationFailedException => AdlsAuth,
                RequestFailedException request => FromHttp(request.Status, request.ErrorCode),
                SocketException { SocketErrorCode: SocketError.HostNotFound } => AdlsHost,
                _ => null,
            };
            if (code is not null)
            {
                return code;
            }

            if (current is SqlException)
            {
                break;
            }
        }

        return CodeFromText(ex?.Message);
    }

    public static string? FromSqlNumber(int number) => number switch
    {
        18456 => SqlLogin,
        4060 => SqlDatabase,
        // Errores del proveedor de red: host inexistente, puerto cerrado, instancia no encontrada.
        53 or 40 or 26 or 11001 or 10060 or 10061 => SqlNetwork,
        -2 or 258 => SqlTimeout,
        1205 => SqlDeadlock,
        9002 => SqlLogFull,
        2627 or 2601 => SqlDuplicateKey,
        8152 or 2628 => SqlTruncated,
        8672 => SqlMergeDuplicateSource,
        _ => null,
    };

    public static string? FromHttp(int status, string? errorCode) => status switch
    {
        403 => AdlsForbidden,
        404 => AdlsNotFound,
        401 => AdlsAuth,
        _ => errorCode switch
        {
            "AuthorizationPermissionMismatch" or "AuthorizationFailure" => AdlsForbidden,
            "FilesystemNotFound" or "ContainerNotFound" or "PathNotFound" => AdlsNotFound,
            _ => null,
        },
    };

    // Respaldo por texto: mensajes de ADLS/Entra ID (no se traducen) y mensajes en inglés de SQL.
    public static string? CodeFromText(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        bool Has(string text) => message.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (Has("AADSTS") || Has("ClientSecretCredential authentication failed"))
        {
            return AdlsAuth;
        }

        if (Has("AuthorizationPermissionMismatch") || Has("AuthorizationFailure") || Has("Status: 403"))
        {
            return AdlsForbidden;
        }

        if (Has("FilesystemNotFound") || Has("ContainerNotFound") || Has("PathNotFound") || Has("Status: 404"))
        {
            return AdlsNotFound;
        }

        // El de SQL antes que el de host: el error de red de SqlClient también dice "No such host".
        if (Has("network-related or instance-specific"))
        {
            return SqlNetwork;
        }

        if (Has("No such host is known") || Has("Name or service not known"))
        {
            return AdlsHost;
        }

        if (Has("Login failed for user"))
        {
            return SqlLogin;
        }

        if (Has("Cannot open database"))
        {
            return SqlDatabase;
        }

        if (Has("Timeout Expired"))
        {
            return SqlTimeout;
        }

        if (Has("deadlocked on lock"))
        {
            return SqlDeadlock;
        }

        if (Has("The transaction log for database"))
        {
            return SqlLogFull;
        }

        if (Has("Violation of PRIMARY KEY") || Has("Cannot insert duplicate key"))
        {
            return SqlDuplicateKey;
        }

        if (Has("would be truncated"))
        {
            return SqlTruncated;
        }

        if (Has("attempted to UPDATE or DELETE the same row more than once"))
        {
            return SqlMergeDuplicateSource;
        }

        return null;
    }

    public static string Title(IStringLocalizer<SharedResource> localizer, string code) => localizer[$"Error.{code}.Title"].Value;

    public static string Cause(IStringLocalizer<SharedResource> localizer, string code) => localizer[$"Error.{code}.Cause"].Value;

    public static string Fix(IStringLocalizer<SharedResource> localizer, string code) => localizer[$"Error.{code}.Fix"].Value;

    // Una línea para avisos (toasts): título y qué hacer, con el mensaje original al final.
    public static string Describe(IStringLocalizer<SharedResource> localizer, string? code, string message) =>
        IsKnown(code) ? $"{Title(localizer, code!)}. {Fix(localizer, code!)} ({message})" : message;
}
