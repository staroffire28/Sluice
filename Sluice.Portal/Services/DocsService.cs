using System.Globalization;
using Markdig;
using Markdig.Syntax;

namespace Sluice.Portal.Services;

public sealed record DocPage(string Slug, string TitleKey, string FileBase);

// Sirve la guía de usuario (Markdown) dentro del portal. La fuente única son los
// archivos de docs/guia-usuario, empaquetados junto a la app (ver csproj). El índice
// es curado: define el orden del menú, la clave de título (localizable) y el nombre
// base del archivo. El slug NUNCA se usa para construir rutas de archivo (defensa
// contra path traversal): solo se resuelve contra esta lista.
//
// Bilingüe: cada documento tiene una versión en inglés ({base}.md) y una en español
// ({base}.es.md). Se sirve la del idioma activo del portal; si falta la traducción,
// cae a la versión en inglés.
public sealed class DocsService
{
    public static readonly IReadOnlyList<DocPage> Pages =
    [
        new("introduction",  "Page.Help.Doc.Introduction",  "01-introduction"),
        new("functionality", "Page.Help.Doc.Functionality", "02-functionality"),
        new("installation",  "Page.Help.Doc.Installation",  "03-installation"),
        new("reference",     "Page.Help.Doc.Reference",     "04-reference"),
    ];

    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "guia-usuario");

    public DocPage? Find(string slug) =>
        Pages.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    // HTML renderizado del documento en el idioma activo, con los enlaces internos .md
    // reescritos a rutas del portal (/ayuda/{slug}). Null si el slug no está en el índice
    // o no hay archivo. Se relee en cada llamada (no es hot path): un cambio en el .md
    // aplica sin reiniciar.
    public string? RenderHtml(string slug)
    {
        var page = Find(slug);
        if (page is null)
        {
            return null;
        }

        var path = ResolvePath(page);
        if (path is null)
        {
            return null;
        }

        // La cabecera de /ayuda ya es el h1 de la página: los encabezados del documento bajan un
        // nivel (su título pasa a h2). Los .md no cambian, así que en GitHub se ven igual.
        var document = Markdown.Parse(File.ReadAllText(path), Pipeline);
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            heading.Level = Math.Min(heading.Level + 1, 6);
        }

        return RewriteInternalLinks(document.ToHtml(Pipeline));
    }

    // Español → {base}.es.md si existe; en cualquier otro caso, {base}.md (inglés).
    private string? ResolvePath(DocPage page)
    {
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es")
        {
            var localized = Path.Combine(_root, $"{page.FileBase}.es.md");
            if (File.Exists(localized))
            {
                return localized;
            }
        }

        var fallback = Path.Combine(_root, $"{page.FileBase}.md");
        return File.Exists(fallback) ? fallback : null;
    }

    // href="01-introduction.md"        → href="/ayuda/introduction"
    // href="04-reference.md#glossary"  → href="/ayuda/reference#glossary"
    // Las versiones .es.md enlazan con los mismos nombres base .md a propósito, así el
    // idioma se vuelve a resolver al navegar.
    private static string RewriteInternalLinks(string html)
    {
        foreach (var p in Pages)
        {
            html = html.Replace($"href=\"{p.FileBase}.md", $"href=\"/ayuda/{p.Slug}", StringComparison.OrdinalIgnoreCase);
        }

        return html;
    }
}
