namespace Sluice.Portal.Services;

// Una validación propia con su texto como clave del recurso y argumentos, para que la UI la pinte
// en el idioma del usuario (ErrorText). Message sigue igual para la API y los logs.
public class LocalizedException(string message, string messageKey, params object?[] messageArgs)
    : InvalidOperationException(message)
{
    public string MessageKey { get; } = messageKey;

    public object?[] MessageArgs { get; } = messageArgs;
}
