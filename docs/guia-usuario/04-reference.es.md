# Referencia

## Glosario

- **Pipeline** — un origen (ADLS) más un destino (SQL), con un nombre. Su interruptor de "scheduler" controla solo las corridas automáticas; nunca bloquea una corrida manual. Su ajuste *Filas borradas en F&O* (`DeleteMode`) las borra en SQL (por defecto) o las carga marcadas con `IsDelete = 1`.
- **Job (corrida)** — una ejecución de un pipeline, con un tipo, un estado, resultados por tabla y una línea de tiempo.
- **Tipo de job** — **Incremental** (solo snapshots nuevos, el predeterminado), **Force** (recarga SQL desde parquet consolidado — todas las tablas, sin ingest), **Reconcile** (solo verifica conteos), **Diff** (reporta deltas de filas), **Re-sync tabla** (re-sincroniza una o varias tablas desde el Parquet existente; desde Conciliación, Tablas o la corrección automática), **Comparar valores** (compara fila a fila, por Id, el Parquet con SQL; desde Conciliación).
- **Snapshot** — una carpeta de exportación de Synapse Link en ADLS (una foto de los datos en un punto en el tiempo).
- **Changelog** — un archivo que Synapse escribe nombrando la carpeta que está escribiendo *en ese momento*. El portal solo consume carpetas estrictamente más antiguas que esa, así que nunca lee datos a medio escribir.
- **Snapshot asentado (settled)** — la última carpeta totalmente cerrada (la inmediatamente anterior a la del changelog). El último snapshot seguro de consumir.
- **Marca de agua (watermark)** — una marca de tiempo que indica hasta dónde llegó el procesamiento; se usa para deduplicar (gana la marca de agua más alta para la misma fila).
- **Tarea / cola** — la unidad atómica de trabajo de una etapa de un job. Los estados son carpetas: `pending`, `claimed` (retenida con un lease), `done`, `error`.
- **Lease / heartbeat** — el lease es la retención temporal de una tarea reclamada (por defecto 120 s); el heartbeat lo renueva. Si un worker muere, el lease expira y la tarea vuelve a `pending`.
- **Conciliación (Reconcile)** — la verificación de que los conteos de filas de SQL coinciden con el Parquet consolidado.
- **Diff** — un reporte informativo del delta de filas (origen vs destino); no marca un job como Partial.
- **Divergencia / umbral** — `|source − dest| / max(source, dest) × 100`. Por encima del umbral (`ReconDivergencePct`, por defecto 1.0%), el job se marca Partial.
- **Remediación (resync por tabla)** — corregir una tabla divergente re-mergeando el Parquet existente. **Re-sincronizar** = upsert + eliminar huérfanos (en ambas direcciones); **Re-mergear** = solo upsert (cuando a SQL le faltan filas).
- **Aceptación de divergencia (recon-ack)** — declarar una divergencia como legítima. Suprime la alerta mientras la divergencia se mantenga cerca del valor aceptado; si se sale de rango, la aceptación expira y la alerta se reanuda.
- **Conexión (Origen / Destino)** — las credenciales y la configuración de un origen ADLS o un destino SQL. Los secretos se cifran y se muestran como `***`.
- **Worker / etapa** — un worker es el proceso que reclama y ejecuta tareas; una etapa es una de Ingest, SQL, Reconcile. En modo distribuido un worker puede limitarse a una sola etapa.
- **Orquestador** — el nodo que programa y purga. En modo distribuido solo él lo hace; los workers solo procesan su cola.

## Estados de job

| Estado | Significado |
|---|---|
| Running | En progreso (creándose o procesando etapas). |
| OK | Terminó limpio, sin divergencia por encima del umbral. |
| Partial | Una tabla no aceptada diverge por encima del umbral (revisa Conciliación), o algunas tablas fallaron en la ingesta o en SQL, o algunos valores del CSV no se pudieron leer con el tipo de su columna y se cargaron como NULL (la tabla y un aviso en la línea de tiempo indican la columna y un ejemplo). Tras una ingesta parcial el job sigue con SQL y Conciliación: mientras no termine cuenta como en curso (se puede cancelar, no reintentar). |
| Error | Una etapa lanzó un error (p. ej. SQL inalcanzable) — revisa la línea de tiempo. |
| Stalled (Blocked) | El origen aún no tenía snapshots seguros — no es culpa tuya, reintentará. |
| Cancelled | Alguien lo canceló; sus tareas pendientes se purgaron. |

## Estados de tarea

| Estado | Significado |
|---|---|
| Pending | En cola, esperando ser reclamada. |
| Claimed | Retenida por un worker con un lease activo (en ejecución). |
| Done | Completada con éxito. |
| Error | Falló; el archivo conserva el mensaje de error. |

