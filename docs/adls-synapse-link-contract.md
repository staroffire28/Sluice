# Contrato ADLS — Synapse Link for Dataverse

Contrato externo fijo para Sluice. La Fase 5 (ingest) y Fase 6 (scheduler) deben implementarlo sin modificaciones.

## A.1 Estructura del contenedor

```
container-root/
├── Changelog/
│   └── changelog.info
├── OptionsetMetadata/                        # EXCLUIR
├── EntitySyncFailure/                        # EXCLUIR
├── Microsoft.Athena.TrickleFeedService/      # CDM schemas — EXCLUIR como entidad
│   ├── account-model.json
│   ├── contact-model.json
├── 2026-06-08T09.00.00Z/
│   ├── account/2024-01.csv, 2024-02.csv ...
│   ├── contact/2024-01.csv ...
├── 2026-06-08T10.00.00Z/
└── 2026-06-08T11.00.00Z/
```

- Snapshot folder: ISO timestamp with `:` replaced by `.` → `YYYY-MM-DDTHH.MM.SSZ`
- Detect snapshots: regex `^\d{4}-\d{2}-\d{2}`
- Exclude from entity list: `OptionsetMetadata`, `Changelog`, `EntitySyncFailure`, `Microsoft.Athena.TrickleFeedService`, any folder starting with `_`

## A.2 Changelog/changelog.info

- Plain UTF-8, single line: settled snapshot name (e.g. `2026-06-08T10.00.00Z`)
- Settled = Synapse Link finished exporting that snapshot
- Only process snapshots up to and including settled; later folders are in-progress exports
- Read file `last_modified` for changelog stall detection (`CHANGELOG_STALL_HOURS`)
- Empty or missing → treat export as in progress → block processing (job status `blocked`)

## A.3 Entity schema: {entity}-model.json (CDM)

Search order (fallback chain):

1. `{snapshot}/Microsoft.Athena.TrickleFeedService/{entity}-model.json`
2. Settled snapshot
3. Any snapshot
4. Container root

Two JSON variants:

- V1: `{ "entities": [ { "name", "attributes": [ { "name", "dataType" } ] } ] }`
- V2: `{ "definitions": [ { "entityName", "hasAttributes": [ { "name", "dataType" } ] } ] }`

`dataType` may be string or array. Decimals may use `appliedTraits` with `is.dataFormat.numeric.shaped` (precision, scale).

### CDM → SQL Server mapping

| CDM | SQL Server |
|-----|------------|
| string | NVARCHAR(MAX) |
| guid | UNIQUEIDENTIFIER |
| dateTime | DATETIME2(7) |
| dateTimeOffset | DATETIMEOFFSET(7) |
| boolean | BIT |
| integer, bigInteger, int64 | BIGINT |
| int32 | INT |
| int16 | SMALLINT |
| decimal | DECIMAL(38,6) or trait precision/scale |
| double | FLOAT |

System columns (inject if missing): `Id` (guid, NOT NULL) first; `SinkModifiedOn` (dateTime) last.

## A.4 Incremental snapshot detection

Inputs: `last_processed` (persisted state), `settled` (from changelog.info).

1. List and sort snapshots ascending by name (= timestamp).
2. No valid `settled` in list → process nothing.
3. First run (`last_processed` null): process all from start through `settled` inclusive.
4. Incremental: only snapshots with name > `last_processed` AND position ≤ settled position.

Pre-validation: empty/missing changelog → block. Folders with name > `settled` may exist; log unclosed snapshots but only process through `settled`.

## A.5 Entity CSV files

- Path: `{snapshot}/{entity}/{partition}.csv` — multiple files per entity; sort and concatenate.
- No header row. Column order from model.json (+ injected Id, SinkModifiedOn).
- UTF-8, comma delimiter, standard quoting, empty = null.
- Ragged rows (fewer columns) → pad with null, align to schema; loose cast (errors → null).

## A.6 Incremental processing and watermark

- Watermark column: `SinkModifiedOn`. MERGE key: `Id`.
- Per entity: rows where `SinkModifiedOn` > previous watermark; drop null `Id`; dedupe by `Id` keeping highest `SinkModifiedOn`; new watermark = max `SinkModifiedOn` in batch.
- Persist per pipeline: `data/{pipeline_id}/state/pipeline_state.json` with `last_processed_snapshot`, `global_watermark`, per table: `row_count`, `watermark`, `last_load`, `last_snapshot`, `status`.
- SQL load: T-SQL MERGE per entity using `Id` as upsert key.

## A.7 Reconciliation

Compare source row counts (per entity in snapshot) vs destination (`row_count` in SQL). Divergence above `RECON_DIVERGENCE_PCT` → job `partial` + alert.

## A.8 SQL Server destination contract (SQL executor)

**Fixed rule:** create-table-if-not-exists from CDM. No soft-delete. No full schema evolution.

### Table creation

- Executor **creates destination table if not exists** (`CREATE TABLE` from CDM `model.json` → types via A.3 mapping, nullable per attribute).
- Logical primary key: `Id`.
- **Does NOT** create database or schema: assume DB (e.g. `SynapseLink`) and schema (`dbo`) **already exist**. Missing DB/schema = configuration error, not portal provisioning.
- Separate E2E reset script may recreate DB for tests; production pipeline does not.

### Schema evolution (limited)

- If table **exists**: **NO** `ALTER` for new columns; **NO** drop columns absent from CDM.
- Only allowed auto-mutation: widen existing string columns to `NVARCHAR(MAX)` (`ALTER COLUMN`) when needed.
- Any other schema drift = manual intervention; do not automate.

### UPSERT via staging + MERGE

Per entity, per batch (configurable, e.g. 1000):

1. `CREATE TABLE` temp staging `#stg_{entity}_{uuid}` (long strings as `NVARCHAR(MAX)`).
2. `INSERT` batch rows into staging (bulk insert, type coercion).
3. `MERGE` target `USING` staging `ON target.[Id] = source.[Id]`:
   - `WHEN MATCHED` → `UPDATE` all columns except `Id`
   - `WHEN NOT MATCHED` → `INSERT` all
4. `DROP` staging.

MERGE key: `Id`.

### No soft-delete

- MERGE is pure upsert (insert/update).
- **Do NOT** delete destination rows when they disappear from source.
- **Do NOT** interpret `IsDelete` or deletion logic unless explicitly requested later.