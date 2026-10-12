namespace Sluice.Portal.Services.Auth;

public static class AuthConstants
{
    public const string SessionCookieName = "sluice_session";

    // Acceso abierto (AUTH_MODE=none): marca que este navegador ya pasó por la landing en esta
    // sesión. No es una credencial; solo evita mostrar la landing en cada página.
    public const string WelcomeCookieName = "sluice_welcome";
    public const string DefaultUiApiKeyPlaceholder = "changeme-ui";

    public static bool IsPlaceholderKey(string? key) =>
        string.Equals(key, DefaultUiApiKeyPlaceholder, StringComparison.Ordinal);
}