## Seguridad

La documentación completa de autenticación (tutorial, guía práctica, referencia y explicación) está en `docs/auth/` del repositorio.

- **`UI_API_KEY`** protege la interfaz y la API de gestión (`/api/*`). Inicia sesión en `/login` (clave compartida + cookie `sluice_session`). Fuera de Development, el portal se niega a arrancar sin ella, salvo con `AUTH_MODE=none` (acceso abierto a propósito: la landing ofrece **Entrar** sin clave y la cabecera muestra **Acceso abierto**).
- Los workers no se autentican contra el portal: no hay API entre ellos, comparten la cola de tareas en el volumen de `DATA_DIR`.
- **Clave placeholder** (`changeme-ui`) se rechaza fuera de Development — define un valor real en `.env` antes de desplegar.
- **Login/logout por formulario** requieren token antiforgery; el `POST /auth/login` JSON (scripts/automatización) queda exento.
- **Data Protection** cifra los secretos de conexión en reposo; el keyring vive en `DATA_DIR/keys` en el volumen compartido.

## Observabilidad y alertas

- **Salud**: `GET /health` → `{ "status": "ok" }`.
- **Registro de actividad**: persistido por proceso bajo `DATA_DIR/logs`, con eventos `user` y `system` (`pipeline.run` firmado por `scheduler`, `retention.sweep`, …). Cada entrada guarda la máquina que la escribió (Host) y, en las acciones auditadas, quién actuó: el canal (`ui`, `api`, `scheduler`, `queue`, `autofix`, `retention`, `worker`, `demo`) y el cliente (IP y `s:` + un hash corto de la sesión, nunca el token). La columna **Actor** muestra canal y cliente (sin canal, la máquina; la máquina va en su tooltip). Filtrable en `/logs` (la búsqueda de texto también busca en canal, cliente, máquina y objetivo; también por fechas Desde/Hasta, días completos en la hora del servidor) y exportable a CSV (UTF-8, separador coma, con las columnas Actor y Host).
- **Alertas** se disparan ante: un **fallo de job de pipeline** (`Error`), una conciliación Partial, diferencias en **Comparar valores**, un error del scheduler, un estancamiento del changelog (deduplicado), un fallo del barrido de retención, un cambio de tipo de columna en el origen (Ingest), un posible bloqueo en SQL (ver abajo), una cola parada en modo distribuido (ver `system.alert` abajo), un snapshot cerrado pero incompleto que frena el pipeline (ver `snapshot.incomplete` abajo), un restore a medias que retiene un pipeline con scheduler (una vez por episodio) y un **pipeline atrasado**: en un pipeline con scheduler, un snapshot cerrado lleva más de *Horas de atraso para alertar* (Ajustes, 6 por defecto; 0 la desactiva) sin procesarse. Se mide desde que el snapshot se cerró, no cuenta la carga inicial ni se evalúa con un job en curso, y avisa una vez por episodio. Configúralas en `/settings` (activar + URL del disparador HTTP de la Logic App). Este webhook es el único canal de alertas del portal (no hay SMTP); para recibirlas por correo, **Ajustes → Alertas por correo (Logic App)** descarga un paquete listo para desplegar en Azure: una Logic App que envía cada alerta como correo de Office 365 con el diseño de Sluice (plantilla ARM, `deploy.ps1`, guía y vista previa del correo).
- **Entrega al webhook**: un HTTP 5xx o un timeout se reintenta una vez (un 4xx o un error de red/DNS no). Las alertas deduplicadas (estancamiento del changelog, pipeline atrasado, cola parada, snapshot incompleto, restore a medias) solo cuentan como enviadas cuando el webhook las acepta; hasta entonces el scheduler las reintenta en cada poll (tras la primera entrega fallida de un poll, las demás esperan al siguiente). El resultado de la última entrega real (no la de **Enviar prueba**) lo comparten la api y los workers en `DATA_DIR/alerts/status.json` y se muestra en **Ajustes → Alertas → Última entrega**; el registro de actividad anota `alert.delivery.failed` y `alert.delivery.recovered` solo cuando la entrega empieza a fallar o se recupera.
- **Payloads JSON del webhook** (POST, `Content-Type: application/json`):
  - `test.ping` — enviado por **Enviar prueba** en Ajustes (la URL debe ser `https://`): `{ "eventType": "test.ping", "timestamp": "...", "message": "Sluice portal alert test" }`
  - `pipeline.failure` — job falló en una etapa: ingesta o SQL terminadas en Error o Partial, o un job huérfano porque un worker cayó entre etapas: `{ "eventType": "pipeline.failure", "pipelineId", "pipelineName", "jobId", "jobType", "stage", "status": "error" | "warning", "message", "timestamp" }` (`warning` cuando la etapa termina en Partial: parte de las tablas sí cargó)
  - `reconcile.divergence` / `reconcile.values` — conciliación Partial o diferencias de valores: `{ "eventType", "pipelineId", "pipelineName", "jobId", "status": "warning", "message", "text", "maxDivergencePct" (solo divergencia), "entities": [tablas], "autoRemediation": "off" | "scheduled" | "unresolved" | "none", "timestamp" }`
  - `system.alert` — scheduler/retención/changelog/cambio de tipo en el origen/bloqueo SQL/pipeline atrasado/restore a medias/cola parada (modo distribuido: tareas de una etapa pendientes más de 15 min sin ningún worker vivo de esa etapa): `{ "eventType": "system.alert", "text", "message", "timestamp" }`
  - `snapshot.incomplete` — el snapshot pendiente más antiguo está cerrado pero su volcado está incompleto, así que el pipeline no avanza (el scheduler no lanza jobs hasta que se complete). Se envía a los 45 min y de nuevo cada 24 h mientras siga igual: `{ "eventType": "snapshot.incomplete", "pipelineId", "pipelineName", "snapshot", "status": "warning", "message", "text", "timestamp" }`
