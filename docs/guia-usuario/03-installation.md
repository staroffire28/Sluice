# Installation

The portal is a single .NET 10 app. The same image runs as the orchestrator (`api`, serves the UI and schedules) or as a per-stage worker (`ingest`, `sql`, `reconcile`); the role is set at runtime. All state lives as JSON files under a named volume — there is no database for the portal itself.

There are two container flavors, pick the one your host runs:

- **Linux containers** — `Dockerfile.linux` + `docker-compose.linux.yml`.
- **Windows containers** — `Dockerfile` + `docker-compose.yml`.

## Hardware requirements

The portal is light; the heavy systems (SQL Server, ADLS) are external. These figures are for the host running the containers.

| Profile | vCPU | RAM | Disk |
|---|---|---|---|
| Minimum (PoC / low volume) | 2 | 4 GB | 20 GB |
| Recommended (production) | 4–8 | 16 GB | 100 GB+ SSD |

- **Disk** is dominated by the `pipeline-data` volume (jobs, reports, parquet staging). It is bounded by the retention setting (default 90 days). Use SSD for the queue.
- **RAM** peaks during parquet consolidation (one snapshot at a time). 16 GB is comfortable for parallel pipelines.
- SQL Server and ADLS run elsewhere and are not counted here.

## Prerequisites

- **Docker Engine 24+** with Docker Compose v2.
- **Linux host**: any distro with Docker (Linux containers, the default daemon mode).
- **Windows host**: Docker Desktop in **Windows containers** mode (Windows Server 2022 or Windows 11 + Hyper-V).
- A value for `UI_API_KEY` in `.env` (or `AUTH_MODE=none` for open access without a key; see Environment variables).

Azure/SQL credentials are **not** set here — you configure them in the portal after it starts.

## Try it: demo mode

To see Sluice working without an F&O environment, an Azure storage account or your own SQL Server, run the demo on a machine with Docker (Linux containers):

```bash
docker compose -f docker-compose.demo.yml up --build
```

Then open `http://localhost:58480` and press **Enter**. The demo:

- Starts its own SQL Server (Developer Edition; running it accepts its license) and creates the `SluiceDemo` database.
- Writes **synthetic** Synapse Link data for three F&O tables (`custtable`, `vendtable`, `generaljournalaccountentry`), with enum metadata.
- Creates the source, the destination and the **Demo F&O** pipeline, and runs the first load.
- Every 5 minutes closes a new snapshot with inserts, updates and deletes; the scheduler processes it, so you can watch Incremental, the deletes and Reconcile. Between snapshots the scheduler may also run an Incremental with nothing new; that is expected.

The header shows **Demo** and **Open access**. It is **not for production**: no key, a fixed SQL password unless you set `DEMO_SQL_PASSWORD`, and test data. The portal is published only on this machine (`127.0.0.1`), and SQL Server only to the portal. Remove it with `docker compose -f docker-compose.demo.yml down -v`. Change the port with `DEMO_HTTP_PORT` and the cadence with `DEMO_SNAPSHOT_MINUTES`.

## Install (distributed)

Distributed mode runs one orchestrator (`api`) plus three workers (`ingest`, `sql`, `reconcile`), all sharing the named volume `pipeline-data`.

**1. Get the code and set the key:**

```bash
git clone https://github.com/staroffire28/Sluice.git
cd Sluice
cp .env.example .env          # set UI_API_KEY; optional SLUICE_HTTP_PORT (default 58472)
```

**2. Build and start — Linux:**

```bash
docker compose -f docker-compose.linux.yml up -d --build
```

**2. Build and start — Windows:**

```powershell
docker compose -f docker-compose.yml up -d --build
```

**3. Verify:**

```bash
curl http://localhost:58472/health      # → {"status":"ok"}
```

Then open `http://localhost:58472/login`, sign in with your `UI_API_KEY` (with `AUTH_MODE=none`, press **Enter**), and confirm the header health pill reads **Online**.

**Updating to a new version:** after `git pull`, run the step 2 command again. It rebuilds the image and recreates the containers that changed; the volume (data, queue and keyring) is kept and `UI_API_KEY` still comes from `.env`.

> A **named Docker volume** is required (the task queue uses atomic file renames). Do not replace it with a host bind mount. The encryption keyring also lives on this volume so every replica can decrypt.

> **Unprivileged user (since .NET 10):** the Linux image runs as the `app` user (UID 1654), not root. A new volume already has that owner. If you upgrade from an earlier version, the existing volume belongs to root and the portal cannot write to it (`Access to the path ... is denied`); change its owner once, with the containers stopped:
>
> ```bash
> docker run --rm -v <volume>:/data alpine chown -R 1654:1654 /data
> ```

