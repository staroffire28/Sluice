# Reference

## Glossary

- **Pipeline** — a source (ADLS) plus a destination (SQL), with a name. Its "scheduler" switch controls only automatic runs; it never blocks a manual run. Its *Rows deleted in F&O* setting (`DeleteMode`) deletes them in SQL (default) or loads them flagged with `IsDelete = 1`.
- **Job** — one execution of a pipeline, with a type, a status, per-table results and a timeline.
- **Job type** — **Incremental** (only new snapshots, the default), **Force** (reload SQL from consolidated parquet — all tables, skips ingest), **Reconcile** (verify counts only), **Diff** (report row deltas), **Table resync** (re-sync one or more tables from the existing Parquet; from Reconcile, Tables or the automatic correction), **Compare values** (compares the Parquet with SQL row by row, by Id; from Reconcile).
- **Snapshot** — a Synapse Link export folder in ADLS (a point-in-time photo of the data).
- **Changelog** — a file Synapse writes naming the folder it is *currently* writing. The portal only consumes folders strictly older than it, so it never reads half-written data.
- **Settled snapshot** — the last fully closed folder (the one just before the changelog's). The last safe-to-consume snapshot.
- **Watermark** — a timestamp marking how far processing has gone; used to deduplicate (the higher watermark wins for the same row).
- **Task / queue** — the atomic unit of work for one stage of one job. States are folders: `pending`, `claimed` (held with a lease), `done`, `error`.
- **Lease / heartbeat** — the lease is a claimed task's temporary hold (default 120 s); the heartbeat renews it. If a worker dies, the lease expires and the task returns to `pending`.
- **Reconcile** — the check that SQL row counts match the consolidated Parquet.
- **Diff** — an informational report of the row delta (source vs destination); does not mark a job Partial.
- **Divergence / threshold** — `|source − dest| / max(source, dest) × 100`. Above the threshold (`ReconDivergencePct`, default 1.0%), the job is marked Partial.
- **Remediation (resync by table)** — fixing a diverging table by re-merging the existing Parquet. **Re-sync** (reload) = upsert + delete orphans (both directions); **Re-merge** = upsert only (when SQL is missing rows).
- **Divergence acceptance (recon-ack)** — declaring a divergence as legitimate. It suppresses the alert while the divergence stays near the accepted value; if it drifts out of band, the acceptance expires and alerting resumes.
- **Connection (Source / Destination)** — the credentials and config for an ADLS source or a SQL destination. Secrets are encrypted and shown as `***`.
- **Worker / stage** — a worker is the process that claims and runs tasks; a stage is one of Ingest, SQL, Reconcile. In distributed mode a worker can be limited to a single stage.
- **Orchestrator** — the node that schedules and purges. In distributed mode only it does so; workers only process their queue.

## Job states

| State | Meaning |
|---|---|
| Running | In progress (being created or processing stages). |
| OK | Finished cleanly, no divergence over the threshold. |
| Partial | A non-accepted table diverges over the threshold (check Reconcile), or some tables failed in ingest or SQL, or some CSV values could not be read with their column type and were loaded as NULL (the table and a warning in the timeline name the column and an example). After a partial ingest the job goes on with SQL and Reconcile: until it finishes it counts as in progress (it can be cancelled, not retried). |
| Error | A stage threw (e.g. SQL unreachable) — check the timeline. |
| Stalled (Blocked) | The source had no safe snapshots yet — not your fault, it will retry. |
| Cancelled | Someone cancelled it; its pending tasks were purged. |

## Task states

| State | Meaning |
|---|---|
| Pending | Queued, waiting to be claimed. |
| Claimed | Held by a worker with an active lease (running). |
| Done | Completed successfully. |
| Error | Failed; the file keeps the error message. |

## Security

The full authentication documentation (tutorial, how-to, reference and explanation) is in `docs/auth/` in the repository.

- **`UI_API_KEY`** protects the UI and management API (`/api/*`). Sign in at `/login` (shared key + session cookie `sluice_session`). Outside Development, the portal refuses to start without it, unless `AUTH_MODE=none` (open access on purpose: the landing offers **Enter** without a key and the header shows **Open access**).
- Workers do not authenticate against the portal: there is no API between them, they share the task queue on the `DATA_DIR` volume.
- **Placeholder key** (`changeme-ui`) is rejected outside Development — set a real value in `.env` before deploy.
- **Form login/logout** require an antiforgery token; JSON `POST /auth/login` (automation/scripts) is exempt.
- **Data Protection** encrypts connection secrets at rest; the keyring lives in `DATA_DIR/keys` on the shared volume.

## Observability and alerts

- **Health**: `GET /health` → `{ "status": "ok" }`.
- **Activity log**: persisted per process under `DATA_DIR/logs`, with `user` and `system` events (`scheduler.run`, `retention.sweep`, …). Filterable in `/logs` (also by From/To dates, whole days in server time) and exportable to CSV (UTF-8, comma separator).
- **Alerts** fire on: a **pipeline job failure** (`Error`), a Partial reconcile, differences in **Compare values**, a scheduler error, a changelog stall (deduplicated), a retention sweep failure, a column type change in the source (Ingest), a possible SQL block (see below), a stalled queue in distributed mode (see `system.alert` below), and a **pipeline that falls behind**: on a pipeline with the scheduler on, a closed snapshot has waited longer than *Hours behind before alerting* (Settings, 6 by default; 0 disables it). It is measured from when the snapshot closed, the initial load doesn't count, it isn't checked while a job runs, and it alerts once per episode. Configure in `/settings` (enable + Logic App HTTP trigger URL). This webhook is the portal's only alert channel (no SMTP); to get alerts by email, **Settings → Email alerts (Logic App)** downloads a ready-to-deploy Azure package: a Logic App that sends each alert as an Office 365 email with the Sluice design (ARM template, `deploy.ps1`, guide and email preview).
- **Webhook delivery**: an HTTP 5xx or a timeout is retried once (a 4xx or a network/DNS error is not). Deduplicated alerts (changelog stall, pipeline behind, stalled queue) only count as sent once the webhook accepts them; until then the scheduler retries them on each poll (after the first failed delivery in a poll, the rest wait for the next one). The result of the last real delivery (not **Send test**) is shared by the api and workers in `DATA_DIR/alerts/status.json` and shown in **Settings → Alerts → Last delivery**; the activity log records `alert.delivery.failed` and `alert.delivery.recovered` only when delivery starts failing or recovers.
- **Webhook JSON payloads** (POST, `Content-Type: application/json`):
  - `test.ping` — sent by **Send test** in Settings (the URL must be `https://`): `{ "eventType": "test.ping", "timestamp": "...", "message": "Sluice portal alert test" }`
  - `pipeline.failure` — job failed in a stage: ingest or SQL ending in Error or Partial, or a job orphaned by a worker that fell between stages: `{ "eventType": "pipeline.failure", "pipelineId", "pipelineName", "jobId", "jobType", "stage", "status": "error", "message", "timestamp" }`
  - `reconcile.divergence` / `reconcile.values` — Partial reconcile or value differences: `{ "eventType", "pipelineId", "pipelineName", "jobId", "status": "warning", "message", "text", "maxDivergencePct" (divergence only), "entities": [tables], "autoRemediation": "off" | "scheduled" | "unresolved" | "none", "timestamp" }`
  - `system.alert` — scheduler/retention/changelog/source type change/SQL block/pipeline behind/stalled queue (distributed mode: tasks of a stage pending for more than 15 min with no live worker of that stage): `{ "eventType": "system.alert", "text", "message", "timestamp" }`
- **Possible SQL block**: if a SQL statement makes no progress for 1 hour, the job timeline gets a Warning with the entity and phase, and a `system.alert` is sent. The statement is **not** cancelled and the job status does not change; check for blocking sessions in the destination database. The 1-hour threshold is fixed in the code.
- **Dead-man ping**: if `DEADMAN_PING_URL` is set, the scheduler GETs it (10 s timeout) at the end of every cycle whose watchdogs succeed and in which not every evaluated enabled pipeline failed; an HTTP error status counts as a failure. Settings → Alerts shows the result of the last ping.

## Retention

### Local volume (`DATA_DIR`)

A janitor caps the volume's growth. The **job is the unit of retention**: an old job is purged together with its run directory, reports, finished tasks and staging.

- **Always kept**: the last 50 jobs per pipeline (even if old), running jobs, divergence acceptances, and pipeline state.
- **Knob**: `RetentionDays` in `/settings` (default 90). `0` or negative disables purging.
- **Cadence**: every 6 hours, on the orchestrator only; logged as `retention.sweep`. Corrupt or locked files are skipped without breaking the sweep.

### Source snapshots (Synapse Link ADLS container)

A second janitor purges **already processed** CSV folders in the Synapse Link source container. Consolidated parquet (in its dedicated container or on the portal disk) is the recovery artifact.

- **Always kept**: the last 14 processed snapshots (floor K), pending or in-export snapshots, `Changelog/` and CDM metadata.
- **Knob**: `SourceSnapshotRetentionDays` in `/settings` (default 30). `0` or negative disables purging.
- **Cadence**: every 6 hours, on the orchestrator only; logged as `snapshot-retention.sweep`.
- **Recovery after purge**: use **Force** (or `POST /api/pipelines/{id}/reload-from-parquet`, same path) to reload SQL from consolidated parquet. Incremental still reads new CSV snapshots from ADLS.
- **Dry-run**: `SourceSnapshotRetentionDryRun` in `/settings` — **on by default**; simulates without deleting. Check `/snapshots` (Retention column) and activity log `snapshot-retention.dry-run` before disabling.
- **Parquet certification**: `SourceSnapshotRetentionRequireParquet` (default on) — skips purge if any `{pipeline}/{entity}.parquet` is missing.
- **Soft-delete**: enable blob soft-delete on the Azure storage account to recover mistakenly deleted folders (configured outside the portal).

### Consolidated Parquet versions

Before a table's consolidated Parquet is replaced, the previous one is moved (rename, no data copy) to `_versions/{pipeline}/{table}/` with a `.json` holding its schema, the table state and the pipeline's last processed snapshot. The index `_versions/{pipeline}.index.json` keeps that data without the schema so each run does not read every `.json`; if it is deleted or corrupted, it is rebuilt on its own. A **restore point** is that snapshot: restoring it, each table takes the first version archived after it was processed (among those of the same snapshot, the oldest); if it has no such version, or that version already holds later data, and its current state has later data, the table did not exist yet and is archived; otherwise it is already at that point and stays as it is.

- **Knob**: `ParquetVersionDays` in `/settings` (default 7). The points of the snapshots of those days (by snapshot date) are kept, and always the most recent one, with the versions they need; only those are offered on the Parquet page. `0` or negative disables them (publishing is still validated and atomic).
- **Space**: each version is a full copy of the table; a table that changes in every snapshot every 12 h builds up to about 14 versions with 7 days. Pruning runs at the end of each ingest, so during a job the new and the previous consolidated files both exist even with versions disabled.
- **Safeguard**: if a table's consolidated Parquet is missing but it has versions (an interrupted publish or a manual delete), ingest stops with an error instead of rebuilding it from the new snapshots only; restore a point on the Parquet page (*Restore points*).

## Common errors

When a job or a connection test fails with a known error, the portal shows what happened and what to do, with the original message below. Errors are recognized by their code (SQL Server error number, ADLS HTTP status, Entra ID `AADSTS` code), so they are recognized whatever the server language.

| Error | Usual cause | What to do |
|---|---|---|
| Could not sign in to SQL Server (18456) | Wrong destination user or password, or disabled login. | Fix them in Destinations and press Test connection. |
| The destination database does not exist (4060) | The database does not exist or the user cannot access it. | Create it (Sluice does not) or fix its name, and grant access. |
| SQL Server cannot be reached | Host does not resolve, port closed or blocked by a firewall. | Check host and port (1433 by default) and that TCP connections are allowed. |
| SQL Server took too long to respond | Slow network, or a busy or blocked server. | Check load and blocking, then Retry. |
| Deadlock in SQL Server (1205) | Another session locked the same rows. | Retry; if it repeats, check concurrent writers. |
| The destination transaction log is full (9002) | No room left in the log. | Grow the log or disk, or back up the log, then Retry. |
| Duplicate Id in the destination table (2627/2601) | The table already has a row with that Id. | Re-sync the table from Tables (Reload). |
| A value does not fit in the destination column (8152/2628) | A column type was changed by hand. | Restore the type (or drop the table) and run Force. |
| The MERGE received the same Id twice (8672) | The consolidated Parquet has duplicate Ids. | Reset the pipeline and run it. |
| No permission on the ADLS storage (403) | The identity lacks a role on a container. | Give it **Storage Blob Data Contributor** on the Synapse Link and Parquet containers. |
| ADLS container or path not found (404) | Wrong account or container name. | Check them in Sources. |
| The Azure credentials are not valid (`AADSTS…`) | Expired or wrong secret, or wrong tenant/client id. | Create a new secret and update it in Sources. |
| The storage account cannot be found | The account name does not resolve. | Check the name and outbound access to `*.dfs.core.windows.net`. |
