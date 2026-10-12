# Sluice

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache%202.0-blue.svg)](LICENSE)
[![Support on Ko-fi](https://img.shields.io/badge/Ko--fi-support%20Sluice-ff5e5b?logo=ko-fi&logoColor=white)](https://ko-fi.com/staroffire28)

<p align="center">
  <img src="assets/sluice-overview.svg" alt="Sluice as a hydraulic sluice: water released from a header tank (ADLS Gen2) through a sluice gate runs down a flume past a paddle-wheel meter, settles clean in a clarifier (ingest), is pumped through a turbine meter into a cistern (SQL merge), while the portal's centre-zero gauge compares both meters until they match (reconcile)." width="100%">
</p>

Control and observability portal for **Dynamics 365 Finance and Operations** data pipelines:
**ADLS Gen2 (Azure Synapse Link) → Parquet → SQL Server MERGE**.

> **Scope:** Sluice works only with Dynamics 365 Finance and Operations data exported to ADLS Gen2 by Azure Synapse Link. Other sources are not supported.

## Features

- Manage ADLS Gen2 sources and SQL Server destinations. Secrets are encrypted at rest and masked.
- Run pipelines manually (incremental, force, reconcile, diff) or let the scheduler launch them when new snapshots arrive.
- Follow jobs, tables and row counts, and retry failed jobs.
- Reconcile reports with PDF export, and webhook alerts.
- English and Spanish interface.

## Quick start

Requires Docker Engine 24+.

```bash
cp .env.example .env          # set UI_API_KEY
docker compose -f docker-compose.linux.yml up -d --build
```

Open <http://localhost:58472>, sign in with your `UI_API_KEY`, then add a source and a destination under **Sources** and **Destinations**.

Windows containers and the full setup are in the [installation guide](docs/guia-usuario/03-installation.md).

## Documentation

- [User guide](docs/guia-usuario/README.md), also available inside the portal under **Help**.
- [Authentication](docs/auth/README.md)

## Contributing and support

Contributions are welcome: see [CONTRIBUTING](CONTRIBUTING.md). Report vulnerabilities as described in [SECURITY](SECURITY.md).

If Sluice is useful to you, you can support its development on [Ko-fi](https://ko-fi.com/staroffire28).

## License

[Apache License 2.0](LICENSE)