- **Posible bloqueo en SQL**: si una sentencia SQL no avanza durante 1 hora, la línea de tiempo del job recibe un Warning con la entidad y la fase, y se envía un `system.alert`. La sentencia **no** se cancela y el estado del job no cambia; revisa las sesiones bloqueantes en la base de destino. El umbral de 1 hora está fijado en el código.
- **Dead-man ping**: si `DEADMAN_PING_URL` está configurada, el scheduler le hace un GET (timeout de 10 s) al final de cada ciclo que termina sin errores de sus vigilantes y en el que al menos un pipeline habilitado evaluado termina bien (o no se evalúa ninguno); un HTTP de error cuenta como fallo. El resultado del último ping se ve en Ajustes → Alertas.

## Retención

### Volumen local (`DATA_DIR`)

Un janitor limita el crecimiento del volumen. El **job es la unidad de retención**: un job viejo se purga junto con su directorio de corrida, reportes, tareas terminadas y staging.

- **Siempre se conservan**: los últimos 50 jobs por pipeline (aunque sean viejos), los jobs en ejecución, las aceptaciones de divergencia y el estado del pipeline.
- **Perilla**: `RetentionDays` en `/settings` (por defecto 90). `0` o negativo desactiva la purga.
- **Jobs fallidos o cancelados**: el staging del job se borra cuando el ingest falla o se cancela; lo que quede se purga 24 h después de que el job termine, aunque siga dentro de la retención e incluso con la purga desactivada.
- **Registro de actividad**: los `logs/activity-*.jsonl` (incluidos los `.1` rotados) se purgan cuando su última escritura supera `RetentionDays`, también los de réplicas que ya no existen. El archivo activo del proceso que barre no se borra nunca.
- **Cadencia**: cada 6 horas, solo en el orquestador; registrado como `retention.sweep`. Los archivos corruptos o bloqueados se omiten sin romper el barrido.

### Snapshots origen (container Synapse Link en ADLS)

Un segundo janitor purga carpetas CSV **ya procesadas** en el container origen de Synapse Link. El parquet consolidado (en su container dedicado o en el disco del portal) es el artefacto de recovery.

- **Siempre se conservan**: las últimas 14 snapshots procesadas (piso K), snapshots pendientes o en export, `Changelog/` y metadatos CDM.
- **Perilla**: `SourceSnapshotRetentionDays` en `/settings` (por defecto 30). `0` o negativo desactiva la purga.
- **Cadencia**: cada 6 horas, solo en el orquestador; registrado como `snapshot-retention.sweep`.
- **Recovery tras purga**: usa **Force** (o `POST /api/pipelines/{id}/reload-from-parquet`, mismo camino) para recargar SQL desde parquet consolidado. Incremental sigue leyendo CSV nuevos en ADLS.
- **Dry-run**: `SourceSnapshotRetentionDryRun` en `/settings` — **activo por defecto**; simula sin borrar. Revisa `/snapshots` (columna Retención) y activity log `snapshot-retention.dry-run` antes de desactivarlo.
- **Certificación parquet**: `SourceSnapshotRetentionRequireParquet` (default activo) — no purga si falta algún `{pipeline}/{entity}.parquet`.
- **Soft-delete**: habilita blob soft-delete en la storage account de Azure para recuperar carpetas borradas por error (configuración fuera del portal).

