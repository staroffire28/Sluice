using Microsoft.Extensions.Localization;

namespace Sluice.Portal.Services;

// Texto de un error para un aviso de la UI: el de una validación propia, en el idioma del usuario;
// el de cualquier otra excepción, tal cual.
public static class ErrorText
{
    public static string Render(IStringLocalizer l, Exception ex) =>
        ex is LocalizedException localized
            ? EventText.Render(l, localized.MessageKey, localized.MessageArgs, localized.Message)
            : ex.Message;
}
