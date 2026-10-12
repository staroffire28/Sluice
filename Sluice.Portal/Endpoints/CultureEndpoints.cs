using Microsoft.AspNetCore.Localization;

namespace Sluice.Portal.Endpoints;

public static class CultureEndpoints
{
    // El toggle de idioma navega aquí (recarga completa): fija la cookie de cultura
    // y vuelve a la página de origen. Blazor Server no puede cambiar la cultura del
    // circuito en caliente, por eso el ciclo cookie + recarga.
    public static IEndpointRouteBuilder MapCultureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/culture/set", (string culture, string? redirectUri, HttpContext context) =>
        {
            var normalized = culture == "en" ? "en" : "es";
            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(normalized)),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), Path = "/" });

            // Solo se acepta una ruta local: evita open-redirect Y la excepción no
            // controlada (500) que LocalRedirect lanzaría ante una URL absoluta o
            // protocol-relative (//host, /\host) recibida por query.
            var target = IsLocalPath(redirectUri) ? redirectUri! : "/";
            return Results.LocalRedirect(target);
        });

        return app;
    }

    private static bool IsLocalPath(string? uri) =>
        !string.IsNullOrEmpty(uri)
        && uri[0] == '/'
        && (uri.Length == 1 || (uri[1] != '/' && uri[1] != '\\'));
}
