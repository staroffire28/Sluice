using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

// Causa que muestra el aviso del detalle de un job. Antes era el último evento de error, que
// cuando fallan todas las entidades es un resumen genérico ("SQL MERGE completed with entity
// errors"); ahora se prefiere el error conocido (KnownErrors) más concreto.
public static class JobDiagnosis
{
    // Event: el evento del que sale, para pintarlo en el idioma de quien lo ve (EventText).
    public sealed record Diagnosis(string? Code, string Message, JobEvent? Event = null);

    public static Diagnosis? Pick(Job job)
    {
        var errors = job.Events.Where(e => e.Level == EventLevel.Error).Reverse().ToList();

        if (errors.FirstOrDefault(e => KnownErrors.IsKnown(e.Code)) is { } coded)
        {
            return new Diagnosis(coded.Code, coded.Message, coded);
        }

        if (job.Entities.FirstOrDefault(e => KnownErrors.IsKnown(e.Code)) is { } entity)
        {
            return new Diagnosis(entity.Code, entity.Message ?? entity.Entity);
        }

        // Jobs guardados antes de existir el código: se reconoce por el texto si se puede.
        foreach (var error in errors)
        {
            if (KnownErrors.CodeFromText(error.Message) is { } code)
            {
                return new Diagnosis(code, error.Message, error);
            }
        }

        if (errors.FirstOrDefault() is { } lastError)
        {
            return new Diagnosis(null, lastError.Message, lastError);
        }

        return job.Events.LastOrDefault(e => e.Level == EventLevel.Warning) is { } warning
            ? new Diagnosis(null, warning.Message, warning)
            : null;
    }
}
