# Diseño: Reintento de pipelines, pausa de tablas y spinner en test de conexión

> Office-hours / design doc. Define el QUÉ y el CÓMO; no es código.
> Estado: aprobado ("tú decides" → recomendaciones por defecto).
> Contexto: Sluice.Portal (.NET 8 / Blazor + FluentUI 4.14.2), persistencia JSON sobre `DataDir`, modo distribuido (varios workers, scheduler solo en orquestador).

## Premisas confirmadas

- **P1.** `DataDir` es compartido entre nodos (la cola de tareas ya lo asume). Un store JSON de pausas lo ven todos los workers sin infraestructura extra.
- **P2.** Reintentar = re-ejecutar generando una ejecución nueva; el job en `Error` se conserva como historial. Patrón: `OrchestrationService.ResyncTableAsync`.
- **P3.** Pausar una tabla = omitirla en próximas corridas. No borra estado ni datos ya sincronizados. Default: activa.
- **P4.** "Pausada" se modela como conjunto de nombres por pipeline (ausencia = activa), no como flag en `TableState` (se sobrescribe en cada ingest).

---

## Feature 1 — Reintentar pipeline en error

**Decisión:** Opción A (reintento completo desde Ingest), habilitado para `Error`, `Partial` y `Blocked`. Crea un Job nuevo; no edita el fallido.

### Backend
- `Services/OrchestrationService.cs` — nuevo `RetryJobAsync(string jobId, CancellationToken)` en `IOrchestrationService`:
  1. `Get(jobId)`; validar que `Status ∈ {Error, Partial, Blocked}` (si no, excepción/400).
  2. Reusar la guarda anti-doble-run (`_jobs.ListRunning().Any(j => j.PipelineId == ...)`) → si hay uno corriendo, rechazar.
  3. Delegar en `RunPipelineAsync(job.PipelineId, job.Type)` para crear Job nuevo + encolar tarea inicial de `Ingest`. Reusa todo el camino existente; sin tocar artefactos en disco.
  4. `AppendEvent` / `ActivityLog` con `pipeline.retry` (espejo de `pipeline.run`).
- Distribuido: la tarea de Ingest encolada la reclama cualquier worker. Sin cambios en el loop de workers.
- La tarea fallida queda en `tasks/{stage}/error/` como evidencia; no se limpia en v1 (no estorba: el Job nuevo tiene su propio `JobId`).

### API
- `POST /api/jobs/{id}/retry` en `Endpoints/ManagementEndpoints.cs` (junto a los `/api/jobs`), llamando a `RetryJobAsync`.

### UI
- `Components/Pages/Jobs.razor` — `FluentMenuItem` "Reintentar" en el menú de acciones, visible si `context.Status ∈ {Error, Partial, Blocked}` (espejo del Cancelar que hoy solo aparece en `Running`). Confirmación con `MessageBox` y Toast de resultado.
- `Components/Pages/JobDetail.razor` — mismo botón en la cabecera del detalle.

### Fuera de alcance (v2)
- Reintento desde la etapa que falló (Opción B): eficiente pero frágil (depende de manifest/parquet en disco) y requiere limpiar la tarea en `error/`. Añadir solo si re-ingestar resulta caro en producción.

---

## Feature 2 — Pausar / habilitar sincronización por tabla

**Decisión:** Opción A. Store separado de pausadas por pipeline + filtro único en Ingest. Tablas pausadas permanecen **visibles** en `/tables` con badge y toggle (si no, no habría forma de reactivarlas).

### Modelo y persistencia
- Nuevo store `Services/Stores/TablePauseStore.cs` siguiendo el patrón de `JsonFileStore` / `SettingsStore`:
  - Archivo por pipeline: `{DataDir}/{pipelineId}/state/paused_tables.json` (junto a `pipeline_state.json`, no lo pisa el ingest).
  - Contenido: `{ "paused": ["EntityA", "EntityB"] }` (set de nombres). Escritura atómica vía `AtomicJsonFile`.
  - API del store: `IsPaused(pipelineId, entity)`, `GetPaused(pipelineId)`, `Pause(pipelineId, entity)`, `Resume(pipelineId, entity)`.
- Default-activa sale gratis: ausencia en el set = activa. Sin migración de estado existente.

### Filtro de ejecución
- `Workers/IngestExecutor.cs` (loop `foreach (var entity in entities)`, ~línea 129): saltar entidad si `tablePauseStore.IsPaused(pipelineId, entity)`, análogo a `ExcludedEntities.IsExcluded`. Al no ingestarse, no llega a SQL MERGE → cero doble-filtro.
- Registrar las pausadas omitidas en el `Job.Events` / log para trazabilidad ("3 tablas omitidas por pausa").

### UI
- `Components/Pages/Tables.razor` (hoy grilla de solo lectura): añadir `TemplateColumn` "Sincronización" con un toggle/switch por fila (patrón del toggle `Enabled` de `Pipelines.razor`), que llama a `Pause/Resume` e invalida la fila. Badge "Pausada" cuando aplica.
- Inyectar el nuevo store/servicio en la página.

### API (opcional, por consistencia)
- `POST /api/pipelines/{id}/tables/{entity}/pause` y `/resume` en `ManagementEndpoints.cs`.

### Notas de diseño
- El estado/watermark de una tabla pausada se conserva intacto; al reactivarla, el ingest la retoma desde su watermark.
- Distribuido: el filtro lo aplica cualquier worker leyendo el JSON compartido; sin coordinación extra.

---

## Feature 3 — Spinner en test de conexión (FluentUI)

**Decisión:** reusar `FluentProgressRing` (spinner canónico, ya en `Components/Shared/PanelState.razor`) en los **3 lugares**, con el control deshabilitado durante el test y `StateHasChanged()` alrededor del await.

- `Components/Pages/Sources.razor` — `Test(id)` ya tiene `_saving` a medias: añadir `StateHasChanged()` tras `_saving = true`, reflejar el spinner en el menú/botón "Test" y `Disabled` mientras corre. Mejor usar un `_testingId` por fila para no bloquear toda la grilla.
- `Components/Pages/Destinations.razor` — no tiene manejo de carga: replicar el patrón anterior.
- `Components/Pages/Dashboard.razor` — `TestConnectionAsync()` hace dos tests secuenciales (source + destination, el caso de mayor latencia, hasta ~15 s por el `Connect Timeout` de SqlClient): añadir flag `_testing` + `FluentProgressRing` junto al botón "Test connections" y `Disabled="@_testing"`.

Sin cambios de backend. El feedback de resultado sigue por `ToastService`.

---

## Orden de entrega sugerido

1. **Feature 3** (bajo riesgo, valor inmediato, sin backend).
2. **Feature 1** (reusa `RunPipelineAsync`, cambio acotado).
3. **Feature 2** (nuevo store + filtro + UI; la más amplia).

Cada una es un commit/PR independiente y verificable por separado.

## Verificación mínima por feature

- **F3:** abrir `/sources`, `/destinations`, dashboard; lanzar test contra un host lento/inalcanzable y confirmar que el spinner aparece, el botón se deshabilita y vuelve al terminar.
- **F1:** forzar un job a `Error`, pulsar Reintentar, confirmar que nace un Job nuevo `Running` y que el fallido se conserva; verificar la guarda anti-doble-run.
- **F2:** pausar una tabla en `/tables`, correr el pipeline, confirmar que se omite (en log/eventos) y su estado no cambia; reactivar y confirmar que vuelve a sincronizar desde su watermark. Probar con 2 workers que ambos respetan la pausa.