### Versiones del Parquet consolidado

Antes de sustituir el consolidado de una tabla, el anterior se mueve (rename, sin copiar datos) a `_versions/{pipeline}/{tabla}/` junto a un `.json` con su schema, el estado de la tabla y el último snapshot procesado del pipeline. El índice `_versions/{pipeline}.index.json` guarda esos datos sin el schema para no leer cada `.json` en cada ejecución; si se borra o se corrompe, se reconstruye solo. Un **punto de restauración** es ese snapshot: al restaurarlo, cada tabla toma la primera versión archivada después de procesarlo (entre las de un mismo snapshot, la más antigua); si no tiene esa versión, o esa versión ya trae datos posteriores, y su estado actual tiene datos posteriores, la tabla aún no existía y se archiva; si no, ya está en ese punto y se queda como está.

- **Perilla**: `ParquetVersionDays` en `/settings` (por defecto 7). Se conservan los puntos de los snapshots de esos días (por la fecha del snapshot) y siempre el más reciente, con las versiones que necesitan; solo esos se ofrecen en la página Parquet. `0` o negativo las desactiva (la publicación sigue siendo validada y atómica). Si `settings.json` no se puede leer, no se poda nada y el job lleva un Warning, para que los 7 días por defecto no borren versiones que el ajuste guardado conserva.
- **Espacio**: cada versión es una copia completa de la tabla; una tabla que cambia en cada snapshot cada 12 h acumula hasta unas 14 versiones con 7 días. La poda corre al final de cada ingesta, así que durante un job conviven el consolidado nuevo y el anterior aunque las versiones estén desactivadas.
- **Protección**: si falta el consolidado de una tabla pero tiene versiones (publicación interrumpida o borrado a mano), la ingesta se para con un error en vez de reconstruirlo solo con los snapshots nuevos; restaura un punto en la página Parquet (*Puntos de restauración*).

## Errores frecuentes

Cuando un job o una prueba de conexión falla con un error conocido, el portal muestra qué pasó y qué hacer, con el mensaje original debajo. Los errores se reconocen por su código (número de error de SQL Server, estado HTTP de ADLS, código `AADSTS` de Entra ID), así que se reconocen sea cual sea el idioma del servidor.

| Error | Causa habitual | Qué hacer |
|---|---|---|
| No se pudo iniciar sesión en SQL Server (18456) | Usuario o contraseña del destino incorrectos, o login deshabilitado. | Corrígelos en Destinos y pulsa Probar conexión. |
| No existe la base de datos de destino (4060) | La base no existe o el usuario no tiene acceso. | Créala (Sluice no la crea) o corrige su nombre, y da acceso. |
| No se puede llegar al servidor SQL | El host no se resuelve, el puerto está cerrado o un firewall lo bloquea. | Revisa host y puerto (1433 por defecto) y que se permitan conexiones TCP. |
| SQL Server tardó demasiado en responder | Red lenta, o servidor saturado o bloqueado. | Revisa la carga y los bloqueos, y usa Reintentar. |
| Interbloqueo en SQL Server (1205) | Otra sesión bloqueó las mismas filas. | Reintentar; si se repite, revisa quién escribe a la vez. |
| El log de transacciones del destino está lleno (9002) | No queda espacio en el log. | Amplía el log o el disco, o haz copia del log, y usa Reintentar. |
| Id duplicado en la tabla de destino (2627/2601) | La tabla ya tiene una fila con ese Id. | Re-sincroniza la tabla desde Tablas (Reload). |
| Un valor no cabe en la columna de destino (8152/2628) | Se cambió a mano el tipo de una columna. | Restaura el tipo (o borra la tabla) y ejecuta Forzar. |
| El MERGE recibió el mismo Id dos veces (8672) | El Parquet consolidado tiene Ids duplicados. | Restablece el pipeline y ejecútalo. |
| Sin permiso en el almacenamiento ADLS (403) | A la identidad le falta un rol sobre un contenedor. | Asígnale **Storage Blob Data Contributor** sobre los contenedores de Synapse Link y de Parquet. |
| No se encuentra el contenedor o la ruta en ADLS (404) | Nombre de cuenta o de contenedor incorrecto. | Revísalos en Orígenes. |
| Las credenciales de Azure no son válidas (`AADSTS…`) | Secreto caducado o incorrecto, o tenant/client id erróneos. | Genera un secreto nuevo y actualízalo en Orígenes. |
| No se encuentra la cuenta de almacenamiento | El nombre de la cuenta no se resuelve. | Revisa el nombre y la salida hacia `*.dfs.core.windows.net`. |
