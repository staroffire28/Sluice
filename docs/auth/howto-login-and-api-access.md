# How to sign in and call the API

You will authenticate to Sluice Portal as an operator, from the browser or from a script. Both use the same shared `UI_API_KEY`.

## Prerequisites

- Portal running and reachable (default `http://localhost:58472`)
- `UI_API_KEY` set in `.env` (non-placeholder value in Production)
- For Docker: see [Installation](../guia-usuario/03-installation.md)

## Sign in with the browser

1. Open `http://localhost:58472/login` (or your host + `SLUICE_HTTP_PORT`).

2. Enter your `UI_API_KEY` in the access key field and click **Sign in**.

3. Confirm you land on the dashboard (`/`) and the header health pill shows **Online**.

**Logout:** use the sign-out button in the top-right header (posts to `/auth/logout` with antiforgery protection). It signs out every session, and portal tabs already open in other browsers or devices go back to `/login` on their next click or refresh, without running that action.

## Call `/api/*` from a script (JSON login)

Automation and smoke scripts use JSON login; antiforgery is not required for `application/json`.

```bash
BASE=http://localhost:58472
UI_KEY=your-ui-key

# Login (save cookie; 302 = signed in, 401 = wrong key)
curl -c /tmp/sluice-cookies.txt -X POST "$BASE/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"Key\":\"$UI_KEY\"}" \
  -o /dev/null -s -w "login: %{http_code}\n"

# Authenticated API call
curl -b /tmp/sluice-cookies.txt "$BASE/api/status"

# Calls that change something (POST, PUT, PATCH, DELETE) also send X-Sluice-Client (any value);
# without it the portal answers 403, so another site cannot act with the session cookie.
curl -b /tmp/sluice-cookies.txt -H "X-Sluice-Client: script" -X POST "$BASE/api/pipelines/<id>/run"
```

Outside Development the session cookie is `Secure`: with plain `http`, curl sends it back to `localhost` as in the example; from another machine use `https`.

PowerShell (same pattern as `scripts/docker-smoke.ps1`). Outside Development PowerShell never sends the `Secure` session cookie over plain `http`, so the example copies it without `Secure` into the session:

```powershell
$base = "http://localhost:58472"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-WebRequest -Uri "$base/auth/login" -Method Post -UseBasicParsing `
  -ContentType "application/json" -Body (@{ Key = $env:UI_API_KEY } | ConvertTo-Json) `
  -WebSession $session | Out-Null
$cookie = $session.Cookies.GetCookies([Uri]($base -replace '^http:', 'https:'))["sluice_session"]
$session.Cookies.Add([Uri]$base, (New-Object System.Net.Cookie "sluice_session", $cookie.Value, "/"))
Invoke-RestMethod -Uri "$base/api/orchestration/mode" -WebSession $session
Invoke-RestMethod -Uri "$base/api/pipelines/<id>/run" -Method Post `
  -Headers @{ 'X-Sluice-Client' = 'script' } -WebSession $session
```

## Workers (distributed Docker)

Workers need no authentication setup. They do not call the portal over HTTP; they claim tasks from the queue on the shared `DATA_DIR` volume. Only the API/orchestrator service needs the key:

```yaml
environment:
  UI_API_KEY: ${UI_API_KEY}
```

A worker serves neither the UI nor `/api/*`: it only answers `/health`, which the container healthcheck uses and which is anonymous:

```bash
curl http://localhost:58472/health
```

## Rotate keys in production

1. Generate a new random string for `UI_API_KEY`.
2. Update `.env` on the host.
3. Redeploy containers (`docker compose up -d` or your redeploy script).
4. Distribute the new UI key to operators.

Rotating `UI_API_KEY` and restarting signs out every open session: cookies issued with the previous key stop working, so everyone (and any script) signs in again with the new key.

## Verification

| Check | Expected |
|-------|----------|
| `GET /health` | `{"status":"ok","scheduler":"ok","disk":"ok","config":"ok","alerts":"ok"}` |
| `GET /api/status` without cookie | `401 Unauthorized` |
| `GET /api/status` after JSON login | `200` with JSON body |

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Portal won't start in Production | Missing or placeholder key | Set a real `UI_API_KEY` in `.env` |
| Form login shows "session expired" | Missing/expired antiforgery token | Reload `/login` and submit again |
| `401` on API after login | Cookie not sent (outside Development it is `Secure`) | curl: use `-b cookies.txt` and `localhost` (or `https`). PowerShell: copy the cookie without `Secure` into the `-WebSession`, as in the example above |
| Cookie not marked Secure behind proxy | Proxy not forwarding proto | Ensure `X-Forwarded-Proto: https` reaches the app (and, if `KNOWN_PROXIES` / `KNOWN_NETWORKS` are set, that the proxy is listed) |

## Related

- [Authentication reference](reference-dual-auth.md)
- [Why this authentication design](explanation-dual-auth.md)
- [Tutorial: first login with Docker](tutorial-first-login-docker.md)
