using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services.Auth;

public sealed class AuthService : IAuthService
{
    private readonly AppOptions _options;
    private readonly SessionTokenService _sessionTokens;

    // Fuera de Development la cookie es siempre Secure: el portal va detrás de un proxy que
    // termina TLS, así que Request.IsHttps puede ser false aunque el usuario use https.
    private readonly bool _forceSecureCookie;

    public AuthService(
        IOptions<AppOptions> options,
        SessionTokenService sessionTokens,
        IHostEnvironment? environment = null)
    {
        _options = options.Value;
        _sessionTokens = sessionTokens;
        _forceSecureCookie = environment is not null && !environment.IsDevelopment();
    }

    public bool IsUiAuthEnabled => _options.IsUiAuthEnabled;

    public bool IsOpenAccess => _options.IsOpenAccess;

    // No es una credencial: no se fuerza Secure (en una red interna por http sin TLS no volvería y
    // la landing reaparecería en cada página) y Lax para que un enlace externo (una alerta) no
    // vuelva a pasar por la landing.
    public void Enter(HttpContext httpContext) =>
        httpContext.Response.Cookies.Append(AuthConstants.WelcomeCookieName, "1", new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Path = "/",
        });

    public bool TryLogin(string apiKey, HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(_options.UiApiKey))
        {
            return false;
        }

        if (!SecureCompare.Equals(apiKey, _options.UiApiKey))
        {
            return false;
        }

        var token = _sessionTokens.CreateToken();
        httpContext.Response.Cookies.Append(
            AuthConstants.SessionCookieName,
            token,
            BuildCookieOptions(httpContext));

        return true;
    }

    public bool HasValidUiSession(HttpContext httpContext)
    {
        if (!IsUiAuthEnabled)
        {
            return true;
        }

        return httpContext.Request.Cookies.TryGetValue(AuthConstants.SessionCookieName, out var token)
            && _sessionTokens.TryValidate(token);
    }

    // Cerrar sesión invalida TODAS las sesiones abiertas (incluida una cookie robada), no solo la
    // de este navegador: el token lleva un epoch que se incrementa aquí.
    public void SignOut(HttpContext httpContext)
    {
        _sessionTokens.RevokeAll();
        httpContext.Response.Cookies.Delete(AuthConstants.SessionCookieName, BuildCookieOptions(httpContext));
    }

    private CookieOptions BuildCookieOptions(HttpContext httpContext) =>
        new()
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = _forceSecureCookie || httpContext.Request.IsHttps,
            Path = "/",
        };
}