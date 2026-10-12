using System.Globalization;
using System.Resources;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Sluice.Portal;

// Fluent UI solo trae sus textos (aria-label "Menu"/"Close" de la hamburguesa, mensajes de la
// tabla...) en inglés. Este localizador los busca en SharedResource como "Fluent.<clave de la
// librería>", en la cultura de la petición; si la clave no está, usa el texto de la librería.
public sealed class SharedFluentLocalizer : IFluentLocalizer
{
    private static readonly ResourceManager Resources =
        new("Sluice.Portal.Resources.SharedResource", typeof(SharedResource).Assembly);

    public string this[string key, params object[] arguments]
    {
        get
        {
            var value = Resources.GetString("Fluent." + key, CultureInfo.CurrentUICulture);
            return value is null
                ? this.GetDefault(key, arguments)
                : string.Format(CultureInfo.CurrentCulture, value, arguments);
        }
    }
}
