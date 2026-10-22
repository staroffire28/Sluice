using System.Globalization;
using Microsoft.JSInterop;

namespace Sluice.Portal.Services;

// Fechas en la zona del navegador y en la cultura elegida (es/en). El servidor no conoce la zona
// del usuario (en Docker es UTC): se pide una vez por circuito a sluiceUi.timeZone y, mientras no
// se conoce (prerender), se usa la del servidor.
public sealed class BrowserTime(IJSRuntime js, ILogger<BrowserTime> logger)
{
    private TimeZoneInfo? _zone;

    public TimeZoneInfo Zone => _zone ?? TimeZoneInfo.Local;

    // Solo desde OnAfterRenderAsync (necesita el circuito conectado). Devuelve true si la zona
    // acaba de conocerse y la página tiene que repintar.
    public async Task<bool> InitAsync()
    {
        if (_zone is not null)
        {
            return false;
        }

        try
        {
            var id = await js.InvokeAsync<string?>("sluiceUi.timeZone");
            if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
            {
                _zone = tz;
                return true;
            }
        }
        catch (JSException ex)
        {
            logger.LogDebug(ex, "No se pudo leer la zona horaria del navegador.");
        }

        return false;
    }

    public DateTimeOffset ToLocal(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone);

    public string Format(DateTimeOffset at, string format = "g") => ToLocal(at).ToString(format, CultureInfo.CurrentCulture);
}
