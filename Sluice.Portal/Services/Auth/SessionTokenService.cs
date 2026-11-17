using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services.Auth;

// Token de sesión: {epoch}.{expiryUnix}.{hmac(epoch.expiry)}.
// - La clave aleatoria vive en DATA_DIR/auth/session.json. Antes se firmaba con la propia
//   UI_API_KEY, y una cookie capturada permitía atacar esa clave por fuerza bruta offline.
// - La clave de firma es HMAC-SHA256(clave aleatoria, UI_API_KEY vigente): rotar UI_API_KEY y
//   reiniciar invalida todas las sesiones emitidas con la anterior (si se filtró, la cookie que ya
//   obtuvo el atacante deja de valer). Quien solo tiene la cookie sigue sin poder atacar la clave
//   offline: le falta la clave aleatoria. Y no se guarda en disco nada derivado de UI_API_KEY:
//   DATA_DIR entra en los backups y una huella permitiría ese mismo ataque desde un backup.
// - El epoch permite revocar: cerrar sesión lo incrementa y todos los tokens emitidos antes dejan
//   de valer. Antes el logout solo borraba la cookie y una robada seguía válida hasta expirar.
// Solo el nodo api valida sesiones de UI: el estado se carga una vez y se mantiene en memoria.
public sealed class SessionTokenService
{
    private readonly AppOptions _options;
    private readonly TimeProvider _time;
    private readonly string _statePath;
    private readonly object _lock = new();
    private SessionState? _state;

    public SessionTokenService(IOptions<AppOptions> options, TimeProvider? time = null)
    {
        _options = options.Value;
        _time = time ?? TimeProvider.System;
        _statePath = Path.Combine(_options.DataDir, "auth", "session.json");
    }

    public string CreateToken()
    {
        var state = GetState();
        var expiryUnix = _time.GetUtcNow()
            .AddHours(_options.SessionExpiryHours)
            .ToUnixTimeSeconds();

        return $"{state.Epoch}.{expiryUnix}.{Sign(state, state.Epoch, expiryUnix)}";
    }

    public bool TryValidate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // Tres partes: el formato antiguo {expiry}.{hmac} se rechaza (obliga a iniciar sesión de nuevo).
        var parts = token.Split('.');
        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiryUnix))
        {
            return false;
        }

        var state = GetState();
        if (epoch != state.Epoch || _time.GetUtcNow().ToUnixTimeSeconds() > expiryUnix)
        {
            return false;
        }

        return SecureCompare.Equals(Sign(state, epoch, expiryUnix), parts[2]);
    }

    // Invalida todas las sesiones emitidas hasta ahora (se llama al cerrar sesión).
    public void RevokeAll()
    {
        lock (_lock)
        {
            var current = LoadOrCreate();
            _state = current with { Epoch = current.Epoch + 1 };
            AtomicJsonFile.Write(_statePath, _state);
        }
    }

    private SessionState GetState()
    {
        lock (_lock)
        {
            return _state ??= LoadOrCreate();
        }
    }

    private SessionState LoadOrCreate()
    {
        if (TryRead(out var existing))
        {
            return existing;
        }

        var fresh = new SessionState(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), 0);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);

        if (File.Exists(_statePath))
        {
            // Archivo ilegible: se regenera (las sesiones abiertas tendrán que volver a entrar).
            AtomicJsonFile.Write(_statePath, fresh);
            return fresh;
        }

        // Creación atómica: si otra instancia lo creó a la vez, gana la suya y se usa esa clave.
        return AtomicJsonFile.WriteNew(_statePath, fresh) || !TryRead(out var winner) ? fresh : winner;
    }

    private bool TryRead(out SessionState state)
    {
        state = null!;
        try
        {
            if (!File.Exists(_statePath))
            {
                return false;
            }

            using var stream = File.OpenRead(_statePath);
            var read = JsonSerializer.Deserialize<SessionState>(stream, JsonDefaults.Options);
            if (read is null || string.IsNullOrWhiteSpace(read.SigningKey))
            {
                return false;
            }

            state = read;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException)
        {
            return false;
        }
    }

    private string Sign(SessionState state, long epoch, long expiryUnix)
    {
        // Sin UI_API_KEY (Development o AUTH_MODE=none) no se emiten ni validan sesiones.
        var key = HMACSHA256.HashData(
            Convert.FromBase64String(state.SigningKey),
            Encoding.UTF8.GetBytes(_options.UiApiKey ?? string.Empty));
        var payload = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{epoch}.{expiryUnix}"));
        return Convert.ToHexString(HMACSHA256.HashData(key, payload)).ToLowerInvariant();
    }

    private sealed record SessionState(string SigningKey, long Epoch);
}
