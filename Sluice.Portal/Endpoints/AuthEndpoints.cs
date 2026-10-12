using Microsoft.AspNetCore.Antiforgery;
using Sluice.Portal.Services.Auth;

namespace Sluice.Portal.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Key);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", async (HttpContext context, IAuthService auth, IAntiforgery antiforgery, LoginThrottle throttle) =>
        {
            var isJson = context.Request.HasJsonContentType();

            if (!isJson && !await TryValidateFormAntiforgeryAsync(context, antiforgery))
            {
                return Results.Redirect("/login?error=csrf");
            }

            // Fuerza bruta: demasiados fallos recientes desde este cliente => se rechaza sin
            // comprobar la clave (ni siquiera la correcta) hasta que expire la ventana.
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.IsBlocked(client))
            {
                return isJson
                    ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                    : Results.Redirect("/login?error=throttled");
            }

            var key = await ReadKeyAsync(context);

            if (string.IsNullOrWhiteSpace(key))
            {
                return isJson
                    ? Results.BadRequest(new { error = "Key is required in request body." })
                    : Results.Redirect("/login?error=required");
            }

            if (!auth.TryLogin(key, context))
            {
                throttle.RecordFailure(client);
                return isJson
                    ? Results.Unauthorized()
                    : Results.Redirect("/login?error=invalid");
            }

            throttle.Reset(client);
            return Results.Redirect("/");
        })
        .DisableAntiforgery();

        // Sin clave (AUTH_MODE=none, o Development sin UI_API_KEY): el botón "Entrar" de la landing.
        // No autentica; solo deja la cookie de bienvenida y lleva al portal. Con clave no aplica:
        // se vuelve al login.
        app.MapGet("/auth/enter", (HttpContext context, IAuthService auth) =>
        {
            if (auth.IsUiAuthEnabled)
            {
                return Results.Redirect("/login");
            }

            auth.Enter(context);
            return Results.Redirect("/");
        });

        app.MapPost("/auth/logout", async (HttpContext context, IAuthService auth, IAntiforgery antiforgery) =>
        {
            if (!await TryValidateFormAntiforgeryAsync(context, antiforgery))
            {
                return Results.BadRequest();
            }

            auth.SignOut(context);
            return Results.Redirect("/login");
        })
        .DisableAntiforgery();

        return app;
    }

    private static async Task<bool> TryValidateFormAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadKeyAsync(HttpContext context)
    {
        if (context.Request.HasJsonContentType())
        {
            var body = await context.Request.ReadFromJsonAsync<LoginRequest>(context.RequestAborted);
            return body?.Key;
        }

        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return form["key"].FirstOrDefault();
        }

        return null;
    }
}