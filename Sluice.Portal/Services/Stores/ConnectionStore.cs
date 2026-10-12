using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Connections;

namespace Sluice.Portal.Services.Stores;

/// <summary>
/// Persiste conexiones. Los valores sensibles (SecretMasker.IsSecretKey, por patrón de
/// nombre) se cifran at-rest en Save. GetById/ListAll devuelven el Config CIFRADO a
/// propósito: solo ConnectionResolver.GetUnmasked descifra, justo antes de conectar. La UI
/// lee de aquí y aplica SecretMasker.Mask, que enmascara el valor cifrado igual que el claro.
/// </summary>
public sealed class ConnectionStore : JsonFileStore<Connection>
{
    private readonly IConnectionSecretProtector _protector;

    public ConnectionStore(IOptions<AppOptions> options, IConnectionSecretProtector protector)
        : base(options, "connections") => _protector = protector;

    public IReadOnlyList<Connection> ListAll() => ListAll(Read);

    public Connection? GetById(string id) => Get(id);

    public void Save(Connection connection) =>
        Save(connection.Id, connection with { Config = ProtectConfig(connection.Config) });

    public void SaveNew(Connection connection) =>
        SaveNew(connection.Id, connection with { Config = ProtectConfig(connection.Config) });

    public new bool Delete(string id) => base.Delete(id);

    // Cifra solo las claves sensibles. Protect es idempotente, así que un secreto
    // sin cambio (que vuelve cifrado desde MergeConfig) no se re-cifra. El MaskValue
    // nunca debería llegar hasta aquí (MergeConfig lo resuelve), pero lo excluimos
    // por defensa para no cifrar el placeholder "***".
    private Dictionary<string, string?> ProtectConfig(IReadOnlyDictionary<string, string?> config)
    {
        var result = new Dictionary<string, string?>(config.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config)
        {
            result[key] = SecretMasker.IsSecretKey(key)
                          && !string.IsNullOrEmpty(value)
                          && value != SecretMasker.MaskValue
                ? _protector.Protect(value)
                : value;
        }

        return result;
    }
}