> **Project name `sluice`:** the compose files set `name: sluice`, so the volume is always `sluice_pipeline-data`, whatever folder you run them from. Before, Compose took the project name from the folder: if you deployed from a folder not named `Sluice`, your data is in `<project>_pipeline-data`, where `<project>` is that folder's name in lowercase (check with `docker volume ls`), and the update would start the stack on a new, empty volume. Before step 2, pick one:
>
> - **Keep the old name:** add `COMPOSE_PROJECT_NAME=<project>` to `.env` (it takes precedence over `name:`).
> - **Move the data to `sluice_pipeline-data`** (Linux containers only: the scripts run an `alpine` helper container; with Windows containers, `docker-compose.yml` or `docker-compose.monolith.yml`, use the first option), with the old stack stopped (`docker compose -p <project> -f <compose file> down`), using the [backup scripts](../BACKUP.md):
>
>   ```powershell
>   .\scripts\backup-data-dir.ps1 -VolumeName <project>_pipeline-data -NoEncrypt
>   docker compose -f <compose file> create   # creates sluice_pipeline-data without starting anything
>   .\scripts\restore-data-dir.ps1 -BackupFile .\backups\pipeline-data-<timestamp>.tar.gz -VolumeName sluice_pipeline-data
>   ```
>
>   Then run step 2 and delete the unencrypted backup. Keep the old volume until you have checked the portal.

## Remote access with TLS

By default the compose files publish the portal only on the machine itself (`127.0.0.1:58472`): no certificate is needed, and you reach it at `http://localhost:58472` or through an SSH tunnel or VPN. To open it to other machines, put TLS in front first. The session cookie is `Secure` outside Development, so over plain HTTP from another machine the login does not work.

**Option A, reverse proxy (recommended).** A proxy such as Caddy or nginx terminates HTTPS and forwards to `api:58472` on the Docker network. With a public DNS name and ports 80/443 reachable, Caddy gets and renews a Let's Encrypt certificate by itself; with an internal name, give it your company's certificate. Example `docker-compose.override.yml`:

```yaml
services:
  caddy:
    image: caddy:2
    ports: ["443:443", "80:80"]
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
  api:
    environment:
      KNOWN_NETWORKS: 172.16.0.0/12   # Docker networks: trust X-Forwarded-For from the proxy
volumes:
  caddy-data:
```

with a `Caddyfile` such as `sluice.example.com { reverse_proxy api:58472 }`. Set `KNOWN_PROXIES` or `KNOWN_NETWORKS` to the proxy: without them `X-Forwarded-For` is only trusted from loopback, and every user shares the proxy's IP in the login throttle.

**Option B, HTTPS in the portal.** Mount a `.pfx` and set `ASPNETCORE_URLS=https://+:58472;http://localhost:58471`, `ASPNETCORE_Kestrel__Certificates__Default__Path=/certs/sluice.pfx` and `ASPNETCORE_Kestrel__Certificates__Default__Password` on the `api` service. The compose healthcheck probes plain HTTP, and the workers do not start until `api` is healthy: override the `api` `healthcheck` with the same `test` as your compose file but the URL `http://localhost:58471/health` (that HTTP listener only answers inside the container). Then publish on all interfaces with `SLUICE_HTTP_BIND=0.0.0.0` in `.env`. You renew the certificate yourself.

## First run

In the portal:

1. **Sources** → add your ADLS connection, then **Test**.
2. **Destinations** → add your SQL Server connection, then **Test**.
3. **Pipelines** → create a pipeline (source + destination), then **Run** (Incremental).
4. Watch it in **Dashboard** / **Jobs**; verify counts in **Reconcile**.

## Scaling

Add replicas per stage when a stage is the bottleneck (usually SQL or ADLS, not the worker):

```bash
docker compose -f docker-compose.linux.yml up -d --build --scale worker-ingest=3 --scale worker-sql=2
```

## Settings (`/settings`)

Edited in the portal, saved to the volume, applied without restart.

