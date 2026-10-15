# Functionality

## How the pipeline works

A **job** (one execution of a pipeline) runs in three stages:

```
ADLS Gen2
↓ [Ingest]
consolidated Parquet
↓ [SQL]
SQL Server
↓ [Reconcile]
Report
```

- **Ingest** — reads what's new from the source and writes it to a clean Parquet. It compares each table's columns with the previous run: new columns are added in SQL, and removed columns or type changes are reported in the job (type changes also send an alert). It only processes snapshots that are safe (older than the one currently being written), consolidates them with the previous Parquet (keeping the latest version of each row), and saves the result as a durable copy (in ADLS or on the portal disk, as set on the source).
- **SQL** — reads that Parquet and MERGEs (upserts) it into SQL Server, creating the table if needed. An **Incremental** merges only the rows of its own run (the delta), and updates a row only if its watermark is newer (or equal with a higher `versionnumber`). It merges the full consolidated Parquet instead when the target table is empty or when the last job that loaded SQL did not finish that stage. Rows deleted in F&O arrive as rows with `IsDelete = True` and the other columns empty; by default they are deleted in SQL too (see *Create a pipeline*).
- **Reconcile** — counts rows in the Parquet vs SQL per table (rows deleted in F&O are not counted when the pipeline deletes them in SQL). If a table diverges beyond the threshold, the job is marked **Partial** and an alert fires.

> **Why a new table may not appear yet (Synapse Link).** The portal only processes snapshot folders *older* than the one that Synapse marks as being written in `Changelog/changelog.info`, so it never reads half-written exports. A table just enabled in Power Platform appears only after its folder is closed and a run has processed it. How often a folder closes depends on the interval configured in Synapse Link (for example, once a day).

A job is triggered **manually** (**Run**, **Force**, **Reconcile** or **Diff** in the run menu of Dashboard or Pipelines) or **automatically** by the scheduler when it detects new snapshots. Either way, if a job is already running for that pipeline, no second one starts.

Work flows through a **file-based queue**: each task is a JSON file whose folder (`pending` → `claimed` → `done`/`error`) is its state. A worker claims a task with a temporary lease and sends heartbeats; if it dies, the lease expires and the task returns to `pending`, so nothing gets stuck.

## Pages

