namespace Sluice.Portal.Services.Logging;

// Actor de la UI en este circuito: lo que las páginas pasan a los servicios que auditan.
// HttpContext solo existe en el render estático de App.razor, y el prerender y el circuito son
// scopes de DI distintos: App.razor calcula el cliente y se lo pasa a Routes como parámetro, y
// Routes (que ya corre en el circuito) lo deja aquí.
public sealed class CircuitAuditActor
{
    public AuditActor Current { get; private set; } = AuditActor.Ui();

    public void Set(string? client) => Current = AuditActor.Ui(client);
}
