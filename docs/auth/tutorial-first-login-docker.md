# Tutorial: first login with Docker

You will deploy Sluice Portal in a Linux container, open the login page, and reach the dashboard. By the end you have a running portal on port **58472** with authentication enabled.

## What you'll need

- Docker Engine 24+ (or Docker Desktop with Linux containers)
- Git clone of this repository
- About 5 minutes

## Step 1: Configure keys

Copy the environment template and set the authentication key. Use a real random string, not the `changeme-ui` placeholder (Production rejects it).

```bash
cd Sluice
cp .env.example .env
```

Edit `.env`:

```bash
UI_API_KEY=your-secure-ui-key-here
SLUICE_HTTP_PORT=58472
```

## Step 2: Start the portal

On Linux or WSL2:

```bash
docker compose -f docker-compose.linux.yml up -d --build api
```

Wait until the API container is healthy:

```bash
curl http://localhost:58472/health
```

Expected output:

```json
{"status":"ok"}
```

## Step 3: Sign in

1. Open [http://localhost:58472/login](http://localhost:58472/login) in your browser.
2. Enter the same value you set for `UI_API_KEY` in `.env`.
3. Click **Sign in**.

You should land on the dashboard. The header shows **Sluice Portal** and the health indicator should read **Online**.

If you see an error, check the key matches `.env` exactly (no extra spaces).

## What you built

You now have:

- A running Sluice Portal API container on port `58472`
- Authentication active (`UI_API_KEY`)
- An operator session via the `sluice_session` cookie

**Next steps:**

- Add workers for the full pipeline: `docker compose -f docker-compose.linux.yml up -d --build`
- Configure ADLS and SQL connections in the portal under **Sources** and **Destinations**
- Read [How to sign in and call the API](howto-login-and-api-access.md) for curl and script access
- See [Authentication reference](reference-dual-auth.md) for endpoints and path rules

## Related

- [Installation guide](../guia-usuario/03-installation.md)
- [Authentication docs index](README.md)