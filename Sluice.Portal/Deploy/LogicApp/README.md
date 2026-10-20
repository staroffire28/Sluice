# Alertas de Sluice por correo (Azure Logic App)

Este paquete despliega una **Logic App (plan de consumo)** que recibe el webhook de alertas de
Sluice Portal y las reenvía por **correo de Office 365 Outlook** con una plantilla HTML con la
imagen de Sluice. Abre `email-preview.html` para ver cómo llega cada tipo de aviso.

| Archivo | Para qué |
|---|---|
| `azuredeploy.json` | Plantilla ARM: la Logic App y la conexión de Office 365. |
| `azuredeploy.parameters.json` | Parámetros de ejemplo (destinatarios, URL del portal). |
| `deploy.ps1` | Despliegue con Azure CLI en un paso. |
| `email-preview.html` | Vista previa de los correos con datos de ejemplo. |

## Qué avisa

| Evento (`eventType`) | Cuándo lo envía Sluice | Correo |
|---|---|---|
| `pipeline.failure` | Un job falla en ingesta, SQL o conciliación | **FALLO**, importancia alta |
| `pipeline.failure` con `status` `warning` | La ingesta o SQL terminan en Partial: parte de las tablas sí cargó | **AVISO**, importancia normal |
| `snapshot.incomplete` | El snapshot pendiente más antiguo está cerrado pero incompleto y frena el pipeline | **SNAPSHOT INCOMPLETO**, con el snapshot |
| `reconcile.divergence` | La divergencia de conteos supera el umbral de Ajustes | **DIVERGENCIA**, con las tablas afectadas |
| `reconcile.values` | «Comparar valores» encuentra filas distintas, faltantes o sobrantes | **VALORES**, con las tablas afectadas |
| `test.ping` | Botón «Probar alerta» de Ajustes | **PRUEBA** |
| `snapshot.incomplete` | Un snapshot cerrado pero incompleto frena el pipeline | **ALERTA** |
| `system.alert` | Otros avisos del sistema (bloqueos de SQL, retención) | **ALERTA** |

Las alertas de conciliación dicen además el estado de la **corrección automática** (programada,
desactivada o no resuelta).

## Requisitos

- Una suscripción de Azure y permiso para crear recursos en un grupo de recursos.
- Una cuenta **profesional o educativa** de Office 365 desde la que salen los correos (el conector
  de Office 365 Outlook no admite cuentas @outlook.com).
- Para `deploy.ps1`: [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) con
  sesión iniciada (`az login`).

## Despliegue

### Opción A: script (Azure CLI)

```powershell
./deploy.ps1 -ResourceGroup rg-sluice -Location westeurope `
  -Recipients "operaciones@contoso.com;dba@contoso.com" `
  -PortalUrl "https://sluice.contoso.com"
```

`-Location` crea el grupo de recursos si no existe; sin él, el grupo debe existir. `-PortalUrl`
es opcional: con él el correo usa el logo del portal y trae el botón **Abrir en Sluice** (el portal
debe ser accesible desde donde se lea el correo). Sin él, el correo lleva el logo dibujado en HTML.

### Opción B: Azure Portal

1. **Crear un recurso → Implementación de plantillas (implementar con plantillas personalizadas)
   → Crear su propia plantilla en el editor → Cargar archivo** y elige `azuredeploy.json`.
2. Rellena los parámetros (`recipients` obligatorio) y despliega.
3. En **Salidas** de la implementación está `triggerUrl`.

## Después de desplegar (obligatorio)

1. **Autoriza la conexión de Office 365.** En el grupo de recursos abre la conexión de API
   `sluice-office365` → **Editar conexión de API** → **Autorizar** → inicia sesión con la cuenta
   que enviará los correos → **Guardar**. Hasta entonces la Logic App recibe las alertas pero
   no puede enviar correo.
2. **Copia la URL del disparador** (`triggerUrl`, también en la Logic App → *Diseñador* → disparador
   *When an HTTP request is received* → *HTTP URL*).
3. En **Sluice → Ajustes → Alertas**: activa las alertas, pega la URL, **Guardar** y pulsa
   **Probar alerta**. Debe llegar el correo de **PRUEBA**.

> La URL del disparador lleva una firma (`sig=…`): es un secreto. Quien la tenga puede enviar
> correos a tus destinatarios. Sluice no la muestra en pantalla ni en los registros. Si se filtra,
> regenera las claves de acceso en la Logic App (*Configuración → Claves de acceso*) y pega la URL
> nueva en Ajustes. Ojo: también queda en el historial de implementaciones del grupo de recursos.

## Personalizar

- **Destinatarios o URL del portal:** vuelve a desplegar con otros valores, o cambia los parámetros
  `destinatarios` y `urlPortal` en la Logic App (*Diseñador → Parámetros*).
- **Diseño del correo:** la acción `Html` compone el cuerpo y `Presentacion` decide textos y colores
  por tipo de evento. Si cambias el diseño, la vista previa ya no coincidirá.
- **Coste:** plan de consumo; se paga por ejecución (una por alerta). Sin alertas, no hay coste.
