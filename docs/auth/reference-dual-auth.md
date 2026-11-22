# Authentication — reference

Sluice Portal protects **operator access** (UI + management API) with a single shared key, `UI_API_KEY`, exchanged at login for a session cookie. Workers do not call the portal over HTTP (they share the task queue on the `DATA_DIR` volume), so there is no worker credential.

## Environment variables

| Variable | Required (Production) | Default | Effect |
|----------|----------------------|---------|--------|
| `UI_API_KEY` | Yes, unless `AUTH_MODE=none` | — | Shared key for `/login` and session creation. When set, UI and `/api/*` require a valid `sluice_session` cookie. A key shorter than 32 characters logs a warning at startup. |
| `AUTH_MODE` | No | `key` | `key` requires `UI_API_KEY`. `none` is open access on purpose: no key, `/api/*` open, and the landing (`/login`) offers **Enter** instead of a key field. Any other value stops startup. |
| `SESSION_EXPIRY_HOURS` | No | `12` | Hours until the HMAC session cookie expires. A value below 1 stops startup. |
| `KNOWN_PROXIES` / `KNOWN_NETWORKS` | No | — | Reverse proxies (IPs / CIDR networks) trusted for `X-Forwarded-For` / `X-Forwarded-Proto`. When unset, only loopback is trusted and a warning is logged at startup. |
| `ALLOWED_HOSTS` | Yes with `AUTH_MODE=none` reached by a DNS name | — | Comma-separated host names (exact or `*.example.com`) accepted besides `localhost` and any IP address; any other `Host` gets `400` (blocks DNS rebinding). Always enforced with `AUTH_MODE=none`; with `AUTH_MODE=key`, only when set. Checks the `Host` the proxy sends, not `X-Forwarded-Host`. An invalid value stops startup. |

**Production startup rules** (when `ASPNETCORE_ENVIRONMENT` is not `Development`):

- Startup fails if `UI_API_KEY` is missing, unless `AUTH_MODE=none`.
- Startup fails if `UI_API_KEY` is the placeholder `changeme-ui`.
- Startup fails if `AUTH_MODE=none` and `UI_API_KEY` are both set (contradictory).

**Open access** (`AUTH_MODE=none`, any environment):

- UI and `/api/*` are open; a warning is logged at every startup and the header shows an **Open access** badge on every page.
- The first page of each browser session redirects to the landing (`/login`); **Enter** (`GET /auth/enter`) sets the non-secret `sluice_welcome` cookie and goes to `/`. It is not a credential.

**Development** (a missing key logs a warning only):

- No `UI_API_KEY` → UI and `/api/*` are open; the landing shows **Enter**, without the per-session redirect.

## HTTP endpoints

### `POST /auth/login`

Authenticates with `UI_API_KEY` and sets the session cookie.

| Content-Type | Antiforgery | Body | Success | Failure |
|--------------|-------------|------|---------|---------|
| `application/x-www-form-urlencoded` | Required (`AntiforgeryToken` in form) | `key=<UI_API_KEY>` | `302` → `/` | `302` → `/login?error=…` |
| `application/json` | Not required (automation) | `{ "Key": "<UI_API_KEY>" }` | `302` → `/` | `401` / `400` / `429` |

Query error codes on form login redirect: `invalid`, `required`, `csrf`, `throttled`.

After 10 failed attempts (wrong or empty key, or a form without a valid antiforgery token) from the same client IP within 15 minutes, further attempts are rejected (`429`, or `?error=throttled` for the form) until the window expires — even with the correct key. Successful logins do not count and reset the counter. Logins, failed attempts and the first throttled attempt of each window are recorded in the activity log as `auth.login` (never the key); once a client is throttled, its further attempts are not recorded. Behind a reverse proxy the client IP comes from `X-Forwarded-For`; set `KNOWN_PROXIES` / `KNOWN_NETWORKS` so only your proxy can supply it.

### `POST /auth/logout`

