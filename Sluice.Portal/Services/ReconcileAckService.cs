using System.Globalization;
using Sluice.Portal.Models;
using Sluice.Portal.Services.Logging;
using Sluice.Portal.Services.Stores;

namespace Sluice.Portal.Services;

// Aceptar una divergencia de conciliación (o quitar la aceptación) con auditoría: aceptar una
// divergencia hace que deje de verse como problema, así que queda quién lo decidió.
public sealed class ReconcileAckService(ReportStore reports, IAuditLog audit)
{
    public void Accept(AuditActor actor, string pipelineId, string entity, double divergencePct) =>
        audit.Audit(
            actor,
            "reconcile.accept",
            pipelineId,
            () =>
            {
                reports.SaveAck(pipelineId, entity, new ReconAck { AckPct = divergencePct });
                return true;
            },
            _ => new AuditResult($"Divergencia de {entity} aceptada ({divergencePct.ToString("F2", CultureInfo.InvariantCulture)}%).",
                MessageKey: "Activity.Divergence.Accepted", MessageArgs: [entity, divergencePct]),
            $"No se pudo aceptar la divergencia de {entity}",
            failureKey: "Activity.Failed.DivergenceAccept", failureArgs: [entity],
            target: entity);

    public void Remove(AuditActor actor, string pipelineId, string entity) =>
        audit.Audit(
            actor,
            "reconcile.unaccept",
            pipelineId,
            () =>
            {
                reports.RemoveAck(pipelineId, entity);
                return true;
            },
            _ => new AuditResult($"Se quitó la aceptación de la divergencia de {entity}.",
                MessageKey: "Activity.Divergence.Unaccepted", MessageArgs: [entity]),
            $"No se pudo quitar la aceptación de {entity}",
            failureKey: "Activity.Failed.DivergenceUnaccept", failureArgs: [entity],
            target: entity);
}
