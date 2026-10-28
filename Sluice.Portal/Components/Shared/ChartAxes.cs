using ApexCharts;

namespace Sluice.Portal.Components.Shared;

// Ejes de las gráficas de Apex (Analytics y crecimiento de una tabla).
public static class ChartAxes
{
    // Eje de cantidades (jobs, filas): sin decimales. Con valores pequeños, Apex repartía las marcas
    // de 0,5 en 0,5 ("125,5", o "1, 1, 2, 2" al redondear): hasta 4, una marca por unidad.
    public static YAxis Count(double max) => max <= 4
        ? new YAxis { Min = 0, Max = 4, TickAmount = 4, DecimalsInFloat = 0 }
        : new YAxis { Min = 0, DecimalsInFloat = 0 };
}
