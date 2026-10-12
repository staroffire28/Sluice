using System.Globalization;
using Sluice.Portal.Services;

namespace Sluice.Portal.Tests;

// La guía se sirve dentro de /ayuda, cuya cabecera ya es el h1 de la página: los encabezados del
// Markdown bajan un nivel (el título del documento pasa a h2) para no repetir h1. Los .md no
// cambian, así que en GitHub se siguen viendo igual.
public sealed class DocsServiceTests
{
    [Fact]
    public void Document_headings_are_shifted_one_level_down()
    {
        var html = RenderInEnglish("functionality");

        Assert.DoesNotContain("<h1", html);
        Assert.Contains("<h2 id=\"functionality\">Functionality</h2>", html);
        Assert.Contains("<h3 id=\"how-the-pipeline-works\">", html);
    }

    [Fact]
    public void Internal_links_still_point_to_help_routes()
    {
        var html = RenderInEnglish("introduction");

        Assert.Contains("href=\"/ayuda/reference", html);
        Assert.DoesNotContain("href=\"04-reference.md", html);
    }

    private static string RenderInEnglish(string slug)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try
        {
            return new DocsService().RenderHtml(slug)!;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
