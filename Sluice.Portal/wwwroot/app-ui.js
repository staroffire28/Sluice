(function () {
    const errorUi = document.getElementById('blazor-error-ui');
    if (errorUi) {
        const dismiss = errorUi.querySelector('.dismiss');
        if (dismiss) {
            dismiss.addEventListener('click', function () {
                errorUi.style.display = 'none';
            });
        }
    }
})();

// Menú lateral expandido/riel (MainLayout): un clic en un botón [data-nav-mode-set] guarda la
// elección en <html data-nav-mode> y en localStorage (App.razor la restaura antes de pintar).
// El CSS decide qué se ve; sin elección guardada manda el ancho de la ventana. Escucha en el
// document para funcionar antes del circuito y aunque Blazor reemplace los botones.
document.addEventListener('click', function (event) {
    const button = event.target.closest('[data-nav-mode-set]');
    if (!button) {
        return;
    }

    const mode = button.getAttribute('data-nav-mode-set');
    document.documentElement.setAttribute('data-nav-mode', mode);
    window.sluiceUi.setItem('sluice-nav-mode', mode);

    // El botón pulsado queda oculto: el foco pasa al que lo sustituye.
    const visible = Array.from(document.querySelectorAll('[data-nav-mode-set]'))
        .find(function (b) { return b.offsetParent !== null; });
    if (visible) {
        visible.focus();
    }
});

// Mostrar/ocultar la clave en la landing (Login.razor). Con clave la página es HTML estático (sin
// sesión no hay circuito de Blazor), así que el botón [data-toggle-key] se atiende aquí.
document.addEventListener('click', function (event) {
    const button = event.target.closest('[data-toggle-key]');
    if (!button) {
        return;
    }

    const input = document.getElementById(button.getAttribute('data-toggle-key'));
    if (!input) {
        return;
    }

    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    const label = button.getAttribute(show ? 'data-label-hide' : 'data-label-show');
    button.setAttribute('aria-label', label);
    button.setAttribute('title', label);
    button.setAttribute('aria-pressed', show ? 'true' : 'false');
});

// Tema en la landing (Login.razor): sin sesión no hay circuito, así que el botón
// [data-theme-toggle] escribe el modo en el mismo ajuste que IThemeService de Fluent
// (localStorage "fluentui-blazor:theme-settings", campo "mode") y recarga: Fluent lo aplica al
// arrancar. Sin modo guardado manda la preferencia del sistema, como en Fluent.
(function () {
    const key = 'fluentui-blazor:theme-settings';

    function stored() {
        try {
            const value = JSON.parse(localStorage.getItem(key) || 'null');
            return value && typeof value === 'object' ? value : {};
        } catch (e) {
            return {};
        }
    }

    function isDark() {
        const mode = stored().mode;
        return mode === 'dark'
            || (mode !== 'light' && window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    }

    function sync() {
        const dark = isDark();
        document.querySelectorAll('[data-theme-toggle]').forEach(function (button) {
            const label = button.getAttribute(dark ? 'data-label-light' : 'data-label-dark');
            button.setAttribute('data-dark', dark ? 'true' : 'false');
            button.setAttribute('aria-label', label);
            button.setAttribute('title', label);
        });
    }

    document.addEventListener('click', function (event) {
        if (!event.target.closest('[data-theme-toggle]')) {
            return;
        }

        const settings = stored();
        settings.mode = isDark() ? 'light' : 'dark';
        try {
            localStorage.setItem(key, JSON.stringify(settings));
        } catch (e) {
            return;
        }

        location.reload();
    });

    // Al arrancar, Blazor puede sustituir el botón prerenderizado (y perder data-dark): se vuelve a
    // aplicar si aparece uno sin estado. Solo se observa en páginas que lo tienen (la landing).
    function start() {
        sync();
        if (!document.querySelector('[data-theme-toggle]')) {
            return;
        }

        new MutationObserver(function () {
            if (document.querySelector('[data-theme-toggle]:not([data-dark])')) {
                sync();
            }
        }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-dark'] });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();

window.sluiceUi = {
    // localStorage genérico (usado hoy para persistir qué grupos del nav lateral
    // quedan expandidos/colapsados, ver NavMenu.razor). Devuelve null si no existe
    // o si localStorage no está disponible (modo privado estricto en algunos navegadores).
    getItem: function (key) {
        try {
            return window.localStorage.getItem(key);
        } catch {
            return null;
        }
    },
    setItem: function (key, value) {
        try {
            window.localStorage.setItem(key, value);
        } catch {
            // localStorage no disponible: la preferencia simplemente no persiste.
        }
    },
    // Zona horaria IANA del navegador (p. ej. "Europe/Madrid"), para mostrar horas locales
    // desde Blazor Server (el servidor no conoce la zona del usuario).
    timeZone: function () {
        try {
            return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
        } catch {
            return null;
        }
    },
    downloadJson: function (filename, json) {
        const blob = new Blob([json], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = filename;
        anchor.click();
        URL.revokeObjectURL(url);
    },
    // Descarga un archivo binario (p. ej. PDF) recibido como base64 desde el servidor.
    downloadFileBase64: function (filename, base64, mime) {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const blob = new Blob([bytes], { type: mime || 'application/octet-stream' });
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = filename;
        anchor.click();
        URL.revokeObjectURL(url);
    },
};
