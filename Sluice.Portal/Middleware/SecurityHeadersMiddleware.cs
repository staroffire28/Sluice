namespace Sluice.Portal.Middleware;

// Cabeceras de seguridad en todas las respuestas. La CSP va de momento solo en modo informe: el
// navegador avisa en la consola de lo que bloquearía sin bloquear nada (el script inline de App.razor
// necesita un nonce antes de poder aplicarla de verdad).
public sealed class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; "
        + "form-action 'self'; object-src 'none'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XFrameOptions = "DENY";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers["Content-Security-Policy-Report-Only"] = ContentSecurityPolicy;
            return Task.CompletedTask;
        });

        return _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();
}
