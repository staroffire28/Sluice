namespace Sluice.Portal.Services;

// Modo claro/oscuro del circuito actual. Fluent UI v5 persiste y aplica el tema por su cuenta
// (IThemeService + localStorage), pero no avisa a los componentes .NET: ThemeToggle publica aquí
// cada cambio para que las gráficas ApexCharts, que no leen las variables CSS, se repinten.
public sealed class ThemeState
{
    public bool IsDark { get; private set; }

    public event Action? Changed;

    public void Set(bool isDark)
    {
        if (IsDark == isDark)
        {
            return;
        }

        IsDark = isDark;
        Changed?.Invoke();
    }
}
