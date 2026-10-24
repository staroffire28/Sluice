# Funcionalidad

## Cómo funciona el pipeline

Un **job** (una ejecución de un pipeline) corre en tres etapas:

```
ADLS Gen2
↓ [Ingest]
Parquet consolidado
↓ [SQL]
SQL Server
↓ [Reconcile]
Reporte
```

- **Ingest** — lee lo nuevo del origen y lo escribe en un Parquet limpio. Compara las columnas de cada tabla con la ejecución anterior: las nuevas se añaden en SQL, y las eliminadas o los cambios de tipo se avisan en el job (un cambio de tipo también envía una alerta). Solo procesa snapshots que son seguros (más antiguos que el que se está escribiendo en ese momento), los consolida con el Parquet anterior (conservando la última versión de cada fila) y guarda el resultado como copia duradera (en ADLS o en el disco del portal, según el origen).
- **SQL** — lee ese Parquet y lo hace MERGE (upsert) en SQL Server, creando la tabla si hace falta. Un **Incremental** mergea solo las filas de su propia corrida (el delta) y actualiza una fila solo si su watermark es más nuevo (o igual con un `versionnumber` mayor). Mergea el Parquet consolidado completo cuando la tabla destino está vacía o cuando el último job que cargó SQL no terminó esa etapa. Las filas borradas en F&O llegan como filas con `IsDelete = True` y el resto de columnas vacías; por defecto se borran también en SQL (ver *Crear un pipeline*).
- **Reconcile** — cuenta las filas en el Parquet vs SQL por tabla (las filas borradas en F&O no cuentan cuando el pipeline las borra en SQL). Si una tabla diverge más allá del umbral, el job se marca como **Partial** y se dispara una alerta.

> **Por qué una tabla nueva puede no aparecer todavía (Synapse Link).** El portal solo procesa carpetas de snapshot *anteriores* a la que Synapse marca como escrita en `Changelog/changelog.info`, así que nunca lee exportaciones a medio escribir. Una tabla recién habilitada en Power Platform aparece solo cuando su carpeta se cierra y una corrida la ha procesado. Cada cuánto se cierra una carpeta lo define el intervalo configurado en Synapse Link (por ejemplo, una vez al día).

Un job se dispara **manualmente** (**Ejecutar**, **Forzar**, **Reconciliar** o **Diff** en el menú de ejecución del Dashboard o de Pipelines) o **automáticamente** por el scheduler cuando detecta nuevos snapshots. En cualquier caso, si ya hay un job corriendo para ese pipeline, no arranca un segundo: una ejecución manual puede dejarse **en cola** para cuando termine.

El trabajo fluye por una **cola basada en archivos**: cada tarea es un archivo JSON cuya carpeta (`pending` → `claimed` → `done`/`error`) es su estado. Un worker reclama una tarea con un lease temporal y envía heartbeats; si muere, el lease expira y la tarea vuelve a `pending`, así nada se queda atascado.

## Páginas

| Página | Ruta | Qué haces ahí |
|---|---|---|
| Panel | `/` | Salud, cola, workers; lanzar y cancelar corridas. Mientras ninguna ejecución haya cargado tablas, muestra **Primeros pasos**: conectar origen y destino, crear un pipeline, elegir tablas (opcional) y ejecutarlo, cada paso con su enlace. |
| Jobs | `/jobs` | Historial de corridas (filtros de pipeline, estado y fechas Desde/Hasta); abrir detalle; descargar reporte PDF/JSON; **Exportar CSV** con los filtros aplicados. La columna *Trabajo* muestra el id corto (el sufijo final); el id completo aparece al pasar el ratón, en el detalle y en el CSV. |
| Detalle del job | `/jobs/{id}` | Progreso en vivo: etapas, resultados por tabla, línea de tiempo; descargar el reporte PDF. |
| Tablas | `/tables` | Conteos de filas, watermarks, último dato (resaltado tras los *Días sin datos*, ver Ajustes) y estado de carga por tabla; **Re-sincronizar** cualquier tabla desde su Parquet. |
| Orígenes | `/sources` | Conexiones de origen ADLS (crear, probar, editar, eliminar). |
| Destinos | `/destinations` | Conexiones de destino SQL Server. |
| Pipelines | `/pipelines` | Crear/editar pipelines, programarlos, ejecutarlos. |
| Catálogo de tablas | `/catalog` | Qué tablas y columnas del lago se cargan en SQL (desde Pipelines → **Tablas y columnas**). |
| Snapshots | `/snapshots` | Carpetas de snapshot en ADLS, estado del changelog, backlog. |
| Conciliación | `/reconcile` | Tablas divergentes y su remediación. |
| Parquet | `/parquet` | Archivos Parquet materializados, en ADLS o en el disco del portal (descargar, eliminar, **Puntos de restauración** del pipeline). |
| Analítica | `/analytics` | Indicadores y gráficas de rendimiento, de todos los pipelines o de uno (selector *Pipeline*). La cola y los workers son compartidos y no se filtran. |
| Logs | `/logs` | Registro de actividad (eventos de usuario y de sistema), filtrable por origen, nivel, texto y fechas Desde/Hasta; **Exportar CSV** con todos los registros que cumplen los filtros. |
| Ajustes | `/settings` | Parámetros operativos globales: scheduler, umbral de divergencia, corrección automática, retención, alertas (webhook) y descarga del paquete de la Logic App de correo. |
| Ayuda | `/ayuda` | Esta guía de usuario. |

