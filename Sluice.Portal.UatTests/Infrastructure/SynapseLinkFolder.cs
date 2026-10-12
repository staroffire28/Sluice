using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sluice.Portal.UatTests.Infrastructure;

// Filas de una entidad para un snapshot: índices [From, From + Count). Un índice ya publicado se
// reescribe con fecha posterior (actualización); uno nuevo es un alta. Ragged = sin la última columna.
public sealed record EntityRows(string Entity, int From, int Count, bool Ragged = false);

// Simula un container de Synapse Link en una carpeta, con el contrato de
// docs/adls-synapse-link-contract.md: Changelog/changelog.info, esquemas CDM en
// Microsoft.Athena.TrickleFeedService/ y carpetas de snapshot con CSV sin cabecera.
// Cada entidad tiene las columnas Id, SinkModifiedOn, name y amount.
public sealed class SynapseLinkFolder
{
    private const string CdmFolder = "Microsoft.Athena.TrickleFeedService";

    private DateTimeOffset _clock = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    public SynapseLinkFolder(string root, string container)
    {
        Container = container;
        Root = Path.Combine(root, container);
        Directory.CreateDirectory(Root);
    }

    // Carpeta relativa a LOCAL_SOURCE_ROOT: lo que se escribe en el campo "Container" del origen.
    public string Container { get; }

    public string Root { get; }

    public void DefineEntities(params string[] entities)
    {
        foreach (var entity in entities)
        {
            var model = new
            {
                definitions = new[]
                {
                    new
                    {
                        entityName = entity,
                        hasAttributes = new[]
                        {
                            new { name = "Id", dataType = "guid" },
                            new { name = "SinkModifiedOn", dataType = "dateTime" },
                            new { name = "name", dataType = "string" },
                            new { name = "amount", dataType = "double" },
                        },
                    },
                },
            };
            Write($"{CdmFolder}/{entity}-model.json", JsonSerializer.Serialize(model));
        }
    }

    // Escribe un snapshot y lo cierra. Synapse Link nombra en el changelog la carpeta que está
    // escribiendo y solo las anteriores son consumibles: se crea esa carpeta posterior, vacía.
    public string PublishSnapshot(params EntityRows[] rows)
    {
        var snapshot = NextFolder();
        WriteRows(snapshot, rows);
        var inProgress = NextFolder();
        Directory.CreateDirectory(Path.Combine(Root, inProgress));
        Write("Changelog/changelog.info", inProgress + "\n");
        return snapshot;
    }

    // Datos en la carpeta que el changelog marca como "en escritura": aún no deben procesarse.
    public string WriteInProgressRows(params EntityRows[] rows)
    {
        var current = File.ReadAllText(Path.Combine(Root, "Changelog", "changelog.info")).Trim();
        WriteRows(current, rows);
        return current;
    }

    private void WriteRows(string snapshot, IEnumerable<EntityRows> rows)
    {
        var modified = _clock;
        foreach (var batch in rows)
        {
            // Dos particiones por entidad: el ingest debe leerlas en orden y concatenarlas.
            var lines = Enumerable.Range(batch.From, batch.Count).Select(i => Line(batch.Entity, i, modified, batch.Ragged)).ToList();
            var half = lines.Count / 2;
            Write($"{snapshot}/{batch.Entity}/1.csv", string.Join("\n", lines.Take(half)) + (half > 0 ? "\n" : ""));
            Write($"{snapshot}/{batch.Entity}/2.csv", string.Join("\n", lines.Skip(half)) + "\n");
        }
    }

    public static Guid RowId(string entity, int index) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{entity}:{index}")));

    private static string Line(string entity, int index, DateTimeOffset modified, bool ragged)
    {
        var columns = new List<string>
        {
            RowId(entity, index).ToString(),
            modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
            $"\"{entity} {index}\"",
        };
        if (!ragged)
        {
            columns.Add((index * 1.5).ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(",", columns);
    }

    private string NextFolder()
    {
        _clock = _clock.AddHours(1);
        return _clock.UtcDateTime.ToString("yyyy-MM-dd'T'HH.mm.ss'Z'", CultureInfo.InvariantCulture);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
