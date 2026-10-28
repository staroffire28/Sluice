namespace Sluice.Portal.Services;

// Rango de fechas de los filtros de Logs y Jobs: ambos extremos opcionales e incluidos, por día
// completo ("Hasta 9 oct" incluye todo el día 9). El día se toma en la zona que muestra la tabla
// (la del navegador; si no se indica, la del servidor), para que lo filtrado coincida con lo que
// se ve. Zone: la de Jobs, que viaja con el rango; Logs la pasa en su filtro.
public sealed record DateRange(DateOnly? From, DateOnly? To, TimeZoneInfo? Zone = null)
{
    public bool IsEmpty => From is null && To is null;

    public bool Contains(DateTimeOffset timestamp, TimeZoneInfo? zone = null)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone ?? Zone ?? TimeZoneInfo.Local).DateTime);
        return (From is null || day >= From) && (To is null || day <= To);
    }
}