El menú lateral se puede contraer a un riel de iconos con su botón de contraer; la elección se recuerda en el navegador.

El estado se muestra en todas partes como una insignia de color: **Running/Claimed** (acento), **OK** (verde), **Error** (rojo), **Partial/Stalled** (naranja), **Cancelled** (gris), **Pending** (neutro).

## Tareas comunes

**Crear una conexión.** Orígenes/Destinos → Agregar → completar los campos → Guardar → Probar. Los secretos se guardan cifrados y se muestran como `***`.

**Crear un pipeline.** Pipelines → Agregar → elegir un origen y un destino → Guardar. Arranca con el scheduler apagado; ejecútalo manualmente primero. Con un job en curso no se puede cambiar su origen ni su destino. **Filas borradas en F&O** decide qué pasa con los borrados: *Borrarlas también en SQL* (por defecto) o *Cargarlas marcadas* (`IsDelete = 1`, resto de columnas vacías). El cambio se aplica desde la próxima ejecución; para corregir las filas ya cargadas, ejecuta **Forzar**.

**Elegir tablas y columnas.** Al crear un pipeline, un aviso ofrece **Elegir tablas**; después, Pipelines → menú de la fila → **Tablas y columnas**. El catálogo lista las tablas del lago (los esquemas del último snapshot cerrado y de la raíz, más las tablas ya cargadas) sin lanzar ningún job. El interruptor **Cargar** excluye una tabla: no se ingiere ni se carga (es el mismo ajuste que la sincronización de la tabla en Tablas). Las tablas nuevas que aparezcan en el lago se cargan solas. **Columnas** abre la lista de columnas de la tabla: las desmarcadas no se cargan en SQL, pero el Parquet las conserva completas, así que se pueden volver a incluir; al hacerlo, el portal ofrece re-sincronizar la tabla para rellenarlas. `Id`, `SinkModifiedOn`, `versionnumber` e `IsDelete` son obligatorias. Una columna excluida que ya estaba en la tabla SQL no se borra (no rompe informes que la usen): conserva su último valor, queda vacía en las filas nuevas y el job lo indica. Comparar valores tampoco compara las columnas excluidas. Con `TRANSLATE_ENUMS`, la columna de etiqueta (`_$label`) de una columna excluida se vacía en vez de conservar su último valor.

**Ejecutar un pipeline.** Desde el menú de ejecución del Dashboard o de Pipelines: **Ejecutar** (Incremental: solo datos nuevos, el habitual), **Forzar** (recarga SQL desde el Parquet consolidado — todas las tablas), **Reconciliar** (solo verifica conteos), **Diff** (solo reporta diferencias). Confirma en el diálogo. Si el pipeline tiene un job en curso, el diálogo ofrece **Poner en cola**: la ejecución arranca en la siguiente vuelta del scheduler tras terminar ese job (como mucho el intervalo del scheduler de Ajustes), aunque termine con error o se cancele, y también con el scheduler del pipeline apagado. Si ese job deja tablas divergentes, la corrección automática (si está activada) va primero. Cabe una ejecución en cola por pipeline: mientras espera se ve **En cola** en Pipelines y en el Dashboard, con **Quitar de la cola**; para encolar otra cosa, quita antes la que hay. En cuanto el scheduler empieza a lanzarla ya no se puede quitar (te lo avisa), y la etiqueta desaparece sola al arrancar. El job que arranca lo indica en sus eventos. Borrar o restablecer el pipeline vacía su cola. Por API: `POST /api/pipelines/{id}/run?queue=true` encola si está ocupado y `DELETE /api/pipelines/{id}/queue` la quita (409 si ya está arrancando).

**Seguir / cancelar una corrida.** Abre el detalle del job para ver etapas, tablas y línea de tiempo en vivo; su lienzo muestra también el paso en que se guarda el Parquet consolidado: *Consolidar y publicar en ADLS*, o *Consolidar y guardar en el disco del portal* si el origen guarda el Parquet en el disco del portal. **Cancelar** solo está disponible mientras el job está corriendo. El worker pide a la etapa en curso que se detenga en su siguiente latido (hasta un tercio del lease de la tarea, 40 s por defecto); hasta que la etapa se detiene de verdad, el pipeline rechaza cualquier otra ejecución, re-sync, restauración, reset o borrado, avisando de que el job cancelado aún se está deteniendo.