| Page | Route | What you do there |
|---|---|---|
| Dashboard | `/` | Health, queue, workers; launch and cancel runs. |
| Jobs | `/jobs` | Run history (pipeline, status and From/To date filters); open detail; download PDF/JSON report; **Export CSV** with the applied filters. The *Job* column shows the short id (the final suffix); the full id appears on hover, in the detail and in the CSV. |
| Job detail | `/jobs/{id}` | Live progress: stages, per-table results, timeline. |
| Tables | `/tables` | Row counts, watermarks, last data (highlighted after *Days without data*, see Settings) and load status per table; **Re-sync** any table from its Parquet. |
| Sources | `/sources` | ADLS source connections (create, test, edit, delete). |
| Destinations | `/destinations` | SQL Server destination connections. |
| Pipelines | `/pipelines` | Create/edit pipelines, schedule them, run them. |
| Snapshots | `/snapshots` | ADLS snapshot folders, changelog state, backlog. |
| Reconcile | `/reconcile` | Diverging tables and their remediation. |
| Parquet | `/parquet` | Materialized Parquet files, in ADLS or on the portal disk (download, delete, **Versions** of each table's consolidated file). |
| Analytics | `/analytics` | Performance indicators and charts, for all pipelines or one (*Pipeline* selector). The queue and workers are shared and are not filtered. |
| Logs | `/logs` | Activity log (user and system events), filterable by source, level, text and From/To dates; **Export CSV** with every record matching the filters. |
| Settings | `/settings` | Global operational parameters: scheduler, divergence threshold, automatic correction, retention, alerts (webhook) and the download of the email Logic App package. |
| Help | `/ayuda` | This user guide. |

The side menu can be collapsed into an icon rail with its collapse button; the choice is remembered in the browser.

Status is shown everywhere as a colored badge: **Running/Claimed** (accent), **OK** (green), **Error** (red), **Partial/Stalled** (orange), **Cancelled** (gray), **Pending** (neutral).

## Common tasks

**Create a connection.** Sources/Destinations → Add → fill the fields → Save → Test. Secrets are stored encrypted and shown as `***`.

**Create a pipeline.** Pipelines → Add → pick a source and a destination → Save. It starts with the scheduler off; run it manually first. **Rows deleted in F&O** sets what happens to deletes: *Delete them in SQL too* (default) or *Load them flagged* (`IsDelete = 1`, other columns empty). A change applies from the next run; to fix rows already loaded, run **Force**.

**Run a pipeline.** From the run menu on Dashboard or Pipelines: **Run** (Incremental: only new data, the usual one), **Force** (reload SQL from consolidated Parquet — all tables), **Reconcile** (verify counts only), **Diff** (report differences only). Confirm in the dialog.

**Follow / cancel a run.** Open the job detail to watch stages, tables and timeline live; its canvas also shows the step where the consolidated Parquet is stored: *Consolidate and publish to ADLS*, or *Consolidate and save to the portal disk* when the source keeps its Parquet on the portal disk. **Cancel** is available only while the job is running.

**Retry a job.** From the job menu on Jobs, or **Retry** in the job detail, for jobs in Error, Partial or Stalled. It creates a new job; the original stays in history.

**Schedule a pipeline.** Pipelines → Enable scheduler. The portal then launches an Incremental run on its own when new snapshots arrive.

**Reset a pipeline.** Pipelines → the row's menu → **Reset**. It deletes the record of processed snapshots, each table's state and the pipeline's consolidated Parquet files; this cannot be undone. The configuration, paused tables and the original Synapse Link CSVs are kept, and the next run reprocesses every snapshot from scratch. The dialog offers *Also empty the destination tables in SQL*: if unchecked, the next run updates the rows already in SQL with MERGE.

**Reconcile and remediate.** Reconcile → pick the pipeline. For a diverging table: **Re-sync** reloads it from the consolidated Parquet: it updates every row and deletes the ones no longer there, without emptying the table (fixes rows missing or extra in SQL); **Re-merge** only adds/updates rows (shown when SQL is missing rows). **Accept** declares a divergence you consider legitimate, so it stops alerting (it starts no job). Re-sync and Re-merge start a *Table resync* job, and any table can also be re-synced from **Tables**.

**Compare values.** Reconcile counts rows; it can't see a changed value when the row counts match. **Compare values** (Reconcile toolbar for the whole pipeline, or **Compare again** on a table) starts a job that pairs each consolidated-Parquet row with its SQL row by Id and lists the **different** rows, the ones **missing** in SQL and the **extra** ones (rows deleted in F&O are skipped when the pipeline deletes them in SQL) (each counter's tooltip shows up to 20 Ids). It reads whole tables: large tables take time and memory, so it is started by hand. Enum label columns are not compared (they are derived at load time).

**Restore a Parquet version.** Each run that changes a table uploads its new consolidated Parquet under another name, checks that it arrived complete, and only then replaces the previous one, which is kept as a **version** (for the *Days of Parquet versions* in Settings, 7 by default, and the most recent one is always kept); the list shows which run replaced each version. If the new one has fewer rows than the previous one, the job warns and sends an alert. In Parquet → the table's **Versions** button → **Restore** → confirm: that version becomes the consolidated file again, the current one is kept as another version (so it can be undone), and a *Re-sync table* job reloads the table in SQL from it. The pipeline goes back to that point, so the next run brings back the later rows if their snapshots are still in the lake. You can't restore while a job is running. Versions live in `_versions/` next to the Parquet; resetting the pipeline deletes them, and they are not copied when you change where the Parquet is kept.

**Automatic correction.** With **Settings → Automatic correction** on, when a run (or a value comparison) ends with tables above the divergence threshold or with value differences, Sluice starts a re-sync (reload) of those tables by itself, in a single job. One attempt only: if the difference persists, it alerts and does not retry. Accepted tables are not corrected. The Reconcile history marks these re-syncs as *Automatic*.

**Get a report.** Jobs → **Actions** → **Report** (PDF) or **JSON**, per job. There is no separate Reports screen.

For what each state and term means, see [Reference](04-reference.md).
