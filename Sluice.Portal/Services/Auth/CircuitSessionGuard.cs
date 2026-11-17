using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Options;
using Sluice.Portal.Configuration;

namespace Sluice.Portal.Services.Auth;

// Un circuito de Blazor dura lo que su websocket y DualAuthMiddleware solo mira la cookie al
// abrirlo: una pestaña ya conectada seguía operando el portal tras cerrar sesión en otra pestaña o
// dispositivo, o tras caducar la cookie. Aquí cada actividad entrante del circuito (eventos de la
// UI, interop, navegación, confirmaciones de render) vuelve a validar el token con el que se abrió;
// si ya no vale, no se procesa y se lleva al login con recarga completa, que cierra el circuito.
// La cookie no llega al circuito: App.razor la lee en el render estático y Routes la entrega aquí
// (mismo patrón que CircuitAuditActor). Sin clave de UI (AUTH_MODE=none o sin UI_API_KEY) no actúa.
public sealed class CircuitSessionGuard : CircuitHandler
{
    private readonly AppOptions _options;
    private readonly SessionTokenService _sessionTokens;
    private readonly NavigationManager _navigation;
    private bool _captured;
    private string? _token;
    private bool _redirected;

    public CircuitSessionGuard(IOptions<AppOptions> options, SessionTokenService sessionTokens, NavigationManager navigation)
    {
        _options = options.Value;
        _sessionTokens = sessionTokens;
        _navigation = navigation;
    }

    public void Capture(string? token)
    {
        _token = token;
        _captured = true;
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        context => IsSessionValid() ? next(context) : RejectAsync();

    // La primera actividad del circuito (montar Routes) llega antes de que Routes entregue el
    // token: hasta entonces se deja pasar (abrir el websocket ya exigió una sesión válida).
    private bool IsSessionValid() =>
        !_options.IsUiAuthEnabled || !_captured || _sessionTokens.TryValidate(_token);

    // Se bloquea siempre (un cliente puede ignorar la redirección), pero se redirige una sola vez.
    private Task RejectAsync()
    {
        if (!_redirected)
        {
            _redirected = true;
            _navigation.NavigateTo("/login", forceLoad: true);
        }

        return Task.CompletedTask;
    }
}
