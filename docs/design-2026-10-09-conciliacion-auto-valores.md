# Diseño — Corrección automática, comparación de valores y alertas por correo

Fecha: 2026-10-09
Estado: Implementado

## 1. Problema

La conciliación solo **cuenta filas** (parquet consolidado frente a SQL) y la corrección era siempre
**manual** desde `/reconcile`. Un valor cambiado en SQL con el mismo número de filas pasaba sin
aviso. Las alertas salían por un webhook genérico sin un receptor listo para usar.

## 2. Decisiones (confirmadas con el usuario)

| Tema | Decisión |
|---|---|
| Corrección automática | Opcional en Ajustes (`AutoRemediate`, apagada por defecto). Un re-sync en modo **recarga** de las tablas accionables, en un solo job. **Un intento**: el re-sync no dispara otro. |
| Tablas accionables | Divergencia > umbral y sin aceptación vigente (`ReconcileService.ActionableEntities`). Por valores: con diferencias y sin aceptación. |
| Comparación de valores | **Bajo demanda** (job `CompareValues`), hash por fila emparejado por Id. No corre en cada corrida: lee las tablas enteras. |
| Valores → corrección | Sí, con la misma regla y el mismo interruptor. |
| Tras la corrección | Se verifican solo conteos; la comparación de valores se relanza a mano. |
| Alertas por correo | Paquete descargable desde Ajustes: Logic App de consumo + conexión de Office 365 (plantilla ARM, `deploy.ps1`, guía y vista previa). |

## 3. Diseño

### Corrección automática

- `ReconcileExecutor` deja las tablas accionables en `Job.Summary["autoRemediateEntities"]` (texto
  separado por comas: el Summary vuelve de JSON como `JsonElement`).
- `StageRouter`, al **cerrar** el job (si no, la guarda anti doble run lo rechazaría), llama a
  `OrchestrationService.ResyncTablesAsync(..., Reload, "auto")` si el ajuste está activo y el tipo es
  elegible (`AutoRemediation.IsEligible`: no `ResyncTable` ni `Diff`). Un error (p. ej. el scheduler
  arrancó otro job) se registra en el job y en el registro de actividad, **sin hacer fallar la tarea**
  (una excepción del router marcaría el job como Error).
- `ResyncTablesAsync` generaliza el re-sync a varias tablas: un manifest sintético con todas, una
  remediación por tabla (`TriggeredBy` = `auto` | `user`) y payload `entity` con las tablas por comas.
  `BuildScopedReport` arrastra las tablas no re-sincronizadas del último reporte.
- Todos los contenedores corren el mismo binario y comparten `DATA_DIR`; el lock de run es de fichero,
  así que el worker de conciliación puede lanzar el re-sync.

### Comparación de valores

- `ValueComparer` normaliza cada valor **como lo guarda SQL** (mismas conversiones que el MERGE:
  `CoerceForSql` + tipo de columna): `DECIMAL(p,s)` redondeado al scale con mitades hacia fuera,
  `DATETIME2/DATETIMEOFFSET` por ticks (el MERGE envía un `DateTime`; en `DATETIMEOFFSET` se compara la
  hora de reloj), Guid canónico, `BIT` 0/1, enteros a `long`. FNV-1a de 64 bits por fila, NULL ≠ "".
- Lado parquet en un índice Id → hash (Guid como clave, ~40 bytes/fila, una tabla cada vez); lado SQL
  en streaming (`ISqlMergeExecutor.ReadRowsAsync`, `SequentialAccess`, sin CommandTimeout).
  Resultado: iguales, distintas, faltan en SQL, sobran en SQL (Id repetido en SQL cuenta como sobrante)
  y hasta 20 Id de muestra de cada tipo.
- Las columnas de etiqueta de enum no se comparan: no están en el schema consolidado (se derivan al cargar).
- Reporte propio en `reports/values/values-{jobId}.json` (incluido en la retención); no toca la
  conciliación de conteos. El detalle del job muestra la comparación en `Job.Entities`.

### Alertas

`AlertService.SendReconcileAlertAsync` envía `reconcile.divergence` / `reconcile.values` con
`entities` y `autoRemediation` (`off` | `scheduled` | `unresolved` | `none`), conservando
`text`/`message`. La Logic App del paquete distingue esos `eventType` (test de contrato en
`LogicAppPackageTests`). La plantilla y la vista previa salen del mismo generador (`scripts/generate-logicapp.js`).

## 4. Riesgos

- **Memoria** de la comparación: ~0,4–0,5 GB para una tabla de 11 M de filas (sin `mem_limit` en los
  workers, por decisión del usuario).
- **Carga en el SQL de producción**: la comparación lee tablas enteras; por eso es bajo demanda.
- **URL del disparador**: secreto (`sig=`); queda también en el historial de implementaciones de Azure.
