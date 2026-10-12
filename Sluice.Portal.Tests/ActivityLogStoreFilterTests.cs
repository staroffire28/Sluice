using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;
using Sluice.Portal.Services;
using Sluice.Portal.Services.Logging;

namespace Sluice.Portal.Tests;

// Filtros de la página Logs: fecha (Desde/Hasta, días completos) combinada con origen, nivel y
// búsqueda; Query devuelve todo lo filtrado (la exportación CSV), QueryPage solo una página.
public sealed class ActivityLogStoreFilterTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "sluice-actlog-" + Guid.NewGuid().ToString("N"));
    private readonly SettableTime _time = new();
    private readonly ActivityLogStore _log;

    public ActivityLogStoreFilterTests()
    {
        _log = new ActivityLogStore(Options.Create(new AppOptions { DataDir = _dataDir }), _time);
        Write("2026-10-07T10:00:00Z", "user", "info", "pipeline.run", "corrida del 7");
        Write("2026-10-08T23:59:00Z", "system", "error", "scheduler.run", "fallo del 8");
        Write("2026-10-09T00:01:00Z", "user", "info", "pipeline.run", "corrida del 9");
        Write("2026-10-09T18:00:00Z", "user", "info", "job.report", "reporte del 9");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public void Date_range_keeps_whole_days_inclusive()
    {
        var items = _log.Query(Filter(range: new DateRange(new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 9))));

        Assert.Equal(["reporte del 9", "corrida del 9", "fallo del 8"], items.Select(e => e.Message));
    }

    [Fact]
    public void Date_range_combines_with_source_level_and_search()
    {
        var range = new DateRange(new DateOnly(2026, 10, 7), null);

        Assert.Equal(["fallo del 8"], _log.Query(Filter(level: "error", range: range)).Select(e => e.Message));
        Assert.Equal(["corrida del 9", "corrida del 7"], _log.Query(Filter(search: "pipeline.run", range: range)).Select(e => e.Message));
        Assert.Equal(["reporte del 9", "corrida del 9"], _log.Query(Filter(source: "user", range: new DateRange(new DateOnly(2026, 10, 9), null))).Select(e => e.Message));
    }

    // La exportación es todo lo filtrado, no la página visible.
    [Fact]
    public void Query_returns_everything_while_QueryPage_returns_one_page()
    {
        var all = _log.Query(Filter());
        var page = _log.QueryPage(Filter(), page: 1, pageSize: 2);

        Assert.Equal(4, all.Count);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(4, page.Total);
        Assert.Equal(all.Take(2).Select(e => e.Message), page.Items.Select(e => e.Message));
    }

    private void Write(string at, string source, string level, string action, string message)
    {
        _time.Now = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);
        if (source == "user")
        {
            _log.LogUser(action, message, level: level);
        }
        else
        {
            _log.LogSystem(action, message, level: level);
        }
    }

    // El día del filtro se compara en UTC (la zona que muestra el contenedor).
    private static ActivityLogFilter Filter(string? source = null, string? level = null, string? search = null, DateRange? range = null) =>
        new(source, level, search, range, TimeZoneInfo.Utc);

    private sealed class SettableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
