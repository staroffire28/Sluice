using System.Security.Cryptography;
using System.Text;
using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Services.Logging;

// Quién lanza una acción auditada. No hay usuarios (una única clave compartida), así que canal +
// cliente (IP y hash corto de la sesión) es la identidad más honesta que se puede registrar.
// Source: "user" | "system" (el filtro de Logs). Client: null en las acciones del sistema.
public sealed record AuditActor(string Source, string Channel, string? Client)
{
    public const string UserSource = "user";
    public const string SystemSource = "system";

    public static readonly AuditActor Scheduler = System("scheduler");
    public static readonly AuditActor Queue = System("queue");
    public static readonly AuditActor AutoFix = System("autofix");
    public static readonly AuditActor Retention = System("retention");
    public static readonly AuditActor Worker = System("worker");
    public static readonly AuditActor Demo = System("demo");

    public static AuditActor Ui(string? client = null) => new(UserSource, "ui", client);

    public static AuditActor FromHttp(HttpContext context) => new(UserSource, "api", DescribeClient(context));

    // "IP · s:<8 hex>". Sin cookie de sesión (AUTH_MODE=none) queda solo la IP. Del token solo se
    // guarda un prefijo de su SHA-256: distingue sesiones sin permitir reutilizarlo.
    public static string? DescribeClient(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var session = context.Request.Cookies.TryGetValue(AuthConstants.SessionCookieName, out var token)
            && !string.IsNullOrEmpty(token)
                ? "s:" + ShortHash(token)
                : null;

        return (ip, session) switch
        {
            (null, null) => null,
            (_, null) => ip,
            (null, _) => session,
            _ => $"{ip} · {session}",
        };
    }

    private static AuditActor System(string channel) => new(SystemSource, channel, null);

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
}
