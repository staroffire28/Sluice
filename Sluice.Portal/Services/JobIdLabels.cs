namespace Sluice.Portal.Services;

public static class JobIdLabels
{
    // Ids "job-{pipeline}-{fecha}-{hash}": en pantalla basta el hash; el id completo va en el
    // tooltip, en el detalle y en las exportaciones. Un id con otro formato se muestra entero.
    public static string Short(string id)
    {
        var last = id[(id.LastIndexOf('-') + 1)..];
        return id.Count(c => c == '-') >= 3 && last.Length is >= 4 and <= 12 ? last : id;
    }
}
