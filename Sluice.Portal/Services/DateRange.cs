namespace Sluice.Portal.Services;

// Rango de fechas de los filtros de Logs y Jobs: ambos extremos opcionales e incluidos, por día
// completo ("Hasta 9 oct" incluye todo el día 9). El día se toma en la zona que muestran las
// tablas (la del servidor; en el contenedor, UTC), para que lo filtrado coincida con lo que se ve.
public sealed record DateRange(DateOnly? From, DateOnly? To)
{
    public bool IsEmpty => From is null && To is null;

    public bool Contains(DateTimeOffset timestamp, TimeZoneInfo? zone = null)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone ?? TimeZoneInfo.Local).DateTime);
        return (From is null || day >= From) && (To is null || day <= To);
    }
}