| Setting | Controls | Default |
|---|---|---|
| Scheduler poll interval (min) | How often the orchestrator checks ADLS and auto-launches incrementals. | 5 |
| Task lease (s) | How long a claimed task is held before returning to `pending`. | 120 |
| Reconcile divergence threshold (%) | Above this, a job is marked Partial and alerts. | 1.0 |
| Automatic correction | When a run ends with tables above the threshold (or with value differences), re-syncs them automatically, once. | Off |
| History retention (days) | Age past which old jobs and their artifacts are purged. `0` disables it. | 90 |
| Days without data to flag a table | In **Tables**, *Last data* is highlighted when the last load with rows is older (for example, a table removed from the Synapse Link profile). `0` disables it. | 7 |
| Source snapshot retention (days) | Age past which already-processed CSV folders in the Synapse Link container are purged. `0` disables it. | 30 |
| Snapshot purge dry run | Logs what would be deleted without deleting anything. | On |
| Require Parquet before purging | Skips the snapshot purge if any consolidated Parquet of the pipeline is missing. | On |
| Alerts | Enable and webhook URL (e.g. an Azure Logic App); **Send test** checks the URL. | Off |
| Hours behind before alerting | Alerts when a closed snapshot has waited longer than this on a pipeline with the scheduler on ([details](04-reference.md#observability-and-alerts)). `0` disables it. | 6 |

## Environment variables

`UI_API_KEY` and `AUTH_MODE` go in `.env`; the rest are set by the compose files.

| Variable | Purpose | Default |
|---|---|---|
| `UI_API_KEY` | Shared key for UI + `/api/*` (login page + session cookie). Required unless `AUTH_MODE=none`. | — |
| `AUTH_MODE` | `key` requires `UI_API_KEY`. `none` is open access on purpose: the landing offers **Enter** without a key, the API is open, the header shows **Open access** and a warning is logged at startup. Use it only on an internal network or behind an authenticating proxy. With `none` leave `UI_API_KEY` empty: both together refuse to start. | `key` |
| `EXECUTION_MODE` | `local` or `distributed`. | set by compose |
| `DATA_DIR` | JSON persistence root (`/data` Linux, `C:/data` Windows). | set by compose |
| `WORKER_STAGE` | Stage a worker processes (`ingest`, `sql` or `reconcile`; any other value stops startup); absence = orchestrator. | set by compose |
| `KNOWN_PROXIES` / `KNOWN_NETWORKS` | Comma-separated IPs and CIDR networks of the trusted reverse proxy for `X-Forwarded-For`/`Proto`. Without them it is only trusted from loopback; behind a proxy in another container set them, or every user shares the proxy's IP in the login throttle (a warning is logged at startup). An invalid value stops startup. See *Remote access with TLS*. | empty |
| `HEARTBEAT_MAX_FAILURES` | Heartbeat failures before releasing a task. | 3 |
| `CHANGELOG_STALL_HOURS` | Hours without changelog update before alerting. | 16 |
| `LOCAL_SOURCE_ROOT` | Demo and tests only: enables the *Local (UAT)* source type, which reads local folders instead of ADLS. **Never used in production**; only `docker-compose.demo.yml` sets it. | — |
| `DEMO_MODE` | Demo mode (`docker-compose.demo.yml`): generates synthetic F&O data in `LOCAL_SOURCE_ROOT` and creates the demo database, connections and pipeline. Needs `LOCAL_SOURCE_ROOT` and `DEMO_SQL_PASSWORD`. **Never in production.** | `false` |
| `DEMO_SQL_PASSWORD` | Password of the demo SQL Server `sa` user. | — |
| `DEMO_SNAPSHOT_MINUTES` | Minutes between synthetic snapshots in demo mode. | 5 |

Alerts are configured in **Settings** (`/settings`): enable alerts and paste your Azure Logic App HTTP trigger URL. If you don't have the Logic App yet, **Settings → Email alerts (Logic App) → Download package** gives you a ready-to-deploy one that sends each alert as an Office 365 email (follow its `README.md`).

## Connections

Created in `/sources` and `/destinations`. Fill the `Config` fields; secrets are encrypted at rest and shown as `***`. If you change where a connection points (the SQL `host`, or the ADLS `account`, `tenant_id` or `client_id`), type its secret again: the saved one is not sent to a different target. While a pipeline that uses the connection has a job running, Sluice refuses to change where it points (also its `database`, `container`, `parquet_container` or where it keeps the Parquet). Changing the type of an existing connection does not carry over its previous configuration: only the fields of the new type are saved.

- **Source ADLS:** `account`, `container`, `parquet_container`, `tenant_id`, `client_id`, `client_secret`. The account and container names must be valid Azure Storage names (lowercase letters and digits; containers may also use hyphens).
- **Where to keep the Parquet** (per source): in an **ADLS container** (`parquet_container`, the default) or on the **portal disk** (`DATA_DIR/parquet`, the volume the portal and the workers already share). The portal disk needs no Parquet container and no write access to ADLS, but the consolidated Parquet of every table is stored there: size the `DATA_DIR` volume for it (plus its earlier versions, see *Consolidated Parquet versions* in the reference) and include it in your backups. When you change it, Sluice copies the consolidated Parquet to the new place before using it and keeps the old copy; if the copy fails, the source stays as it was.
- **Destination SQL:** `host`, `database`, `user`, `password`, **Trust the server certificate** (`trust_server_certificate`, off by default: the server certificate is validated; tick it only for a SQL Server with a self-signed certificate you cannot replace. Destinations created before this option keep trusting it), and `sql_version` (SQL Server version, default 2022). You don't need to pick the version: **Test connection** in the form detects it from the server and sets it (without saving), and **Test** from the row fixes the saved one if it doesn't match. A full `connection_string` is used only if the connection already has one; the form has no field for it.

Use **Test connection** in the form (source or destination) to validate against the real ADLS/SQL before saving, or **Test** from the row for a saved connection, before running a pipeline. For a source, the test checks the account, the container and the Synapse Link changelog. SQL statements of a pipeline have no time limit by default; a `connection_string` with its own `Command Timeout` is respected.
