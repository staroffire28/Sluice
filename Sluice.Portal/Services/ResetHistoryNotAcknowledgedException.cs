namespace Sluice.Portal.Services;

// Con la retención de origen borrando de verdad (o settings.json ilegible), el reset reconstruye el
// pipeline solo con los snapshots que quedan en el lago: la historia ya purgada se pierde. Se rechaza
// si no se acusa esa pérdida. Clase propia para que la API la distinga (409) de una petición inválida.
public sealed class ResetHistoryNotAcknowledgedException(string pipelineId)
    : LocalizedException(
        $"El reset de {pipelineId} reconstruiría el pipeline solo con los snapshots que quedan en el lago: " +
        "la retención de origen ya borró los anteriores y su historia, que solo está en el parquet consolidado, se perdería. " +
        "Para resetear igualmente, confírmalo (acknowledgeHistoryLoss=true en la API).",
        "Error.ResetHistoryNotAcknowledged", pipelineId);
