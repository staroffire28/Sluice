using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sluice.Portal.Services.Demo;

// Datos sintéticos de Dynamics 365 F&O con la forma de Synapse Link para el modo demo: changelog,
// model.json, carpetas de snapshot con CSV sin cabecera y optionsets. El changelog nombra la
// carpeta en curso y solo las anteriores se procesan (SnapshotSelector), así que cerrar un
// snapshot es escribir sus filas en la carpeta en curso y apuntar el changelog a una nueva.
public sealed class DemoDataGenerator
{
    public const string Container = "synapse";
    public const string ParquetContainer = "parquet";

    private const string CdmFolder = "Microsoft.Athena.TrickleFeedService";
    private const string StateFile = "_sluice-demo-state.json";

    private static readonly string[] Companies =
    [
        "Contoso", "Fabrikam", "Adventure Works", "Northwind Traders", "Tailspin Toys",
        "Litware", "Proseware", "Woodgrove", "Alpine Ski House", "Coho Winery",
    ];

    private static readonly string[] LedgerAccounts = ["110110", "130100", "140200", "200110", "401100", "600150", "605200"];

    // Columnas de sistema de Synapse Link para F&O, delante de las de negocio.
    private static readonly (string Name, string Type)[] SystemColumns =
    [
        ("Id", "guid"), ("SinkModifiedOn", "dateTime"), ("versionnumber", "int64"), ("IsDelete", "boolean"),
    ];

    private static readonly Entity[] Entities =
    [
        new("custtable", Initial: 120, NewPerSnapshot: 4, UpdatesPerSnapshot: 3, DeletesPerSnapshot: 1,
            [("accountnum", "string"), ("name", "string"), ("custgroup", "string"), ("currency", "string"),
             ("dataareaid", "string"), ("blocked", "int64"), ("creditmax", "decimal")],
            (index, rng) =>
            [
                $"C{index + 1:D5}", $"{Companies[index % Companies.Length]} {index / Companies.Length + 1}",
                index % 3 == 0 ? "30" : "10", index % 4 == 0 ? "EUR" : "USD", index % 2 == 0 ? "usmf" : "demf",
                rng.Next(10) == 0 ? "1" : "0", Decimal(rng.Next(1, 200) * 500m),
            ]),
        new("vendtable", Initial: 40, NewPerSnapshot: 1, UpdatesPerSnapshot: 1, DeletesPerSnapshot: 0,
            [("accountnum", "string"), ("name", "string"), ("vendgroup", "string"), ("dataareaid", "string"), ("blocked", "int64")],
            (index, rng) =>
            [
                $"V{index + 1:D5}", $"{Companies[(index + 5) % Companies.Length]} Supplies {index + 1}",
                index % 2 == 0 ? "10" : "20", index % 2 == 0 ? "usmf" : "demf", rng.Next(8) == 0 ? "2" : "0",
            ]),
        new("generaljournalaccountentry", Initial: 600, NewPerSnapshot: 40, UpdatesPerSnapshot: 0, DeletesPerSnapshot: 0,
            [("ledgeraccount", "string"), ("transactioncurrencyamount", "decimal"), ("transactioncurrencycode", "string"),
             ("postingtype", "int64"), ("iscredit", "boolean"), ("dataareaid", "string")],
            (index, rng) =>
            {
                var credit = rng.Next(2) == 0;
                var amount = Math.Round((decimal)rng.NextDouble() * 10_000m, 2);
                return
                [
                    LedgerAccounts[rng.Next(LedgerAccounts.Length)], Decimal(credit ? -amount : amount),
                    index % 4 == 0 ? "EUR" : "USD", rng.Next(1, 6).ToString(CultureInfo.InvariantCulture),
                    credit ? "True" : "False", index % 2 == 0 ? "usmf" : "demf",
                ];
            }),
    ];

