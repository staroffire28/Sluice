using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Middleware;

public sealed class DualAuthMiddleware
{
    private readonly RequestDelegate _next;

    public DualAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAuthService auth)
    {
        // Acceso abierto: nada se protege, pero la primera página de cada sesión del navegador
        // pasa por la landing (con "Entrar", que deja la cookie de bienvenida). La API no.
        // /_blazor (negociación y circuito) tampoco: son peticiones del propio Blazor, no páginas.
        // Solo aquí: en modo con clave el circuito sigue exigiendo sesión.
        if (auth.IsOpenAccess
            && !IsExemptPath(context.Request.Path)
            && !IsApiPath(context.Request.Path)
            && !context.Request.Path.StartsWithSegments("/_blazor")
            && IsPageRequest(context)
            && !context.Request.Cookies.ContainsKey(AuthConstants.WelcomeCookieName))
        {
            context.Response.Redirect("/login");
            return;
        }

        if (!auth.IsUiAuthEnabled || IsExemptPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        if (IsApiPath(context.Request.Path))
        {
            if (!auth.HasValidUiSession(context))
            {
                await WriteUnauthorizedAsync(context);
                return;
            }

            await _next(context);
            return;
        }

        if (!auth.HasValidUiSession(context))
        {
            if (IsPageRequest(context))
            {
                context.Response.Redirect("/login");
                return;
            }

            await WriteUnauthorizedAsync(context);
            return;
        }

        await _next(context);
    }

    private static bool IsExemptPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/health/ready", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/internal/health", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/login", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/auth/logout", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/auth/enter", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/culture/", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/_content", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/favicon.png", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/apple-touch-icon.png", StringComparison.OrdinalIgnoreCase)
            || value.Equals("/logo.svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApiPath(PathString path) =>
        (path.Value ?? string.Empty).StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

    private static bool IsPageRequest(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        || HttpMethods.IsHead(context.Request.Method);

    private static async Task WriteUnauthorizedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unauthorized");
    }
}

public static class DualAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseDualAuth(this IApplicationBuilder app) =>
        app.UseMiddleware<DualAuthMiddleware>();
}