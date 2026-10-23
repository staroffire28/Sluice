using System.Globalization;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Guardar los ajustes con auditoría: queda qué claves cambiaron y de qué valor a cuál. El webhook se
// nombra sin su URL (lleva la firma sig= de la Logic App). Desactivar el dry-run de la retención de
// snapshots activa el borrado de carpetas en el lago del cliente: esa entrada sale como warning.
public sealed class SettingsService(SettingsStore store, IAuditLog audit)
{
    public void Save(AuditActor actor, AppSettings settings)
    {
        var before = store.Get();
        audit.Audit(
            actor,
            "settings.save",
            null,
            () =>
            {
                store.Save(settings);
                return Changes(before, settings);
            },
            changes => new AuditResult(
                changes.Count == 0 ? "Ajustes guardados sin cambios." : $"Ajustes guardados: {string.Join("; ", changes)}.",
                Level: before.SourceSnapshotRetentionDryRun && !settings.SourceSnapshotRetentionDryRun ? "warning" : null,
                MessageKey: changes.Count == 0 ? "Activity.Settings.Unchanged" : "Activity.Settings.Saved",
                MessageArgs: [string.Join("; ", changes)]),
            "No se pudieron guardar los ajustes",
            failureKey: "Activity.Failed.Settings");
    }

    // "Clave: antes → después" por cada ajuste distinto, en el orden en que se declaran.
    internal static IReadOnlyList<string> Changes(AppSettings before, AppSettings after) =>
        typeof(AppSettings).GetProperties()
            .Select(p => (p.Name, Before: p.GetValue(before), After: p.GetValue(after)))
            .Where(p => !Equals(p.Before, p.After))
            .Select(p => p.Name == nameof(AppSettings.AlertWebhookUrl)
                ? $"{p.Name}: {(p.After is null ? "quitada" : "cambiada")}"
                : $"{p.Name}: {Format(p.Before)} → {Format(p.After)}")
            .ToList();

    private static string Format(object? value) => value switch
    {
        null => "—",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—",
    };
}
