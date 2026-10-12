# Sluice Portal — Authentication

Sluice Portal uses a **single shared UI key**: operators sign in with `UI_API_KEY` and get a session cookie that protects both the UI and `/api/*`. Pipeline workers do not call the portal over HTTP — they share the task queue on the `DATA_DIR` volume — so they need no credential of their own.

## Documentation map (Diataxis)

| Document | Quadrant | Reader goal |
|----------|----------|-------------|
| [Tutorial: first login with Docker](tutorial-first-login-docker.md) | Tutorial | Go from zero to a signed-in dashboard in a few steps |
| [How to sign in and call the API](howto-login-and-api-access.md) | How-to | Accomplish a specific auth task (browser, curl, scripts, key rotation) |
| [Authentication reference](reference-dual-auth.md) | Reference | Look up endpoints, env vars, cookies, and path rules |
| [Why this authentication design](explanation-dual-auth.md) | Explanation | Understand the design, trade-offs, and security model |

## Related docs

- [User guide — Installation](../guia-usuario/03-installation.md) (deploy + `.env` setup)
- [User guide — Reference / Security](../guia-usuario/04-reference.md) (glossary + retention + alerts)
- [README — Authentication](../../README.md#autenticación) (quick summary in the main README)