# Diseño — Remediación de conciliación por tabla

Fecha: 2026-06-17
Estado: Implementado (Approach B) — build 0 errores, 95/95 tests
Autor: sesión office-hours

## Desviaciones de implementación frente al diseño

- **Estado de drift por tabla (DRY):** en vez de duplicar la divergencia dentro de
  `TableState`, la UI (`/reconcile` y `/tables`) la lee del último `ReconcileReport`,
  que ya es la fuente de verdad. Solo se añadió estado persistente nuevo para los
  acknowledgments (`reports/recon-ack/{pipelineId}.json`) y el historial
  (`reports/remediation/{id}.json`). `TableState` no se tocó.
- **Acción única (DRY):** la remediación vive solo en `/reconcile`. `/tables` quedó
  de solo lectura con una columna de divergencia (visibilidad), sin botón de re-sync,
  para no duplicar el flujo confirmar+orquestar en dos páginas.
- **`StageRouter` sin cambios:** el job de re-sync arranca encolando la tarea en el
  stage SQL; el router ya encadena SQL → Reconcile → done. Solo cambia el
  `StageStatus` inicial del job (sin `ingest`).

## 1. Problema

Cuando el stage de conciliación detecta diferencias entre el destino SQL y el
origen (parquet consolidado), hoy el sistema **solo señaliza**: marca el job
`Partial`, dispara `AlertService`, y muestra un banner en Pipelines + un KPI en
Analytics. No hay menú dedicado, no hay acción de remediación, no hay estado de
drift por tabla, no hay trazabilidad. El usuario solo puede re-ejecutar el
pipeline entero con **Force**, aunque haya divergido una sola tabla de 40.

Objetivo: **re-sincronizar una tabla concreta** sin reprocesar el pipeline
completo, con visibilidad y trazabilidad del problema y su resolución.

## 2. Cómo funciona la conciliación hoy (base del diseño)

- `ReconcileExecutor.cs:44` compara conteo de filas: fuente = parquet
  consolidado (`entry.FilteredRows` / `ParquetMaterializer.CountRowsAsync`),
  destino = `ISqlMergeExecutor.CountRowsAsync`. Es conteo de filas, no checksum
  ni comparación por columna.
- Métrica: `ReconcileService.ComputeDivergencePct` = `|src-dst|*100/max(src,dst)`
  (`ReconcileService.cs:55`). `IsPartial = maxDiv > ReconDivergencePct`
  (`ReconcileService.cs:34`, umbral editable en Settings).
- Resultado: `ReconcileReport` con `Entities` (`ReconcileEntityResult`:
  `Entity, SourceRows, DestinationRows, DivergencePct`), persistido por
  `ReportStore.SaveReconcile` en `{DataDir}/reports/reconcile/{id}.json`.
- El MERGE a SQL ya es **por-entidad e idempotente** (`SqlExecutor.cs:73-154`):
  itera `manifest.Entities`, lee parquet+schema durables de ADLS
  (`entry.ParquetPath` / `entry.SchemaPath`), mergea por lotes de 5000, recuenta.

### Hallazgo de correctitud que define la remediación

`SqlMergeBuilder.BuildMerge` (`SqlMergeBuilder.cs:52-53`) es **solo upsert**:

```
WHEN MATCHED THEN UPDATE SET ...
WHEN NOT MATCHED THEN INSERT (...) VALUES (...);
```

No hay `WHEN NOT MATCHED BY SOURCE THEN DELETE` (correcto: el merge va por
lotes, un delete-by-source borraría lo que no esté en el lote). Implicación:

| Caso | Significado | ¿Re-merge lo arregla? |
|------|-------------|------------------------|
| `source > dest` | A SQL le faltan filas / desactualizadas | **Sí** ✓ |
| `dest > source` | SQL tiene huérfanas (borradas en origen) | **No** ✗ |

Por eso la acción de remediación robusta es **recarga por entidad**: upsert del
parquet consolidado completo + **borrado de filas huérfanas** (Id que ya no está
en el parquet). Garantiza `SQL == parquet` en ambas direcciones, sigue acotada a
una tabla, y reutiliza el streaming de merge existente. No se vacía la tabla antes
(no hay `TRUNCATE`): el upsert la mantiene poblada y las huérfanas se borran en un
solo `DELETE ... WHERE NOT EXISTS`, así no hay ventana en la que la tabla quede
vacía si el worker cae a media recarga (decisión del /review, hallazgo de atomicidad).

## 3. Premisas (confirmadas en sesión)

1. El fix de una divergencia es re-sincronizar parquet→SQL de la entidad, no
   re-ingestar. El parquet consolidado es la fuente de verdad durable; re-ingesta
   solo hace falta si el parquet está incompleto (ya cubierto por la validación
   de snapshots, ver `docs/adls-synapse-link-contract.md`).
