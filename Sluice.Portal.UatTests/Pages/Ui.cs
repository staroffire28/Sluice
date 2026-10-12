using System.Text.RegularExpressions;

namespace Sluice.Portal.UatTests.Pages;

// Interacciones con los controles de Fluent UI v5 que usan todas las páginas.
public static class Ui
{
    public const float Short = 15_000;
    public const float Long = 30_000;

    // El <input> de fluent-text-input vive en su shadow DOM (abierto). El valor se enlaza en
    // 'change', así que se sale del campo para que Blazor lo reciba.
    public static async Task FillFieldAsync(this IPage page, string testId, string value)
    {
        var input = page.GetByTestId(testId).Locator("input");
        await input.FillAsync(value, new LocatorFillOptions { Timeout = Short });
        await input.BlurAsync();
    }

    public static ILocator Input(this IPage page, string testId) => page.GetByTestId(testId).Locator("input");

    // Escribe una fecha en un FluentDatePicker en el formato de su cultura: el placeholder es el
    // patrón (M/d/yyyy en inglés, d/M/yyyy en español).
    public static async Task FillDateAsync(this IPage page, string testId, DateOnly date)
    {
        var pattern = await page.GetByTestId(testId).GetAttributeAsync("placeholder") ?? "yyyy-MM-dd";
        await page.FillFieldAsync(testId, date.ToString(pattern, System.Globalization.CultureInfo.InvariantCulture));
    }

    // Pulsa un enlace de descarga (FluentAnchorButton con ForceLoad): el navegador descarga el
    // adjunto sin salir de la página.
    public static Task<IDownload> DownloadFromLinkAsync(this IPage page, string testId) =>
        page.RunAndWaitForDownloadAsync(() => page.ClickTestIdAsync(testId),
            new PageRunAndWaitForDownloadOptions { Timeout = 60_000 });

