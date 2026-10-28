namespace Sluice.Portal.Services;

// El pipeline tiene un restore a medias y no arranca nada hasta que se termine restaurando el mismo
// punto. Es una InvalidOperationException para quien ya las trata así; el scheduler la distingue para
// retener el pipeline y avisar una vez, en vez de alertar como fallo en cada poll.
public sealed class RestorePendingException(string? snapshot)
    : LocalizedException($"Hay un restore a medias en este pipeline: vuelve a restaurar el punto {snapshot} para terminarlo.",
        "Error.RestorePending", snapshot);