2. El MERGE ya es por-entidad e idempotente → reuso casi total de `SqlExecutor`.
3. Vale el plumbing de un job acotado a una entidad frente a Force.

## 4. Arquitectura del cambio

### 4.1 Modelo de datos

- `JobType.cs`: añadir `ResyncTable`.
- `PipelineState.cs` → `TableState`: añadir campos de drift escritos por
  reconcile (hoy `Status` solo lo escribe SQL/ingest):
  - `double? DivergencePct`
  - `string? ReconcileStatus` ("ok" | "drift")
  - `DateTimeOffset? LastReconciledAt`
  - `bool DivergenceAcknowledged` (+ `string? AckReason`, `DateTimeOffset? AckAt`)
- Trazabilidad: nuevo `ReconcileRemediation` (record) persistido por `ReportStore`
  en `reports/remediation/{id}.json`:
  `{ Id, PipelineId, Entity, JobId, TriggeredAt, TriggeredBy, Mode (Reload|Remerge),
     DivergenceBefore, DivergenceAfter, Result }`.

### 4.2 Motor de re-sync por tabla

`OrchestrationService`: nuevo método
`Task<Job> ResyncTableAsync(string pipelineId, string entity, ResyncMode mode, CancellationToken)`.

- Respeta la guardia anti-doble-run por pipeline (`OrchestrationService.cs:101`):
  no arranca si ya hay job en curso para ese pipeline. (Decisión: un solo job
  activo por pipeline; re-syncs de tablas distintas son secuenciales.)
- Crea `Job { Type = ResyncTable, StageStatus = { sql, reconcile } }` (sin ingest).
- **Manifest sintético**: `IngestManifestStore` gana
  `BuildResyncManifest(pipelineId, jobId, entity)` que escribe un `IngestManifest`
  de una sola entidad apuntando a los paths durables deterministas
  (`GetConsolidatedBlobPath` / `GetConsolidatedSchemaBlobPath`). Así `SqlExecutor`
  corre **sin cambios** (ya lee parquet/schema de ADLS, no del ingest original).
- Encola `PipelineTask { Stage = Sql, Payload = { entity, resyncMode } }`.

`SqlExecutor` (cambio mínimo): si `Payload["resyncMode"] == "Reload"`, recolecta las
claves (`Id`) durante el merge en streaming y al final borra las huérfanas vía
`ISqlMergeExecutor.DeleteOrphansAsync` (tabla temporal de claves +
`SqlMergeBuilder.BuildDeleteOrphans`). En modo `Remerge` solo hace upsert. El resto
del bucle es idéntico.

`StageRouter`: para `JobType.ResyncTable`, encadenar Sql → Reconcile (sin ingest).

`ReconcileExecutor`: aceptar `Payload["entity"]` para acotar la comparación a esa
entidad (hoy el path `JobType.Reconcile` lista todo; se filtra por entidad).

### 4.3 Reconcile escribe estado por tabla

`ReconcileExecutor` / `ReconcileService.BuildReport`: además de guardar el
`ReconcileReport`, actualizar `PipelineStateStore` por entidad con
`DivergencePct`, `ReconcileStatus`, `LastReconciledAt`. Esto cierra el gap de
visibilidad: `/tables` puede mostrar drift por tabla.

`IsPartial` / alertas: excluir entidades con `DivergenceAcknowledged = true` del
cómputo de alerta y del banner, para que divergencias legítimas no sigan
alertando.

### 4.4 UI

- Nueva página `Reconcile.razor` (`/reconcile`) + item en `NavMenu.razor`.
  - Tabla de entidades divergentes (último reconcile): Entity, SourceRows,
    DestRows, DivergencePct, dirección (src>dst / dst>src), LastReconciledAt, badge.
  - Acciones por fila:
    - **Re-sincronizar (recarga)** → `ResyncTableAsync(..., Reload)`. Recomendada;
      arregla ambas direcciones.
    - **Re-mergear (rápido)** → `ResyncTableAsync(..., Remerge)`. Solo si la
      dirección es src>dst (más ligero, no trunca).
    - **Marcar aceptada** → escribe ack en `TableState` (para tablas que
      divergen legítimamente).
    - Link a historial (`ReconcileRemediation`).
  - Confirmación (MessageBox) en la recarga: el borrado de huérfanas modifica el
    destino (se reconcilia contra el parquet, pero confirmamos).
- `Tables.razor`: columna Divergencia (visibilidad, leída del último reporte).

### 4.5 Modo distribuido

Encaja en el patrón actual: el orquestador (`api`) crea job+tareas; la tarea
`Stage = Sql` la toma el worker `sql` (único con credenciales SQL), la de
`Stage = Reconcile` el worker `reconcile`. El routing por `PipelineStage` ya
existe. Sin cambios de transporte.

