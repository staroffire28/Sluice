using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services.Stores;

public abstract class JsonFileStore<T>
{
    private readonly AppOptions _options;
    private readonly string _subDirectory;
    private readonly object _lock = new();
    private readonly ILogger _logger;
    // Ficheros ilegibles ya avisados, con la fecha de modificación que tenían: un aviso por versión
    // del fichero, no en cada listado.
    private readonly Dictionary<string, DateTime> _unreadableWarned = new(StringComparer.OrdinalIgnoreCase);
    // Registro compartido de ficheros ilegibles (lo leen el Panel, /health y el scheduler).
    private readonly UnreadableFileRegistry? _unreadable;

    protected JsonFileStore(IOptions<AppOptions> options, string subDirectory, ILogger? logger = null, UnreadableFileRegistry? unreadable = null)
    {
        _options = options.Value;
        _subDirectory = subDirectory;
        _logger = logger ?? NullLogger.Instance;
        _unreadable = unreadable;
        Directory.CreateDirectory(GetDirectory());
    }

    protected string GetDirectory() => Path.Combine(_options.DataDir, _subDirectory);

    // El id es un nombre de archivo: validarlo aquí cierra el path traversal para todas las
    // subclases (conexiones, pipelines, jobs). Las lecturas tratan un id inválido como "no existe".
    protected string GetPath(string id) =>
        EntityIds.IsValid(id)
            ? Path.Combine(GetDirectory(), $"{id}.json")
            : throw new ArgumentException($"Id no válido: '{id}'.", nameof(id));

    public IReadOnlyList<T> ListAll(Func<string, T?> deserialize)
    {
        lock (_lock)
        {
            if (!Directory.Exists(GetDirectory()))
            {
                return [];
            }

            var unreadable = new List<(string Path, Exception Error)>();
            var items = Directory.GetFiles(GetDirectory(), "*.json")
                .Select(f => TryDeserialize(f, deserialize, unreadable))
                .Where(x => x is not null)
                .Cast<T>()
                .ToList();
            ReportUnreadable(unreadable);
            return items;
        }
    }

    // Deja en el registro el conjunto de ficheros de la carpeta que este listado no pudo leer.
    protected void ReportUnreadable(IEnumerable<(string Path, Exception Error)> unreadable) =>
        _unreadable?.Report(_subDirectory, unreadable);

    // Un archivo corrupto (o a medio escribir por otro proceso) se omite en vez de tumbar la
    // lista entera: sin esto, un solo JSON malo dejaba sin lista de jobs, scheduler incluido.
    // Otros errores (p. ej. secretos que no se pueden descifrar) se siguen propagando. El omitido
    // deja un aviso en el log, para que no desaparezca de los listados sin rastro. Un fichero con el
    // literal JSON null se lee sin error y no da nada: cuenta como ilegible, igual que uno corrupto.
    private T? TryDeserialize(string path, Func<string, T?> deserialize, List<(string Path, Exception Error)> unreadable)
    {
        try
        {
            return deserialize(path) ?? throw new JsonException("The file holds the JSON literal null.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            WarnUnreadable(path, ex);
            unreadable.Add((path, ex));
            return default;
        }
    }

    protected void WarnUnreadable(string path, Exception ex)
    {
        var modified = File.GetLastWriteTimeUtc(path);
        lock (_unreadableWarned)
        {
            if (_unreadableWarned.TryGetValue(path, out var warned) && warned == modified)
            {
                return;
            }

            _unreadableWarned[path] = modified;
        }

        _logger.LogWarning(ex, "Fichero ilegible omitido del listado: {Path}", path);
    }

    public T? Get(string id) => ReadLocked(id, Read);

    // Lee el fichero de un id con el lector dado bajo el mismo lock que Save (p. ej. un tipo más
    // ligero que T). Un id no válido o un fichero que no existe dan default.
    protected TRead? ReadLocked<TRead>(string id, Func<string, TRead?> read)
    {
        if (!EntityIds.IsValid(id))
        {
            return default;
        }

        lock (_lock)
        {
            var path = GetPath(id);
            return File.Exists(path) ? read(path) : default;
        }
    }

    public void Save(string id, T item)
    {
        lock (_lock)
        {
            Write(GetPath(id), item);
        }
    }

    // Crea solo si el id no existe; si ya existe lanza DuplicateIdException sin tocar el original.
    public void SaveNew(string id, T item)
    {
        lock (_lock)
        {
            if (!AtomicJsonFile.WriteNew(GetPath(id), item))
            {
                throw new DuplicateIdException(id);
            }
        }
    }

    public bool Delete(string id)
    {
        if (!EntityIds.IsValid(id))
        {
            return false;
        }

        lock (_lock)
        {
            var path = GetPath(id);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    public bool Exists(string id)
    {
        if (!EntityIds.IsValid(id))
        {
            return false;
        }

        lock (_lock)
        {
            return File.Exists(GetPath(id));
        }
    }

    protected T? Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonDefaults.Options);
    }

    // Escritura atómica compartida (temp + rename) — ver AtomicJsonFile.
    protected void Write(string path, T item) => AtomicJsonFile.Write(path, item);
}