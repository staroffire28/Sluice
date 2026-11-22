using System.Text.Json;

namespace Sluice.Portal.Services.Stores;

// Ficheros de pipelines, conexiones y jobs que los almacenes no pudieron leer en su último listado.
// Antes un JSON ilegible solo dejaba un aviso en el log: el pipeline desaparecía de la UI y el
// scheduler dejaba de ejecutarlo sin que nada lo dijera. Lo alimentan los listados que ya hacen el
// scheduler y la UI (no lee carpetas por su cuenta) y vive en memoria; lo leen el Panel, /health y
// el aviso del scheduler. Solo guarda la ruta relativa a DATA_DIR y el tipo de error, nunca el
// contenido ni la ruta del host.
//
// Confirmación: un fichero que otro proceso está escribiendo da un error pasajero, así que solo
// cuenta como confirmado si sigue ilegible y además (a) este proceso lo vio ilegible por primera vez
// hace al menos ConfirmAfter, o (b) el error es de contenido (JSON inválido) y el fichero no se
// modifica desde hace al menos ConfirmAfter: AtomicJsonFile escribe en un temporal y lo renombra, así
// que uno a medio escribir tiene la fecha recién puesta. Los errores de E/S o de permisos (un bloqueo)
// solo se confirman por (a), y una fecha de modificación en el futuro no confirma por (b).
public sealed class UnreadableFileRegistry
{
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, UnreadableFile>> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    public UnreadableFileRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    // El conjunto actual de ficheros ilegibles de una carpeta tras listarla: los que no vienen (se
    // leen bien o ya no están) salen del registro.
    public void Report(string folder, IEnumerable<(string Path, Exception Error)> unreadable)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var previous = _byFolder.GetValueOrDefault(folder);
            var current = new Dictionary<string, UnreadableFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, error) in unreadable)
            {
                var relative = $"{folder}/{Path.GetFileName(path)}";
                var seen = previous?.GetValueOrDefault(relative);
                var firstSeenAt = seen?.FirstSeenAt ?? now;
                var confirmed = seen?.Confirmed == true
                    || now - firstSeenAt >= ConfirmAfter
                    || (error is JsonException && UntouchedFor(path, now));
                current[relative] = new UnreadableFile(folder, relative, error.GetType().Name, firstSeenAt, confirmed);
            }

            _byFolder[folder] = current;
        }
    }

    public IReadOnlyList<UnreadableFile> Confirmed()
    {
        lock (_lock)
        {
            return _byFolder.Values.SelectMany(files => files.Values)
                .Where(f => f.Confirmed)
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static bool UntouchedFor(string path, DateTimeOffset now)
    {
        try
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return modified <= now && now - modified >= ConfirmAfter;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record UnreadableFile(string Folder, string RelativePath, string Error, DateTimeOffset FirstSeenAt, bool Confirmed)
{
    // Un pipeline o una conexión que no se lee deja de ejecutarse; un job ilegible solo falta en el
    // historial.
    public bool AffectsScheduling => !string.Equals(Folder, "jobs", StringComparison.OrdinalIgnoreCase);
}
