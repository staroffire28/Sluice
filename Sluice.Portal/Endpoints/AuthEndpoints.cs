using Microsoft.AspNetCore.Antiforgery;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Auth;
using Sluice.Portal.Services.Logging;

namespace Sluice.Portal.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Key);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Cada intento queda en el registro de actividad (nunca la clave): el correcto, la clave
        // incorrecta o vacía, el bloqueo por fuerza bruta y el formulario sin token antiforgery.
        app.MapPost("/auth/login", async (HttpContext context, IAuthService auth, IAntiforgery antiforgery, LoginThrottle throttle, IAuditLog audit) =>
        {
            var isJson = context.Request.HasJsonContentType();
            var actor = AuditActor.FromHttp(context, isJson ? "api" : "ui");
            void Record(AuditOutcome outcome, string message) =>
                audit.Record(actor, "auth.login", outcome, null, null, null, message);

            if (!isJson && !await TryValidateFormAntiforgeryAsync(context, antiforgery))
            {
                Record(AuditOutcome.Rejected, "Inicio de sesión rechazado: el formulario no trae un token antiforgery válido.");
                return Results.Redirect("/login?error=csrf");
            }

            // Fuerza bruta: demasiados fallos recientes desde este cliente => se rechaza sin
            // comprobar la clave (ni siquiera la correcta) hasta que expire la ventana.
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.IsBlocked(client))
            {
                Record(AuditOutcome.Rejected, "Inicio de sesión bloqueado: demasiados intentos fallidos recientes desde este cliente.");
                return isJson
                    ? Results.StatusCode(StatusCodes.Status429TooManyRequests)
                    : Results.Redirect("/login?error=throttled");
            }

            var key = await ReadKeyAsync(context);

            if (string.IsNullOrWhiteSpace(key))
            {
                Record(AuditOutcome.Rejected, "Inicio de sesión rechazado: no se indicó la clave.");
                return isJson
                    ? Results.BadRequest(new { error = "Key is required in request body." })
                    : Results.Redirect("/login?error=required");
            }

            if (!auth.TryLogin(key, context))
            {
                throttle.RecordFailure(client);
                Record(AuditOutcome.Rejected, "Inicio de sesión rechazado: clave incorrecta.");
                return isJson
                    ? Results.Unauthorized()
                    : Results.Redirect("/login?error=invalid");
            }

            throttle.Reset(client);
            Record(AuditOutcome.Ok, "Inicio de sesión.");
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

        app.MapPost("/auth/logout", async (HttpContext context, IAuthService auth, IAntiforgery antiforgery, IAuditLog audit) =>
        {
            // Antes de cerrarla: el actor lleva el hash de la sesión que se cierra.
            var actor = AuditActor.FromHttp(context, "ui");
            if (!await TryValidateFormAntiforgeryAsync(context, antiforgery))
            {
                audit.Record(actor, "auth.logout", AuditOutcome.Rejected, null, null, null,
                    "Cierre de sesión rechazado: el formulario no trae un token antiforgery válido.");
                return Results.BadRequest();
            }

            auth.SignOut(context);
            audit.Record(actor, "auth.logout", AuditOutcome.Ok, null, null, null, "Cierre de sesión.");
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