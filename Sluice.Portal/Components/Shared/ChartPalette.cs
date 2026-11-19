using ApexCharts;
using Sluice.Portal.Models;

namespace Sluice.Portal.Components.Shared;

// Colores de las gráficas de Apex (Analytics y crecimiento de una tabla), por tema: cada serie a
// 3:1 o más contra el fondo de la gráfica (WCAG 1.4.11), ya con la opacidad de 0,85 con que Apex
// pinta barras y líneas. Antes era la misma paleta en los dos temas: en oscuro, el azul de marca
// quedaba a 1,16:1.
public static class ChartPalette
{
    // Claro: primaria de marca y grises corporativos (el tercero, más oscuro: #87867f no llegaba).
    // Oscuro: las mismas familias, aclaradas.
    public static List<string> Series(Mode mode) => mode == Mode.Dark
        ? ["#8fa8d0", "#c2c0b8", "#87867f"]
        : ["#1B365D", "#5e5d59", "#706f69"];

    // Un color por estado, del mismo tono que su insignia (StatusBadge) con el matiz que llega a
    // 3:1 en cada tema. Partial y Blocked comparten insignia amarilla; aquí Blocked va en naranja
    // para no repetir color en el donut.
    public static string ForStatus(JobStatus status, Mode mode)
    {
        var dark = mode == Mode.Dark;
        return status switch
        {
            JobStatus.Running => dark ? "#479ef5" : "#0f6cbd",
            JobStatus.Ok => dark ? "#54b054" : "#107c10",
            JobStatus.Error => dark ? "#dc626d" : "#d13438",
            JobStatus.Partial => dark ? "#fde300" : "#8a6200",
            JobStatus.Blocked => dark ? "#faa06b" : "#bc4b09",
            // Cancelled: gris neutro, el del texto de su insignia.
            _ => dark ? "#adadad" : "#616161",
        };
    }
}
