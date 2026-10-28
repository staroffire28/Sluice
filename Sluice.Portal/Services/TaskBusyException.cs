namespace Sluice.Portal.Services;

// El lock de la tarea sigue ocupado por otro proceso y no se ha tocado nada. Es una IOException para
// quien ya las trata así (el bucle del latido la reintenta); el worker la distingue al completar.
public sealed class TaskBusyException(string taskId) : IOException($"La tarea {taskId} está ocupada por otro proceso.");
