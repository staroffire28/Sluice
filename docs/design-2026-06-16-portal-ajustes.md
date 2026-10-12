# Ajustes Portal — 2026-06-16

Batch de 6 ajustes surgidos en pruebas reales del flujo distribuido. Decisiones tomadas
en /office-hours. Orden de implementación: el item 0 va primero (desbloquea todo lo demás).

Estado: **plan aprobado, sin implementar**. Branch: `master`. Sin commits (regla de sesión).

---

## 0. Resiliencia del worker (bug crítico, va primero)

**Problema:** durante la primera corrida, `worker-ingest` se apagó solo (exit 0). Causa:
`File.Move` en el claim de la cola lanzó `UnauthorizedAccessException` (lock transitorio de
Windows con réplicas concurrentes); la excepción escapó del loop del `BackgroundService` y,
con `BackgroundServiceExceptionBehavior = StopHost` (default), .NET apagó el container.

**Fix (3 piezas, TDD donde aplique):**
1. `TaskStore.WriteTaskAsync` (línea ~252): reintentar `File.Move(overwrite)` ante
   `IOException`/`UnauthorizedAccessException` con backoff corto (5 intentos × ~50ms).
2. `TaskStore.TryClaimAsync` (línea ~48): capturar también `UnauthorizedAccessException` en
   el move de claim → tratar como "perdí la carrera" y `continue`.
3. `WorkerHostService.ExecuteAsync` (loop): envolver el procesamiento por stage en try/catch
   → loguear y continuar al siguiente tick. Defensa en profundidad: ningún error transitorio
   de cola puede tumbar al worker.

**Tests:** TaskStore — claim/escritura concurrente reintenta y no propaga; WorkerHost — un
error transitorio en claim no detiene el loop.

---

## 1. Cancelar corrida en ejecución (feature) — D1: cooperativa

Hoy no existe. `JobStatus` no tiene `Cancelled`.

**Enfoque cooperativo:**
- Añadir `JobStatus.Cancelled`.
- `OrchestrationService.CancelAsync(jobId)`: marca el job `Cancelled`, elimina las tareas
  `Pending`/`Claimed` de ese job de la cola (todas las stages) para que no se encolen/procesen
  más. El stage en vuelo termina o se abandona al expirar el lease (no se mata el worker).
- El `StageRouter` no debe rutear el siguiente stage si el job está `Cancelled`.
- UI: botón "Cancelar" en Dashboard (Running jobs), Jobs y JobDetail, visible solo si
  `Status == Running`. `StatusBadge` mapea Cancelled.

**Tests:** CancelAsync deja el job Cancelled y purga sus tareas; el router no avanza un job cancelado.

---

## 2. `/parquet` PathNotFound 404 (bug) — sin decisión

`ParquetArtifactService.ListAsync` → `store.ListParquetAsync("{pipelineId}/")`. Si el
`parquet_container` aún no tiene nada (o no existe), ADLS devuelve 404 PathNotFound y la
página muestra el error crudo.

**Fix:** capturar `RequestFailedException` con status 404 / `PathNotFound` en
`AdlsClient.ListParquetAsync` → devolver lista vacía. La página ya tiene empty state.

**Test:** ListParquet sobre path inexistente → lista vacía, no excepción.

---

## 3. "Details" no navega en job en ejecución (bug) — investigar

Ruta `/jobs/{JobId}` existe. Botones usan `FluentButton Href`. Hipótesis: `Href` de
FluentButton no navega fiable en InteractiveServer y/o el auto-refresh de 3s del Dashboard
interrumpe el click.

**Fix probable:** reemplazar `Href` por `OnClick` con `NavigationManager.NavigateTo($"/jobs/{id}")`
en Dashboard, Jobs (y donde aplique). Confirmar repro al implementar.

---

## 4. Doble Run = dos jobs simultáneos (feature) — D2: rechazar

`OrchestrationService.RunPipelineAsync` (línea 59) no chequea jobs en curso; crea uno nuevo
siempre. El scheduler sí salta con `ListRunning()`.

**Fix:** al inicio de `RunPipelineAsync`, si `_jobs.ListRunning().Any(j => j.PipelineId == pipelineId)`
→ lanzar `InvalidOperationException("Ya hay un job en ejecución para este pipeline.")`. La UI
ya muestra el mensaje de error (Dashboard/Pipelines capturan y pintan MessageBar).

**Test:** segundo RunPipelineAsync con job corriendo → excepción, no se crea segundo job.

---

## 5. Cards del "flow" muy grandes en Running jobs (UX) — D3: compactar inline

`.stage-flow__item { min-width: 110px }` dentro de la columna estrecha del DataGrid → wrap.

**Fix:** variante compacta de `StageFlow` (parámetro `Compact`) o clase `.stage-flow--compact`
con menos padding, sin `min-width` fijo y etiquetas cortas, usada en la columna "Flow" del
Dashboard. Mantiene visible ingest→sql→reconcile sin salto de línea.

---

## Orden y verificación

1. **0** (resiliencia) → build + tests + reconstruir stack distribuido y confirmar que los
   workers sobreviven contención.
2. **2, 4, 5** (fixes pequeños, bajo riesgo).
3. **3** (investigar navegación).
4. **1** (cancelar, el más grande).

Cada cambio: build 0 errores + suite de tests verde + (donde toque runtime) rebuild del stack
y verificación viva. Sin commit salvo que se pida.