    // Abre un FluentSelect y elige la opción por su texto visible. Si el clic llega mientras se
    // cierra otro desplegable, solo lo descarta y la lista no se abre: se vuelve a pulsar, como
    // haría el usuario. Al final se espera a que la lista se cierre para no pisar el siguiente paso.
    public static async Task ChooseAsync(this IPage page, string testId, string option)
    {
        var item = page.FluentOption(option);
        for (var attempt = 1; ; attempt++)
        {
            await page.GetByTestId(testId).ClickAsync(new LocatorClickOptions { Timeout = Short });
            try
            {
                await item.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
                break;
            }
            catch (TimeoutException) when (attempt < 3)
            {
            }
        }

        await item.ClickAsync(new LocatorClickOptions { Timeout = Short });
        await Assertions.Expect(page.Locator("fluent-option:visible"))
            .ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = Short });
    }

    // Texto que muestra un FluentSelect cerrado (el botón combobox de fluent-dropdown).
    public static ILocator Selected(this IPage page, string testId) =>
        page.GetByTestId(testId).Locator("button[role=combobox]");

    public static Task ClickTestIdAsync(this IPage page, string testId, float timeout = Short) =>
        page.GetByTestId(testId).ClickAsync(new LocatorClickOptions { Timeout = timeout });

    // Abre el menú de acciones de una fila y pulsa una de sus opciones.
    public static async Task MenuAsync(this IPage page, string menuTestId, string itemTestId)
    {
        await page.RevealAsync(page.GetByTestId(menuTestId));
        await page.ClickTestIdAsync(menuTestId, Long);
        await page.ClickTestIdAsync(itemTestId);
    }

    // Fila de una FluentDataGrid que contiene el texto.
    public static ILocator Row(this IPage page, string text) =>
        page.Locator("tr").Filter(new LocatorFilterOptions { HasTextString = text });

    // Aviso (toast o barra) con el texto: lo que el usuario lee tras una acción.
    public static Task ExpectMessageAsync(this IPage page, string text, float timeout = Long) =>
        Assertions.Expect(page.GetByText(text).First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = timeout });

    public static Task ExpectMessageAsync(this IPage page, Regex text, float timeout = Long) =>
        Assertions.Expect(page.GetByText(text).First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = timeout });

    // Las tablas muestran 20 filas por página: como haría el usuario, vuelve a la primera página y
    // avanza con "Siguiente" hasta encontrar el elemento.
    public static async Task RevealAsync(this IPage page, ILocator target)
    {
        if (await AppearsAsync(target, 5_000))
        {
            return;
        }

        var previous = page.GetByTestId("page-prev").First;
        while (await previous.CountAsync() > 0 && await previous.GetAttributeAsync("disabled") is null)
        {
            await previous.ClickAsync(new LocatorClickOptions { Timeout = Short });
        }

        var next = page.GetByTestId("page-next").First;
        while (!await AppearsAsync(target, 1_500))
        {
            if (await next.CountAsync() == 0 || await next.GetAttributeAsync("disabled") is not null)
            {
                break;
            }

            await next.ClickAsync(new LocatorClickOptions { Timeout = Short });
        }

        await Assertions.Expect(target.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 1_000 });
    }

    // Los botones de la columna de acciones (la última) no deben salirse de su fila ni montarse
    // unos sobre otros: sin MultiLine la fila tenía alto fijo y el botón que saltaba de línea
    // invadía la fila siguiente. Se revisan las filas de datos (las que tienen td; la cabecera usa
    // th) y se exige al menos una, para que la comprobación no pase en vacío.
    public static async Task AssertRowActionsFitAsync(this IPage page)
    {
        var result = await page.EvaluateAsync<System.Text.Json.JsonElement>(@"() => {
            const out = [];
            const rows = [...document.querySelectorAll('table')[0].querySelectorAll('tr')].filter(tr => tr.querySelector('td'));
            rows.forEach((tr, i) => {
                const tds = [...tr.querySelectorAll('td')];
                const cell = tds[tds.length - 1].getBoundingClientRect();
                const buttons = [...tds[tds.length - 1].querySelectorAll('fluent-button')].map(b => b.getBoundingClientRect());
                buttons.forEach((b, j) => {
                    if (b.bottom > cell.bottom + 1 || b.top < cell.top - 1) out.push(`fila ${i}: botón ${j} fuera de la fila`);
                    buttons.forEach((c, k) => {
                        if (k > j && b.left < c.right && c.left < b.right && b.top < c.bottom && c.top < b.bottom) out.push(`fila ${i}: botones ${j} y ${k} solapados`);
                    });
                });
            });
            return { rows: rows.length, problems: out };
        }");
        var problems = result.GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.True(result.GetProperty("rows").GetInt32() > 0, "No hay filas de datos que comprobar.");
        Assert.True(problems.Count == 0, "Acciones solapadas: " + string.Join("; ", problems));
    }

    // Espera a que un campo muestre el valor del registro que se acaba de abrir: el diálogo se
    // reutiliza y, justo al abrirlo, el campo aún puede tener el valor del anterior. Los EditAsync lo
    // comprueban con Slugify(nombre) == id: tras renombrar, el id conserva el slug antiguo y no casa.
    public static async Task WaitForLoadedAsync(this IPage page, ILocator input, Func<string, bool> loaded)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Short);
        string value;
        while (!loaded(value = await input.InputValueAsync(new LocatorInputValueOptions { Timeout = Short })))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"El diálogo no cargó el registro esperado (muestra '{value}').");
            }

            await page.WaitForTimeoutAsync(100);
        }
    }

    private static async Task<bool> AppearsAsync(ILocator target, float timeout)
    {
        try
        {
            await target.First.WaitForAsync(new LocatorWaitForOptions { Timeout = timeout });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static Task ExpectGoneAsync(this ILocator locator, float timeout = Short) =>
        Assertions.Expect(locator).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = timeout });

    public static Task ExpectPresentAsync(this ILocator locator, float timeout = Long) =>
        Assertions.Expect(locator.First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = timeout });
}
