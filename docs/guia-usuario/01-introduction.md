# Introduction

Sluice Portal operates and observes a data pipeline that copies data from **Azure Synapse Link (ADLS Gen2)** into **SQL Server**, keeping a durable **Parquet** copy in between:

```
ADLS Gen2 (Synapse Link)
↓
consolidated Parquet
↓
SQL Server (MERGE)
↓
Reconciliation
```

From the portal you create connections, define pipelines, launch runs (manual or scheduled), follow their progress live, and verify that SQL ended up matching the source. The portal itself has no database: all of its state lives as JSON files on a volume (`DATA_DIR`).

## Who it's for

- **Operators** who run the portal day to day: see [Functionality](02-functionality.md).
- **Whoever installs and runs the infrastructure**: see [Installation](03-installation.md).
- **Anyone who needs to know what a term means**: see the glossary in [Reference](04-reference.md).

## Key concepts at a glance

- **Pipeline** — the definition "from this ADLS source to this SQL destination".
- **Job** — one execution of a pipeline.
- **Snapshot** — a Synapse Link export folder in ADLS (a "photo" of the data).
- **Reconcile** — the check that SQL has the same rows as the Parquet.
- **Divergence** — the percentage by which SQL and the source disagree.

Full definitions are in [Reference · Glossary](04-reference.md#glossary).

## Access and language

By default the portal uses a **shared access key** (`UI_API_KEY`): the start page asks for the key and sets a session cookie for the UI and `/api/*`. It can also be deployed with **open access** (`AUTH_MODE=none`): the start page shows **Enter** without a key and the header reads **Open access**; use it only on an internal network. Either way, do not expose its port to the internet. Workers need no key: they share the task queue on the `DATA_DIR` volume.

The portal starts in **English** and can switch to **Spanish** with the language button in the header (top-right corner), next to the light/dark theme one.