    // Optionsets (GlobalOptionsetMetadata.csv): con TRANSLATE_ENUMS se añade [<col>_$label].
    private static readonly (string Entity, string Column, int Option, string Label)[] Optionsets =
    [
        ("custtable", "blocked", 0, "No"), ("custtable", "blocked", 1, "Invoice"), ("custtable", "blocked", 2, "All"),
        ("vendtable", "blocked", 0, "No"), ("vendtable", "blocked", 1, "Invoice"), ("vendtable", "blocked", 2, "All"),
        ("generaljournalaccountentry", "postingtype", 1, "CustBalance"), ("generaljournalaccountentry", "postingtype", 2, "VendBalance"),
        ("generaljournalaccountentry", "postingtype", 3, "LedgerJournal"), ("generaljournalaccountentry", "postingtype", 4, "SalesRevenue"),
        ("generaljournalaccountentry", "postingtype", 5, "PurchConsumption"),
    ];

    private readonly string _root;

    public DemoDataGenerator(string localSourceRoot) => _root = localSourceRoot;

    private string ContainerPath => Path.Combine(_root, Container);

    private string ChangelogPath => Path.Combine(ContainerPath, "Changelog", "changelog.info");

    public bool IsInitialized => File.Exists(ChangelogPath);

    public static IReadOnlyList<string> EntityNames { get; } = [.. Entities.Select(e => e.Name)];

    // Primera carga: modelos, un snapshot cerrado con el volumen inicial y la carpeta en curso.
    public void Initialize(DateTimeOffset now)
    {
        if (IsInitialized)
        {
            return;
        }

        foreach (var entity in Entities)
        {
            WriteModel(entity);
        }

        var state = new DemoState();
        var first = now.AddMinutes(-1);
        WriteRows(FolderName(first), first, state, initial: true);
        OpenFolder(FolderName(now));
        SaveState(state);
    }

    // Cierra el snapshot en curso con altas, cambios y borrados, y abre el siguiente. Devuelve la
    // carpeta cerrada.
    public string Seal(DateTimeOffset now)
    {
        var current = File.ReadAllText(ChangelogPath).Trim();
        var state = LoadState();
        WriteRows(current, now, state, initial: false);

        var next = FolderName(now);
        while (string.CompareOrdinal(next, current) <= 0)
        {
            now = now.AddSeconds(1);
            next = FolderName(now);
        }

        OpenFolder(next);
        SaveState(state);
        return current;
    }

    private void WriteRows(string folder, DateTimeOffset at, DemoState state, bool initial)
    {
        var rng = new Random();
        foreach (var entity in Entities)
        {
            var lines = new List<string>();
            var next = state.Next.GetValueOrDefault(entity.Name);
            if (!state.Deleted.TryGetValue(entity.Name, out var deleted))
            {
                deleted = [];
                state.Deleted[entity.Name] = deleted;
            }

            var live = Enumerable.Range(0, next).Where(i => !deleted.Contains(i)).ToList();
            if (!initial)
            {
                foreach (var index in Pick(live, entity.UpdatesPerSnapshot, rng))
                {
                    lines.Add(Row(entity, index, at, ++state.Version, rng));
                }

                foreach (var index in Pick(live, entity.DeletesPerSnapshot, rng))
                {
                    deleted.Add(index);
                    // Fila de borrado de F&O: solo los campos de sistema, el cuerpo vacío.
                    lines.Add(string.Join(',', [RowId(entity.Name, index).ToString(), Date(at), (++state.Version).ToString(CultureInfo.InvariantCulture), "True",
                        .. entity.Columns.Select(_ => "")]));
                }
            }

            var count = initial ? entity.Initial : entity.NewPerSnapshot;
            for (var i = 0; i < count; i++)
            {
                lines.Add(Row(entity, next++, at, ++state.Version, rng));
            }

            state.Next[entity.Name] = next;
            if (lines.Count > 0)
            {
                var path = Path.Combine(ContainerPath, folder, entity.Name, "1.csv");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, lines);
            }
        }

