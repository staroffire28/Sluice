# Why this authentication design

Sluice Portal sits on a private network and orchestrates a data pipeline. Only **operators** (humans in the browser, or their scripts) talk to it over HTTP. **Workers** (containers that claim and run tasks) never call the portal: they share the task queue as files on the `DATA_DIR` volume. One shared UI key is therefore enough to protect every HTTP surface.

## The problem

Originally the UI and `/api/*` were open to anyone who could reach the port. That was acceptable only when the portal was assumed to live on a trusted LAN with no exposure.

Once the portal manages connection secrets, pipeline triggers, and job history, an open UI is a real risk: anyone on the network could read masked credentials metadata, start runs, or change settings.

## The approach

```
                    ┌─────────────────────────────────────┐
                    │           Sluice Portal             │
  Operator ────────►│  /login  →  sluice_session cookie   │────► UI + /api/*
  (browser)        │         (UI_API_KEY at login)       │
                    └──────────────────┬──────────────────┘
                                       │ enqueue / read status
                                       ▼
                           DATA_DIR volume (task queue)
                                       ▲
                                       │ claim / complete (files, no HTTP, no key)
  Worker ──────────────────────────────┘
  (container)
```

**UI path:** The operator enters `UI_API_KEY` once on `/login`. The server issues an HMAC-signed cookie (`sluice_session`) valid for `SESSION_EXPIRY_HOURS` (default 12). Subsequent page loads and `/api/*` calls send the cookie automatically.

**Workers:** Each worker container mounts the same `DATA_DIR` volume and claims tasks from the file-based queue directly. They need no portal credential.

**Fail-closed production:** Outside `Development`, the app refuses to start without `UI_API_KEY` and rejects the placeholder value `changeme-ui`.

### Why there is no worker key

Earlier versions also had internal HTTP task endpoints (claim, heartbeat, complete, fail) protected by a second, worker-only secret. Nothing ever called them: workers already share the queue on the `DATA_DIR` volume. Those endpoints were removed, and with them the worker key — there was no remaining attack surface worth keeping and rotating a second secret for. Only `/internal/health` remains, as an anonymous Docker healthcheck.

## Session model

Sessions are **HMAC tokens**, not rows in a database:

```
token = {epoch}.{unixExpiry}.{HMAC-SHA256(signingKey, epoch.expiry)}
```

The signing key is random and lives in `DATA_DIR/auth/session.json` — it is not `UI_API_KEY`, so a captured cookie cannot be used to brute-force the UI key offline. Validation checks epoch, expiry and signature. Signing out increments the epoch: every token issued before (any browser, any script) stops working.

This fits a single shared operator key and a small team. It avoids Redis or DB session tables on a portal that already persists everything as JSON files.

## CSRF and automation

Browser forms (`/login`, logout in the header) include an antiforgery token. JSON `POST /auth/login` skips antiforgery so smoke scripts and Playwright fixtures can authenticate without parsing HTML.

Form logout without a valid token returns `400`, preventing cross-site logout tricks.

## TLS and reverse proxies

Outside Development the session cookie is always `Secure` and the app sends HSTS: the portal is expected behind a TLS-terminating proxy, so plain-HTTP access cannot keep a session (browsers treat `localhost` as secure, scripts do not). The app enables `ForwardedHeaders` (`X-Forwarded-Proto`) so HSTS and `IsHttps` reflect the proxy's scheme.

Set `KNOWN_PROXIES` / `KNOWN_NETWORKS` to restrict which proxies may supply `X-Forwarded-For` / `X-Forwarded-Proto`. Without them only loopback is trusted (the app logs a warning at startup): a client that reaches the app directly cannot spoof its IP, but behind a proxy in another container every user shares the proxy's IP in the login throttle.

## Trade-offs

| Choice | Benefit | Cost |
|--------|---------|------|
| Shared UI key (not per-user accounts) | Simple deploy, no user DB | No audit per person |
| HMAC cookie + revocation epoch | No per-session store; logout revokes stolen cookies | Any logout signs out everyone (including running scripts) |
| Workers use the shared `DATA_DIR` queue, no HTTP API | No worker secret to rotate or leak | Workers must mount the same volume as the portal |
| JSON login without CSRF | Easy automation | Brute force limited to 10 failures per client IP per 15 min; behind a proxy not listed in `KNOWN_PROXIES` / `KNOWN_NETWORKS`, all users share one IP and one throttle |
| `/internal/health` exempt | Docker healthcheck without secret | Endpoint reveals process is up (no sensitive data) |

## Alternatives considered

Full user accounts (Entra ID, per-user RBAC) were evaluated. That was deferred: operators are a small trusted group, and the portal has no user database. A shared UI key plus session cookies restores protection without a identity migration.

## Related

- [Authentication reference](reference-dual-auth.md)
- [How to sign in and call the API](howto-login-and-api-access.md)