With a valid session, revokes **all** sessions (every browser, every script) and clears `sluice_session`; without one (anonymous, or an expired cookie) it only clears the cookie and revokes nothing, so a caller holding the public `/login` antiforgery token cannot sign everyone out. Form POST with antiforgery token. Returns `302` → `/login`. Recorded in the activity log as `auth.logout` only if the request carries a valid session, whether it succeeds or is rejected for a missing token.

### `GET /login`

Login page (no session required). Exempt from auth middleware.

### `GET /health`

Public health check. On the orchestrator it returns `{ "status": "ok", "scheduler": "ok", "disk": "ok", "config": "ok", "alerts": "ok" }`, with `status` `degraded` (still HTTP 200) when the scheduler stopped polling, the `DATA_DIR` volume runs low, a pipeline or connection file cannot be read (`config: "unreadable"`) or the last delivery to the configured alert webhook failed (`alerts: "failing"`); 503 when `DATA_DIR`, or its `logs` folder where the activity log is written, is not writable. A worker (`WORKER_STAGE`) returns `{ "status": "ok" }`. Used by the Docker healthcheck, and the only route a worker answers.

### `GET /internal/health`

Anonymous liveness endpoint: `{ "status": "ok" }` while `DATA_DIR` and its `logs` folder are writable (503 otherwise), without the scheduler and disk indicators of `/health`. Requires no key or session. Orchestrator only.

## Session cookie

| Property | Value |
|----------|-------|
| Name | `sluice_session` |
| Format | `{epoch}.{expiryUnix}.{hmacSha256Hex}` |
| Signing key | `HMAC-SHA256(randomKey, UI_API_KEY)`, computed in memory; `randomKey` is a 32-byte key generated on first start and stored in `DATA_DIR/auth/session.json` |
| `HttpOnly` | `true` |
| `SameSite` | `Strict` |
| `Secure` | Always outside Development (the portal runs behind a TLS-terminating proxy); in Development only when `Request.IsHttps` |
| `Path` | `/` |

There is no per-session store. `DATA_DIR/auth/session.json` holds the random key and an `epoch`, never anything derived from `UI_API_KEY`: logout increments the epoch, so every token issued before stops validating, and rotating `UI_API_KEY` and restarting signs out every open session. Outside Development the app also sends HSTS.

Open Blazor pages (circuits) follow the same rules: each one keeps the token it was opened with and validates it again before handling anything the browser sends (UI events, JS interop, navigation, render acknowledgements). When it no longer validates, the event is dropped and the page reloads to `/login`. A page that only refreshes on a server timer gets there on its next refresh (the browser acknowledges it), so it may show that one refresh first.

## Path routing (`DualAuthMiddleware`)

```
Request
  │
  ├─ exempt path? ─────────────────────────────► allow
  ├─ /api/* ? ─────── UI session valid? ───────► allow / 401
  └─ other ───────── UI session valid? ────────► allow / 302 /login (GET/HEAD) or 401
```

### Exempt paths (no auth)

- `/health`, `/health/ready`, `/internal/health`
- `/login`, `/auth/login`, `/auth/logout`
- `/culture/*`
- `/_framework*`, `/_content*`
- `/favicon.png`

Any other path — including any other `/internal/...` path — requires a valid UI session.

### Credential matrix

| Path prefix | UI session (`sluice_session`) |
|-------------|-------------------------------|
| `/api/*` | Required (401 without it) |
| UI pages (`/`, `/jobs`, …) | Required (GET/HEAD redirect to `/login`) |
| Exempt paths (above) | Not required |

## Examples

### JSON login (curl)

```bash
curl -c cookies.txt -X POST http://localhost:58472/auth/login \
  -H "Content-Type: application/json" \
  -d '{"Key":"your-ui-key"}' \
  -o /dev/null -w "%{http_code}\n"
```

Prints `302` when signed in, `401` for a wrong key.

### Authenticated API call

```bash
curl -b cookies.txt http://localhost:58472/api/status
```

### Internal health (anonymous)

```bash
curl http://localhost:58472/internal/health
```

## Related

- [How to sign in and call the API](howto-login-and-api-access.md)
- [Why this authentication design](explanation-dual-auth.md)
- [Tutorial: first login with Docker](tutorial-first-login-docker.md)
