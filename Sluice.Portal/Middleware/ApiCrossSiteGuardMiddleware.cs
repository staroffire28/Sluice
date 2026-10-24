namespace Sluice.Portal.Middleware;

// CSRF en /api: la sesión es una cookie, así que un formulario o un fetch desde otro sitio (o desde un
// subdominio hermano, que es "same-site") podía lanzar, resetear o borrar con ella, y con
// AUTH_MODE=none sin sesión siquiera. Los métodos que mutan solo pasan si el navegador dice que vienen
// del propio origen (Sec-Fetch-Site: same-origin) o si traen la cabecera propia de los clientes de la
// API: otro sitio no puede añadirla sin una petición CORS previa, que el portal no acepta.
public sealed class ApiCrossSiteGuardMiddleware
{
    public const string ClientHeader = "X-Sluice-Client";

    private readonly RequestDelegate _next;

    public ApiCrossSiteGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method)
            && !string.Equals(request.Headers["Sec-Fetch-Site"], "same-origin", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(request.Headers[ClientHeader]))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync($"Cross-site request refused: send the {ClientHeader} header from API clients.");
            return;
        }

        await _next(context);
    }
}

public static class ApiCrossSiteGuardMiddlewareExtensions
{
    public static IApplicationBuilder UseApiCrossSiteGuard(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiCrossSiteGuardMiddleware>();
}
