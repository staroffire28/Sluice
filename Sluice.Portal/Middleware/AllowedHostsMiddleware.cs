using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Middleware;

// DNS rebinding: una web cuyo dominio pasa a resolver a la IP del portal le habla con su propio nombre
// en Host y, para el navegador, es "same-origin" (ApiCrossSiteGuard la deja pasar). Con AUTH_MODE=none
// no hay nada más que la frene. Ese ataque nunca llega como localhost ni como IP literal, así que se
// aceptan esos y los nombres de ALLOWED_HOSTS; el resto recibe 400 antes de tocar nada (ni estáticos,
// ni Blazor, ni /api, ni /health). El HostFiltering de ASP.NET no sabe decir "cualquier IP".
public sealed class AllowedHostsMiddleware
{
    // Un escáner prueba muchos nombres: se avisa una vez por host y solo de los primeros.
    private const int MaxLoggedHosts = 20;

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<string> _allowedHosts;
    private readonly ILogger<AllowedHostsMiddleware> _logger;
    private readonly ConcurrentDictionary<string, byte> _loggedHosts = new(StringComparer.OrdinalIgnoreCase);

    public AllowedHostsMiddleware(RequestDelegate next, IOptions<AppOptions> options, ILogger<AllowedHostsMiddleware> logger)
    {
        _next = next;
        _allowedHosts = options.Value.AllowedHosts;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (IsAllowed(host))
        {
            await _next(context);
            return;
        }

        if (_loggedHosts.Count < MaxLoggedHosts && _loggedHosts.TryAdd(host, 0))
        {
            _logger.LogWarning(
                "Request refused: host {Host} is not allowed. To reach the portal by this name, add it to ALLOWED_HOSTS.",
                host);
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Host not allowed. To reach the portal by this name, add it to ALLOWED_HOSTS.");
    }

    private bool IsAllowed(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Host trae las IPv6 entre corchetes ("[::1]").
        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        if (string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase)
            || Uri.CheckHostName(bare) is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return true;
        }

        foreach (var allowed in _allowedHosts)
        {
            // "*.intra.example" vale para cualquier subdominio (a.intra.example, a.b.intra.example),
            // no para intra.example.
            if (allowed.StartsWith("*.", StringComparison.Ordinal)
                    ? bare.Length > allowed.Length - 1 && bare.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(bare, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public static class AllowedHostsMiddlewareExtensions
{
    // Con AUTH_MODE=key y sin ALLOWED_HOSTS no se filtra nada (la clave ya protege): ni se añade.
    public static IApplicationBuilder UseAllowedHosts(this IApplicationBuilder app, AppOptions options) =>
        options.IsHostFilterActive ? app.UseMiddleware<AllowedHostsMiddleware>() : app;
}
