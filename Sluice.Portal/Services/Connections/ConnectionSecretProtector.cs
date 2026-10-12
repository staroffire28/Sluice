using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Sluice.Portal.Services.Connections;

/// <summary>
/// Cifra/descifra valores sensibles de conexiones (client_secret, password,
/// connection_string) usando Data Protection. Formato en disco: "enc:v1:&lt;payload&gt;".
///
/// Protect es idempotente: un valor ya cifrado vuelve tal cual. Esto importa
/// porque al editar una conexión sin tocar el secreto, el valor cifrado existente
/// pasa de nuevo por Save (ver ConnectionService.MergeConfig).
///
/// Unprotect hace passthrough de valores legacy (sin prefijo, en claro) para
/// compatibilidad con conexiones creadas antes del cifrado.
/// </summary>
public interface IConnectionSecretProtector
{
    /// <summary>Devuelve true si el valor ya está en formato cifrado ("enc:v1:...").</summary>
    bool IsProtected(string? value);

    /// <summary>Cifra el valor. No-op si ya está cifrado, vacío o null.</summary>
    string? Protect(string? plaintext);

    /// <summary>Descifra un valor "enc:v1:..."; passthrough si es legacy en claro. Lanza si el payload no descifra.</summary>
    string? Unprotect(string? value);
}

public sealed class ConnectionSecretProtector : IConnectionSecretProtector
{
    internal const string Prefix = "enc:v1:";
    private readonly IDataProtector _protector;

    public ConnectionSecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Sluice.Portal.ConnectionSecrets");

    public bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext))
        {
            return plaintext;
        }

        return Prefix + _protector.Protect(plaintext);
    }

    public string? Unprotect(string? value)
    {
        if (!IsProtected(value))
        {
            return value; // legacy en claro: passthrough
        }

        try
        {
            return _protector.Unprotect(value![Prefix.Length..]);
        }
        catch (CryptographicException ex)
        {
            // Cert/keyring rotado o payload corrupto. Fallar claro, no devolver basura
            // que rompa ADLS/SQL río abajo con un error opaco.
            throw new InvalidOperationException(
                "No se pudo descifrar un secreto de conexión. El keyring de Data Protection " +
                "puede haber cambiado o el dato está corrupto.", ex);
        }
    }
}