## 5. Riesgos y casos borde

- **Parquet ausente para la entidad** (nunca ingestada): `SqlExecutor` ya marca
  `skipped` cuando falta parquet/schema (`SqlExecutor.cs:84-92`). La UI debe
  decir "no hay parquet consolidado; corre Force primero".
- **DELETE de huérfanas y FKs**: verificado en instancia real (2026-06-17) — el
  destino NO tiene claves foráneas declaradas entre tablas de entidades
  (`INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_TYPE='FOREIGN KEY'`
  devolvió vacío), así que el `DELETE` de huérfanas no rompe integridad referencial.
- **Memoria del borrado de huérfanas**: el modo Reload acumula las claves (`Id`) de
  la tabla en memoria para el `DELETE`. Acotado a UNA tabla y el re-sync es ocasional
  y dirigido, así que es aceptable; para tablas de millones de filas conviene migrar
  a inserción incremental de claves en la temporal durante el stream (fuera de alcance).
- **Reload con parquet vacío**: si el parquet existe pero tiene 0 filas, el conjunto
  de claves queda vacío y el `DELETE` vacía la tabla — correcto (SQL espeja al parquet).
  El parquet ausente se filtra antes (`skipped`), así que no hay borrado accidental.
- **Recarga vs scheduler**: la guardia anti-doble-run cubre el solape con un job
  programado.
- **Ack + datos nuevos**: un reconcile posterior re-evalúa la divergencia; el ack
  debería invalidarse si la divergencia cambia de magnitud (decisión: ack guarda
  el `DivergencePct` aceptado; si el nuevo difiere > umbral, se reactiva alerta).
- **Divergencia por deletes no propagados**: este diseño la resuelve vía recarga.
  Nota de seguimiento: evaluar si conviene que el merge incremental propague
  deletes (columna IsDelete del changelog) para no depender de recargas — fuera
  de alcance aquí.

## 6. Plan por fases

1. **Datos**: `JobType.ResyncTable`, campos de drift+ack en `TableState`,
   record `ReconcileRemediation` + `ReportStore.SaveRemediation/ListRemediation`.
2. **Motor**: `ResyncTableAsync`, `BuildResyncManifest`, `TruncateAsync` +
   `BuildTruncate`, rama de truncate en `SqlExecutor`, encadenado en `StageRouter`,
   filtro de entidad en `ReconcileExecutor`.
3. **Estado por tabla**: reconcile escribe drift en `PipelineStateStore`; ack
   excluye de alerta/banner.
4. **UI**: `Reconcile.razor` + `NavMenu`, columna+acción en `Tables.razor`,
   confirmaciones.
5. **Tests** (xUnit): `ResyncTableAsync` encola SQL+reconcile sin ingest; recarga
   = truncate+merge deja `SQL==parquet` en ambas direcciones; ack suprime
   `IsPartial`; reconcile escribe `TableState.DivergencePct`.

## 7. Principios aplicados

- **DRY/KISS**: reusa el bucle de merge por-entidad existente y el routing por
  stage; el manifest sintético evita duplicar la lógica de lectura de parquet.
- **YAGNI**: no se construye re-ingesta por entidad (Approach C) — la validación
  de snapshots ya cubre el parquet incompleto.
- **SRP**: reconcile detecta y marca; el job de resync remedia; la UI orquesta.
  Sin mezclar responsabilidades.

## 8. Asignación (resuelta)

Verificado en instancia real (2026-06-17): el destino NO tiene claves foráneas
(`INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE CONSTRAINT_TYPE='FOREIGN KEY'` vacío),
así que el `DELETE` de huérfanas del modo Reload no rompe integridad referencial.

## 9. Hallazgos del /review (post-implementación) y su cierre

- **[P2] Reconcile de re-sync borraba la foto del pipeline**: el reporte de un resync
  (una entidad) se volvía el "último reporte" y la UI perdía las demás tablas.
  Cerrado con `ReconcileService.BuildScopedReport`, que arrastra las demás entidades
  del último reporte y reemplaza solo la re-sincronizada.
- **[P2] TRUNCATE+recarga no atómico (ventana de tabla vacía)**: cambiado a upsert +
  `DELETE` de huérfanas (la tabla nunca queda vacía). Ver secciones 2.1 y 4.2.
- **[P3] Stage `ingest` fantasma** en jobs de resync: se marca `skipped` en el
  `StageStatus` del job para que `StageFlow` no lo muestre en blanco.
- **[bajo] `GetAcks` case-sensitivity**: el casing de entidades es consistente en la
  práctica (origen ordinal único); riesgo bajo, anotado.
