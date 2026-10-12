# Catálogo UAT — Sluice Portal

Pruebas de aceptación **por interfaz**: cada escenario se ejecuta como lo haría un usuario (clic,
escritura, menús, diálogos) y comprueba **lo que se ve en pantalla**. La API no se usa ni para
preparar ni para verificar; la única preparación fuera de la UI es borrar filas en SQL para simular
un cambio externo en el destino (CAL-01 a CAL-03).

- **Origen:** carpetas locales con la estructura de Synapse Link (tipo de conexión `Local (UAT)`),
  generadas por la suite. No se toca ningún storage account.
- **Destino:** un SQL Server de pruebas; la suite crea una base `SluiceUat_<id>` por ejecución y la
  borra al terminar.
- **Ejecución:** bajo demanda (`pwsh scripts/uat.ps1` o el workflow `UAT` en GitHub). No bloquea PRs.

Estado: **Auto** = automatizado en `Sluice.Portal.UatTests`; **Manual** = se revisa a mano (no
automatizable con fiabilidad o fuera del alcance del origen simulado).

Cada test lleva el ID en su nombre (`UAT-PIP-03 …`) y el área como `Trait`, así que se puede
filtrar: `pwsh scripts/uat.ps1 -Area Pipelines` o `pwsh scripts/uat.ps1 -Scenario UAT-PIP-03`.

## Cómo se ejecuta

- **En local:** `pwsh scripts/uat.ps1`. Compila, instala Chromium, levanta un SQL Server efímero en
  Docker (contraseña aleatoria, 2 GB como máximo; se borra al terminar) y ejecuta la suite. Con
  `$env:SLUICE_UAT_SQL` definido usa ese SQL en lugar del efímero. `-NoSql` ejecuta solo los
  escenarios que no lo necesitan; `-Headed` muestra el navegador.
- **En GitHub:** Actions → **UAT** → *Run workflow* (opcionalmente con un área o un escenario).
- **Resultado:** `TestResults/uat/uat-report.html`. Por cada escenario que falla queda
  `TestResults/uat/<ID>/` con la traza (`npx playwright show-trace trace.zip`), una captura, el
  vídeo y el final del log del portal.
- La suite usa la versión de Playwright de su proyecto (1.63): el Chromium de la 1.49 que usan los
  UI tests se cuelga al abrir desplegables largos dentro de un diálogo.

## ACC — Acceso

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-ACC-01 | Entrar con la clave correcta | Abrir `/login`, escribir la clave, Entrar | Se abre el Panel con el menú lateral | Auto |
| UAT-ACC-02 | Clave incorrecta | `/login`, clave errónea, Entrar | Sigue en el login con el aviso "Invalid access key." | Auto |
| UAT-ACC-03 | Página protegida sin sesión | Abrir `/pipelines` sin haber entrado | Redirige al login | Auto |
| UAT-ACC-04 | Cerrar sesión | Con sesión, pulsar el botón de salir | Vuelve al login y `/pipelines` ya no abre | Auto |
| UAT-ACC-05 | Mostrar y ocultar la clave | En el login, escribir la clave y pulsar el ojo dos veces | Se ve la clave y vuelve a ocultarse, sin perder lo escrito | Auto |
| UAT-ACC-06 | Idioma y tema en la pantalla de acceso | Sin sesión, en el login pulsar idioma y luego tema | Cambia el idioma de la página y el tema se alterna (sin sesión no hay circuito de Blazor) | Auto |
| UAT-ACC-05 | Volver tras entrar | Abrir `/sources` sin sesión, entrar | Termina en una página del portal con sesión | Manual |

## SRC — Orígenes

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-SRC-01 | Crear un origen local | Orígenes → Añadir → tipo Local (UAT), carpeta y carpeta de parquet → Guardar | La fila aparece con tipo `local` | Auto |
| UAT-SRC-02 | Probar un origen correcto | Acciones → Probar | Aviso verde con el snapshot sellado | Auto |
| UAT-SRC-03 | Probar un origen con carpeta inexistente | Crear origen con carpeta que no existe → Probar | Aviso rojo "does not exist" | Auto |
| UAT-SRC-04 | El formulario local oculta lo de Azure | Añadir → elegir Local (UAT) | No se ven cuenta ni credenciales; sí carpeta y parquet | Auto |
| UAT-SRC-05 | Editar A y luego B muestra los datos de cada uno | Editar origen A (Cancelar), editar origen B | El diálogo muestra el nombre y carpeta de B | Auto |
| UAT-SRC-06 | Renombrar un origen | Editar → cambiar nombre → Guardar | El nombre nuevo en la tabla | Auto |
| UAT-SRC-07 | No se puede borrar un origen en uso | Borrar el origen de un pipeline | Aviso rojo que nombra el pipeline; la fila sigue | Auto |
| UAT-SRC-08 | Borrar un origen libre | Borrar → confirmar | La fila desaparece | Auto |
| UAT-SRC-09 | Pasar un origen a local no guarda las credenciales de Azure | Origen ADLS con cuenta y secreto → editar → tipo Local → guardar → editar → tipo ADLS | Cuenta y secreto vacíos | Auto |

