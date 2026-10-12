# Sluice Portal — User Guide

Sluice Portal is the control and observability panel for a data pipeline that moves data from **Azure Synapse Link (ADLS Gen2)** into **SQL Server**, through durable **Parquet** files:

```
ADLS Gen2 (Synapse Link)  →  consolidated Parquet  →  SQL Server (MERGE)  →  Reconciliation
```

## Contents

| Section | What you'll find |
|---|---|
| [Introduction](01-introduction.md) | What the portal is, who it's for, and the key concepts at a glance. |
| [Functionality](02-functionality.md) | How the pipeline works, what each page does, and the common tasks. |
| [Installation](03-installation.md) | Requirements, deployment, settings, and connection setup. |
| [Reference](04-reference.md) | Glossary, states, security, observability, and retention. |

**Authentication** (UI access key with a session cookie, or open access with `AUTH_MODE=none`) is documented in depth under [`docs/auth/`](../auth/README.md) — tutorial, how-to, reference, and explanation quadrants.

This guide is also available inside the portal under **Help** (`/ayuda`).
