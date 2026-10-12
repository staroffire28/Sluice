namespace Sluice.Portal.Services.Auth;

public interface IAuthService
{
    bool IsUiAuthEnabled { get; }

    // AUTH_MODE=none: sin clave a propósito; la landing ofrece "Entrar".
    bool IsOpenAccess { get; }

    // Acceso abierto: deja la cookie de bienvenida para no volver a mostrar la landing en esta sesión.
    void Enter(HttpContext httpContext);
    bool TryLogin(string apiKey, HttpContext httpContext);
    bool HasValidUiSession(HttpContext httpContext);
    void SignOut(HttpContext httpContext);
}