## DST — Destinos

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-DST-01 | Crear un destino SQL | Destinos → Añadir → host, base, usuario, clave → Guardar | La fila aparece | Auto |
| UAT-DST-02 | Probar un destino correcto | Acciones → Probar | Aviso verde | Auto |
| UAT-DST-03 | Probar un destino inalcanzable | Destino con host inválido → Probar | Aviso rojo | Auto |
| UAT-DST-04 | La clave no se muestra al editar | Editar un destino | El campo clave no trae el valor guardado en claro | Auto |
| UAT-DST-05 | Editar A y luego B | Editar destino A (Cancelar), editar B | El diálogo muestra host y base de B | Auto |
| UAT-DST-06 | No se puede borrar un destino en uso | Borrar el destino de un pipeline | Aviso rojo; la fila sigue | Auto |
| UAT-DST-07 | Detectar la versión desde el diálogo | Nuevo destino con datos correctos y versión 2016 → Probar conexión; guardar; editar, versión 2016 → Probar conexión | La versión pasa sola a la del servidor (2022) con aviso verde; al editar usa la contraseña guardada | Auto |
| UAT-DST-08 | Probar corrige la versión guardada | Destino guardado como 2016 → Acciones → Probar | Aviso "detectado … actualizada" y la columna de versión pasa a 2022 | Auto |

