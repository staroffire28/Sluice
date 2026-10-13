using Sluice.Portal.Models;

namespace Sluice.Portal.Services;

// Causa que muestra el aviso del detalle de un job. Antes era el último evento de error, que
// cuando fallan todas las entidades es un resumen genérico ("SQL MERGE completed with entity
// errors"); ahora se prefiere el error conocido (KnownErrors) más concreto.
public static class JobDiagnosis
{
    public sealed record Diagnosis(string? Code, string Message);

    public static Diagnosis? Pick(Job job)
    {
        var errors = job.Events.Where(e => e.Level == EventLevel.Error).Reverse().ToList();

        if (errors.FirstOrDefault(e => KnownErrors.IsKnown(e.Code)) is { } coded)
        {
            return new Diagnosis(coded.Code, coded.Message);
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
                return new Diagnosis(code, error.Message);
            }
        }

        if (errors.FirstOrDefault() is { } lastError)
        {
            return new Diagnosis(null, lastError.Message);
        }

        return job.Events.LastOrDefault(e => e.Level == EventLevel.Warning) is { } warning
            ? new Diagnosis(null, warning.Message)
            : null;
    }
}