        var optionsets = Path.Combine(ContainerPath, folder, "OptionsetMetadata", "GlobalOptionsetMetadata.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(optionsets)!);
        File.WriteAllLines(optionsets, Optionsets.Select(o =>
            $"{o.Column},{o.Option},False,1033,{Quote(o.Label)},,{o.Entity},{o.Label}"));
        WriteSnapshotModel(folder);
    }

    // model.json de la raíz del snapshot, como Synapse Link: todas las tablas, y "partitions" (URL
    // de cada CSV) solo en las que traen datos en esta carpeta. El portal valida el snapshot con él.
    private void WriteSnapshotModel(string folder)
    {
        var entities = Entities.Select(entity =>
        {
            var dir = Path.Combine(ContainerPath, folder, entity.Name);
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.csv").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()
                : [];
            return files.Count == 0
                ? (object)new { name = entity.Name }
                : new
                {
                    name = entity.Name,
                    partitions = files.Select(file => new
                    {
                        name = Path.GetFileNameWithoutExtension(file),
                        location = $"https://localhost/{Container}/{folder}/{entity.Name}/{file}",
                    }).ToArray(),
                };
        }).ToArray();
        File.WriteAllText(Path.Combine(ContainerPath, folder, "model.json"), JsonSerializer.Serialize(new { name = "cdm", entities }));
    }

    private static string Row(Entity entity, int index, DateTimeOffset at, long version, Random rng)
    {
        var values = entity.Values(index, rng);
        var business = entity.Columns.Zip(values, (column, value) => column.Type == "string" ? Quote(value) : value);
        return string.Join(',', [RowId(entity.Name, index).ToString(), Date(at), version.ToString(CultureInfo.InvariantCulture), "False", .. business]);
    }

    private static List<int> Pick(List<int> live, int count, Random rng)
    {
        var picked = new List<int>();
        while (picked.Count < count && live.Count > 0)
        {
            var at = rng.Next(live.Count);
            picked.Add(live[at]);
            live.RemoveAt(at);
        }

        return picked;
    }

    private void WriteModel(Entity entity)
    {
        var model = new
        {
            definitions = new[]
            {
                new
                {
                    entityName = entity.Name,
                    hasAttributes = SystemColumns.Concat(entity.Columns).Select(c => new { name = c.Name, dataType = c.Type }).ToArray(),
                },
            },
        };
        var path = Path.Combine(ContainerPath, CdmFolder, $"{entity.Name}-model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(model));
    }

    // La carpeta en curso existe antes de que el changelog la nombre, y el changelog se sustituye
    // de una vez (temporal + rename) para que el portal nunca lea uno a medias.
    private void OpenFolder(string folder)
    {
        Directory.CreateDirectory(Path.Combine(ContainerPath, folder));
        Directory.CreateDirectory(Path.GetDirectoryName(ChangelogPath)!);
        var temp = ChangelogPath + ".tmp";
        File.WriteAllText(temp, folder + "\n");
        File.Move(temp, ChangelogPath, overwrite: true);
    }

    private DemoState LoadState() =>
        JsonSerializer.Deserialize<DemoState>(File.ReadAllText(Path.Combine(_root, StateFile))) ?? new DemoState();

    private void SaveState(DemoState state) =>
        File.WriteAllText(Path.Combine(_root, StateFile), JsonSerializer.Serialize(state));

    private static string FolderName(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH.mm.ss'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Decimal(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    // Id estable por tabla e índice: un cambio o un borrado posterior apunta a la misma fila.
    private static Guid RowId(string entity, int index) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{entity}:{index}")));

    private sealed record Entity(
        string Name,
        int Initial,
        int NewPerSnapshot,
        int UpdatesPerSnapshot,
        int DeletesPerSnapshot,
        (string Name, string Type)[] Columns,
        Func<int, Random, string[]> Values);

    private sealed class DemoState
    {
        public Dictionary<string, int> Next { get; set; } = [];

        public Dictionary<string, List<int>> Deleted { get; set; } = [];

        public long Version { get; set; }
    }
}