## PIP — Pipelines

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-PIP-01 | Crear un pipeline | Pipelines → Añadir → nombre, origen, destino → Guardar | La fila con su origen y destino | Auto |
| UAT-PIP-02 | Editar A y luego B muestra el origen/destino de cada uno | Editar A (Cancelar), editar B | Los desplegables muestran el origen y destino de cada uno (falla hasta fusionar el PR #31) | Auto |
| UAT-PIP-03 | Guardar sin cambios no reasigna | Editar B → Guardar → reabrir | Sigue con su origen y destino | Auto |
| UAT-PIP-04 | Cambiar el destino | Editar → otro destino → Guardar → reabrir | El desplegable muestra el destino nuevo; la tabla también | Auto |
| UAT-PIP-05 | Renombrar | Editar → nombre → Guardar | Nombre nuevo en la tabla | Auto |
| UAT-PIP-06 | Activar y desactivar el scheduler | Botón del scheduler dos veces | Insignia Activo / Inactivo | Auto |
| UAT-PIP-07 | Confirmar antes de ejecutar | Acciones → Ejecutar → Cancelar en el diálogo | No se lanza ningún job | Auto |
| UAT-PIP-08 | Borrar un pipeline | Acciones → Borrar → confirmar | La fila desaparece | Auto |
| UAT-PIP-09 | Restablecer un pipeline | Acciones → Restablecer → confirmar | Aviso de éxito; en Tablas ya no hay estado | Auto |
| UAT-PIP-10 | Aviso si dos pipelines comparten base de datos | Crear un pipeline con el destino de otro | (Funcionalidad propuesta, no implementada) | Manual |

## JOB — Ejecución de punta a punta (origen local + SQL)

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-JOB-01 | Primera carga completa | Ejecutar un pipeline nuevo | El job termina en OK; las filas SQL = filas de los CSV | Auto |
| UAT-JOB-02 | El lienzo recorre las etapas | Durante y tras el job | Ingesta, SQL y conciliación en verde | Auto |
| UAT-JOB-03 | Dónde se publica el parquet | Abrir el paso "Consolidar y publicar" | Muestra la carpeta del parquet y el enlace a Parquet | Auto |
| UAT-JOB-04 | Incremental con snapshot nuevo usa el delta | Nuevo snapshot sellado → Ejecutar | Job OK; eventos "merged N (delta)"; las tablas suman las filas nuevas | Auto |
| UAT-JOB-05 | Incremental sin datos nuevos | Ejecutar otra vez sin snapshot nuevo | Job OK sin filas nuevas | Auto |
| UAT-JOB-06 | Forzar recarga SQL | Acciones → Forzar → confirmar | Job OK; recarga desde el parquet | Auto |
| UAT-JOB-07 | Reconciliar sin divergencia | Acciones → Reconciliar | Job OK con divergencia 0 % | Auto |
| UAT-JOB-08 | Diff | Acciones → Diff | Job OK con la comparación por entidad | Auto |
| UAT-JOB-09 | Snapshot sin sellar no se procesa | Carpeta de snapshot posterior al changelog → Ejecutar | Las filas de ese snapshot no llegan a SQL | Auto |
| UAT-JOB-10 | Entidad con filas irregulares | CSV con columnas de menos | Job OK y todas las filas cargadas | Auto |
| UAT-JOB-11 | Destino inalcanzable | Pipeline con destino inválido → Ejecutar | Job en Error con la causa visible y botón Reintentar | Auto |
| UAT-JOB-12 | Reintentar tras arreglar el destino | Corregir el destino → Reintentar | El job termina en OK | Auto |
| UAT-JOB-13 | Cancelar un job en curso | Cancelar desde el detalle | Estado Cancelado | Manual |

## DET — Detalle e informes de job

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-DET-01 | Métricas del job | Abrir el detalle de un job OK | Snapshots, filas CSV y filas SQL con cifras; tabla de entidades | Auto |
| UAT-DET-02 | Tabla de entidades con búsqueda | Escribir parte de un nombre | Solo quedan las entidades que coinciden | Auto |
| UAT-DET-03 | Línea de tiempo: solo incidencias | Pulsar "Solo incidencias" en un job OK | Mensaje sin incidencias | Auto |
| UAT-DET-04 | Descargar el informe PDF | Jobs → Acciones → Informe | Se descarga un PDF no vacío | Auto |
| UAT-DET-05 | Descargar el JSON | Jobs → Acciones → JSON | Se descarga un JSON con el id del job | Auto |
| UAT-DET-06 | Filtrar el historial | Jobs → filtro de pipeline y de estado | Solo los jobs que cumplen; la columna Job muestra el id corto en una línea (id completo en el tooltip) | Auto |
| UAT-DET-07 | Filtrar el historial por fecha y exportarlo | Jobs → pipeline + Desde/Hasta → Exportar CSV | La tabla y el CSV traen solo los jobs del pipeline en esas fechas | Auto |

## CAL — Calidad y datos

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-CAL-01 | Divergencia detectada | Carga completa; se borran 2 filas en SQL fuera del portal → Reconciliar | Job Parcial; Conciliación muestra la entidad con 33.33 % | Auto |
| UAT-CAL-02 | Aceptar una divergencia | Conciliación → Aceptar | Insignia "Aceptada" | Auto |
| UAT-CAL-03 | Re-sincronizar una tabla | Conciliación → Re-sincronizar | Job OK; la conciliación vuelve a 0.00 % y el historial muestra la recarga | Auto |
| UAT-CAL-04 | Tablas del pipeline | Tablas → elegir pipeline | Cada entidad con sus filas | Auto |
| UAT-CAL-05 | Pausar la sincronización de una tabla | Tablas → interruptor | Insignia "Pausada" | Auto |
| UAT-CAL-06 | Parquet publicado | Parquet → elegir pipeline; pulsar descargar en un fichero | Un fichero consolidado por entidad; la descarga baja el `.parquet` sin salir de Parquet | Auto |
| UAT-CAL-07 | Del lienzo a Parquet | Detalle del job → "Ver parquet" | Abre Parquet con el pipeline elegido | Auto |
| UAT-CAL-08 | Catálogo de snapshots | Snapshots → elegir pipeline | Snapshot sellado y último procesado | Auto |
| UAT-CAL-09 | Analítica de una tabla | Tablas → icono de analítica | Abre la página de crecimiento sin errores | Auto |
| UAT-CAL-13 | Filtrar Analítica por pipeline | Analítica → elegir un pipeline | "Total de jobs" cuenta solo los de ese pipeline y aparece el aviso de que la cola y los workers son compartidos | Auto |
| UAT-CAL-10 | Re-sincronizar una tabla sin divergencia | Se cambian filas en SQL fuera del portal (mismo número de filas) → Tablas → menú ⋯ → Re-sincronizar | Job OK; la línea de tiempo muestra la recarga completa de la tabla | Auto |
| UAT-CAL-12 | Comparar valores y corrección automática | Conciliación → Comparar valores; cambiar un valor e insertar una fila en SQL; comparar; activar la corrección automática y comparar de nuevo | Primero 0 diferencias; luego 1 distinta y 1 sobrante; la corrección automática lanza sola un re-sync (historial "Automatic") y la comparación final vuelve a 0 | Auto |
| UAT-CAL-11 | Buscar una entidad en Parquet | Parquet → escribir el nombre de una entidad en Buscar; luego un texto que no existe | Solo queda el parquet de esa entidad; con el texto inexistente, aviso "ningún parquet coincide" | Auto |

## NAV — Panel y navegación

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-NAV-01 | Todas las páginas abren | Recorrer cada entrada del menú | Cada página muestra su cabecera, sin error | Auto |
| UAT-NAV-02 | Última ejecución en el Panel | Tras un job, abrir el Panel | La tarjeta "Última ejecución" enlaza al job | Auto |
| UAT-NAV-03 | Probar conexiones desde el Panel | Panel → Probar conexiones | Avisos de resultado | Auto |
| UAT-NAV-04 | Plegar el menú a iconos | Botón de plegar | Se ve el riel de iconos; tras recargar sigue plegado | Auto |
| UAT-NAV-05 | El menú no desaparece | Navegar por varias páginas | El menú lateral sigue visible | Auto |
| UAT-NAV-06 | Job activo en el Panel | Lanzar un job y abrir el Panel | Tarjeta del job activo con porcentaje | Manual |
| UAT-NAV-07 | Panel a 1920×1080 | Abrir el Panel en pantalla grande | El lienzo usa el ancho de la tarjeta | Manual |

## CFG — Ajustes y registro

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-CFG-01 | Guardar ajustes | Ajustes → Guardar | "Settings saved…" | Auto |
| UAT-CFG-02 | Las acciones quedan en el registro | Crear un origen → Registro | Entrada de usuario "source.save" | Auto |
| UAT-CFG-03 | Ayuda | Abrir Ayuda | Se ve la guía de usuario | Auto |
| UAT-CFG-04 | Prueba de alerta sin webhook | Ajustes → Probar alerta | Mensaje con el resultado | Manual |
| UAT-CFG-06 | Descargar el paquete de la Logic App | Ajustes → Alertas por correo → Descargar paquete | Se descarga sluice-alertas-logicapp.zip con la plantilla, el script y la vista previa; la página sigue respondiendo | Auto |
| UAT-CFG-07 | Activar la corrección automática | Ajustes → Corrección automática → Guardar; recargar; Conciliación | El ajuste queda guardado y Conciliación avisa de que está activa; al desactivarla el aviso desaparece | Auto |
| UAT-CFG-05 | Filtrar el registro por fecha y exportarlo | Registro → buscar + Desde/Hasta → Limpiar fechas → Exportar CSV | La entrada aparece o desaparece según las fechas; el CSV la trae con los mismos filtros | Auto |

## VAR — Variantes: móvil, idioma y tema

| ID | Escenario | Pasos | Resultado visible | Estado |
|---|---|---|---|---|
| UAT-VAR-01 | Cambiar a español | Botón de idioma | Menú y títulos en español | Auto |
| UAT-VAR-02 | Flujo en español | Crear un origen con la UI en español | Botones y avisos en español; se guarda | Auto |
| UAT-VAR-03 | Tema oscuro | Botón de tema | La página pasa a oscuro y se mantiene al recargar | Auto |
| UAT-VAR-04 | Menú en móvil | 375 px → hamburguesa → Pipelines | Se abre el menú y navega | Auto |
| UAT-VAR-05 | Editar un pipeline en móvil | 375 px → Pipelines → Editar | El diálogo cabe en pantalla y guarda | Auto |
| UAT-VAR-06 | Sin desbordes horizontales en móvil | 375 px en cada página | La página no tiene scroll horizontal | Auto |
| UAT-VAR-07 | Tabla de jobs en móvil | 375 px → Jobs | Las columnas no parten letras sueltas | Manual |

## Fuera del alcance del origen simulado

La validación con el **ADLS real del cliente** (permisos, latencia, volúmenes grandes, costes de
lectura) sigue siendo manual y la ejecuta el responsable del entorno: la suite nunca se conecta a
un storage account.
