namespace Sluice.Portal.Services.Stores;

// Create de una entidad cuyo id ya existe. Hereda de InvalidOperationException para que los
// llamadores que ya tratan ese tipo (UI, endpoints con 400) sigan funcionando; los endpoints
// la capturan antes para responder 409.
public sealed class DuplicateIdException : InvalidOperationException
{
    public DuplicateIdException(string id)
        : base($"Ya existe un elemento con el id '{id}'.")
    {
    }
}
