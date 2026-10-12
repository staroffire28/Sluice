using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Sluice.Portal.Components.Layout;

// Página del menú lateral: la pintan el menú expandido (NavMenu) y el riel de iconos (NavRail).
public sealed record NavEntry(string Href, string TextKey, Icon Icon, NavLinkMatch Match = NavLinkMatch.Prefix);

// Grupo del menú. Key es la clave persistida en localStorage (sluice-nav-groups): no cambiarla.
public sealed record NavGroup(string Key, string TitleKey, Icon Icon, IReadOnlyList<NavEntry> Entries);

// Lista única de páginas del menú (Href, icono y clave de texto L["Nav.*"]).
public static class NavCatalog
{
    // Instancias nuevas en cada acceso: los Icon de Fluent son mutables (WithColor) y el layout
    // pinta varios menús a la vez; cada componente guarda su copia.
    public static IReadOnlyList<NavGroup> Groups =>
    [
        new("operation", "Nav.Group.Operation", new Icons.Regular.Size20.Apps(),
        [
            new("/", "Nav.Dashboard", new Icons.Regular.Size20.Home(), NavLinkMatch.All),
            new("/jobs", "Nav.Jobs", new Icons.Regular.Size20.History()),
            new("/pipelines", "Nav.Pipelines", new Icons.Regular.Size20.Flow()),
        ]),
        new("data", "Nav.Group.Data", new Icons.Regular.Size20.Database(),
        [
            new("/tables", "Nav.Tables", new Icons.Regular.Size20.Table()),
            new("/sources", "Nav.Sources", new Icons.Regular.Size20.Cloud()),
            new("/destinations", "Nav.Destinations", new Icons.Regular.Size20.Database()),
            new("/snapshots", "Nav.Snapshots", new Icons.Regular.Size20.CalendarClock()),
        ]),
        new("quality", "Nav.Group.Quality", new Icons.Regular.Size20.CheckmarkCircle(),
        [
            new("/reconcile", "Nav.Reconcile", new Icons.Regular.Size20.CheckmarkCircle()),
            new("/parquet", "Nav.Parquet", new Icons.Regular.Size20.DocumentData()),
            new("/analytics", "Nav.Analytics", new Icons.Regular.Size20.DataTrending()),
        ]),
        new("system", "Nav.Group.System", new Icons.Regular.Size20.Settings(),
        [
            new("/logs", "Nav.Logs", new Icons.Regular.Size20.TextBulletListSquare()),
            new("/settings", "Nav.Settings", new Icons.Regular.Size20.Settings()),
            new("/ayuda", "Nav.Help", new Icons.Regular.Size20.BookQuestionMark()),
        ]),
    ];
}
