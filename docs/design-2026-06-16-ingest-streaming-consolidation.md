# Ingest streaming + consolidación de parquet — 2026-06-16

Rediseño del data path de ingest para eliminar el `OutOfMemoryException` en cargas full,
manteniendo la "doble confianza": el parquet consolidado es un artefacto **independiente
de SQL** (el dato vive en parquet *y* en SQL, ninguno derivado del otro al leer).

Estado: **plan aprobado, sin implementar.** Branch: `master`. Sin commits.

## Problema

`IngestExecutor` acumula TODAS las filas de TODOS los snapshots/entidades en
`Dictionary<entity, List<Dictionary<string,object?>>>` (un dict boxed por fila) y recién
al final deduplica + escribe parquet. Un Force de 14 snapshots = dataset histórico entero
en RAM en la representación más cara → OOM (confirmado en corrida real, ~42 min).

Secundario: lease de tarea 120s vs corrida de 42 min sin heartbeat → con >1 réplica de
ingest habría reprocesamiento concurrente.

## Flujo objetivo (decidido con el usuario)

```
procesar snapshot → parquet transitorio (por snapshot) → unificar a 1 parquet por tabla → MERGE a SQL
```

El parquet consolidado se crea en la capa de parquet (NO exportando desde SQL), para que
sea independiente de SQL. SQL se carga DESDE ese parquet único.

## Clave de dedup (existente, reutilizar)

`WatermarkProcessor`: PK = `CdmSchemaParser.IdColumn`; "última versión" = fila con el
máximo `CdmSchemaParser.WatermarkColumn`. Group by Id, keep max-watermark.

## Restricción transversal: TODO debe ser streaming

`ParquetMaterializer` hoy lee/escribe un único row group con todas las filas en memoria.
Hay que añadir primitivas de streaming (multi row group write; lectura row-group a
row-group; proyección de columnas) para no reintroducir el OOM en ningún punto.

## Cambios

### 1. Ingest por snapshot (memoria acotada a 1 snapshot)
- `IngestExecutor`: quitar `entityAccumulator`. Por cada snapshot (en orden) y entidad:
  CSV → escribir parquet **transitorio** `_staging/{jobId}/{entity}/{NNNN-snapshot}.parquet`
  (prefijo numérico para orden), schema una vez por entidad.
- Manifiesto: por entidad, lista ordenada de parquets transitorios + schemaPath +
  consolidatedPath.

### 2. Consolidación a 1 parquet por tabla (streaming, en la app)
- Nuevo `ParquetConsolidator`: entrada = transitorios nuevos (orden) + consolidado previo
  (en incrementales); salida = un parquet por tabla con la última versión por Id.
- **Mecanismo memory-safe (recomendado): dos pasadas con proyección de columnas.**
  - Pasada 1: leer solo `Id` + `Watermark` (columnar, barato) de todas las fuentes →
    `Dictionary<Id, maxWatermark>`. Memoria = claves + timestamp, no filas anchas.
  - Pasada 2: leer filas completas row-group a row-group; emitir la fila cuyo watermark
    == max[Id] y cuyo Id no se haya escrito aún (`HashSet<Id>`), en streaming a la salida.
  - Pico de memoria = conjunto de claves (≈ filas distintas, solo Id+timestamp) + 1 row group.
- Borrar `_staging/{jobId}/` al consolidar.
- Alternativa más simple si las tablas no son enormes: `Dictionary<Id, fila>` (acotado a
  filas distintas, no a la suma con duplicados). Decisión: empezar con la de dos pasadas
  (la pediste por OOM); cae a la simple solo si complica de más.

### 3. MERGE a SQL en streaming desde el consolidado
- `ParquetMaterializer`: añadir lectura en streaming (`IAsyncEnumerable` por lotes / row
  group). `SqlExecutor`: por entidad, EnsureTable, leer el consolidado por lotes y
  `MergeEntityAsync` por lote (ya hace bulk-insert + MERGE en chunks de 1000). No cargar
  el parquet entero.
- El manifiesto de SQL ahora apunta al consolidado (1 por entidad), no a per-snapshot.

### 4. Heartbeat del lease (cualquier stage largo)
- `WorkerHostService.ProcessStageAsync`: tras claim, lanzar heartbeat periódico
  (`_tasks.HeartbeatAsync` cada ~lease/3) mientras corre el executor; parar en finally.
  Evita que un stage largo pierda el lease y sea reclamado.

## Orden de increments (cada uno: build + tests verde)
1. Primitivas streaming en `ParquetMaterializer` (write multi-row-group, read por row group,
   read con proyección de columnas) + tests de round-trip.
2. `ParquetConsolidator` (dedup dos pasadas) + tests con varios parquets/dups/deletes.
3. `IngestExecutor` por snapshot → transitorios → consolidación. Manifiesto nuevo.
4. `SqlExecutor` MERGE en streaming desde el consolidado.
5. Heartbeat del lease en `WorkerHostService`.
6. Rebuild + corrida real (validación del usuario contra su dataset).

## Riesgo
Es el core del pipeline y solo se valida de verdad contra datos reales. Se hace en
increments verificados por build+tests; la corrida real la confirma el usuario.