**Reintentar un job.** Desde el menú del job en Jobs, o **Reintentar** en el detalle del job, para jobs en Error, Partial o Stalled. Crea un job nuevo; el original queda en el historial.

**Programar un pipeline.** Pipelines → Activar scheduler. A partir de ahí, el portal lanza una corrida Incremental por su cuenta cuando llegan nuevos snapshots.

**Restablecer un pipeline.** Pipelines → menú de la fila → **Restablecer**. Borra el registro de snapshots procesados, el estado de cada tabla y los Parquet consolidados del pipeline; no se puede deshacer. Se conservan la configuración, las tablas pausadas y los CSV originales de Synapse Link, y la siguiente corrida reprocesa todos los snapshots desde cero. El diálogo ofrece *Vaciar también las tablas de destino en SQL*: si se desmarca, la siguiente corrida actualiza con MERGE las filas que ya están en SQL.

**Conciliar y remediar.** Conciliación → elegir el pipeline. Para una tabla divergente, en el menú de la fila (**···**): **Re-sincronizar** la recarga desde el Parquet consolidado: actualiza todas sus filas y borra las que ya no están en él, sin vaciarla (corrige filas faltantes o sobrantes en SQL); **Re-mergear** solo agrega/actualiza filas (aparece cuando a SQL le faltan filas). **Aceptar** declara legítima una divergencia, para que deje de alertar (no lanza ningún job). Re-sincronizar y Re-mergear lanzan un job de tipo *Re-sync tabla*, y cualquier tabla también se puede re-sincronizar desde **Tablas**.

**Comparar valores.** La conciliación cuenta filas; no ve un valor cambiado si el número de filas coincide. **Comparar valores** (barra de Conciliación para todo el pipeline, o **Comparar de nuevo** en el menú de una tabla) lanza un job que empareja por Id cada fila del Parquet consolidado con la de SQL y lista las filas **distintas**, las que **faltan** en SQL y las que **sobran** (las filas borradas en F&O se omiten cuando el pipeline las borra en SQL; el tooltip de cada contador muestra hasta 20 Id). Lee las tablas enteras: en tablas grandes tarda y ocupa memoria, por eso se lanza a mano. Las columnas de etiqueta de enum no se comparan (se derivan al cargar).

**Volver a un punto de restauración.** Cada ejecución que cambia una tabla sube su Parquet consolidado nuevo con otro nombre, comprueba que llegó entero y solo entonces sustituye al anterior, que se guarda como versión. Si el nuevo tiene menos filas que el anterior, el job lo avisa y envía una alerta. Las versiones se agrupan en **puntos de restauración**, uno por cada snapshot procesado que una ejecución posterior dejó atrás al cambiar alguna tabla ya cargada: el lago tal como quedó en ese momento. En Parquet → **Puntos de restauración** cada punto muestra cuántas tablas vuelven atrás y qué ejecución lo dejó atrás; **Restaurar** → confirmar devuelve **todo el pipeline** a ese snapshot: cada tabla que cambió después recupera el Parquet que tenía, las que aún no existían se archivan (no se borran) y el pipeline se rebobina a ese snapshot. Se lanza un *Re-sync tabla* que recarga en SQL solo las tablas que volvieron atrás, y la siguiente ejecución recupera las filas posteriores si sus snapshots siguen en el lago. Las copias actuales quedan como versiones, así que el punto más reciente deshace la restauración. Si una restauración falla a medias, las tablas que ya movió quedan pendientes: solo se puede reintentar con **el mismo punto** (el diálogo lo indica, lo ofrece con **Terminar restauración** y bloquea los demás puntos), que primero las termina y también las recarga en SQL. Hasta entonces el pipeline rechaza cualquier ejecución, re-sync, conciliación o restauración de otro punto y pide volver a restaurar ese punto para terminarlo; el scheduler retiene el pipeline y envía una sola alerta. No se restauran tablas sueltas ni se puede restaurar con un job en curso. Se guardan todos los puntos de las últimas 24 h y, más atrás hasta los *Días de versiones del parquet* de Ajustes (7 por defecto), el último de cada día; siempre al menos el más reciente. Las versiones viven en `_versions/` junto al Parquet, el reset del pipeline las borra y no se copian al cambiar dónde se guarda el Parquet.

**Corrección automática.** Con **Ajustes → Corrección automática** activada, al terminar una corrida (o una comparación de valores) con tablas que superan el umbral de divergencia o con diferencias de valores, Sluice lanza solo un re-sync (recarga) de esas tablas, en un único job. Un solo intento: si la diferencia persiste, alerta y no reintenta. Las tablas aceptadas no se corrigen. El historial de Conciliación marca estos re-sync como *Automática*.

**Obtener un reporte.** Botón **Reporte** (PDF) en el detalle del job, o Jobs → **Acciones** → **Reporte** (PDF) o **JSON**, por job. No hay una pantalla de Reportes aparte.

Para saber qué significa cada estado y término, ver [Referencia](04-reference.md).
