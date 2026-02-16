# Quick Start

Hands-on local runbook for the current repository.

## Prerequisites

- .NET SDK `10.0`
- `curl`

Optional:

- Docker + Docker Compose
- `jq` for prettier JSON output

## 1. Run the Demo App

From repo root:

```bash
dotnet run --project Mostlylucid.BotDetection.Demo
```

Expected local endpoints:

- `http://localhost:5080`
- `https://localhost:5001` (when HTTPS profile is used)

Primary pages:

- `http://localhost:5080/bot-test`
- `http://localhost:5080/SignatureDemo`

## 2. Verify Bot Detection API

```bash
curl http://localhost:5080/bot-detection/health
curl http://localhost:5080/bot-detection/stats
curl http://localhost:5080/bot-detection/check
```

## 3. Simulate Different Clients

Human-like request:

```bash
curl -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" http://localhost:5080/bot-detection/check
```

Bot-like request:

```bash
curl -A "curl/8.4.0" http://localhost:5080/bot-detection/check
```

Use demo test-mode simulation header:

```bash
curl -H "ml-bot-test-mode: googlebot" http://localhost:5080/bot-detection/check
curl -H "ml-bot-test-mode: malicious" http://localhost:5080/bot-detection/check
curl -H "ml-bot-test-mode: nikto" http://localhost:5080/bot-detection/check
```

## 4. Test Protected Endpoints

```bash
curl http://localhost:5080/api/protected
curl http://localhost:5080/api/humans-only
curl http://localhost:5080/api/allow-search-engines
curl http://localhost:5080/api/strict-protection
```

## 5. Dashboard & Diagnostics

The demo app includes the StyloBot Dashboard at:

- **Dashboard UI**: `http://localhost:5080/stylobot/`
- **Summary API**: `http://localhost:5080/stylobot/api/summary`
- **Diagnostics API**: `http://localhost:5080/stylobot/api/diagnostics` (rate-limited: 10 req/min)

```bash
# Dashboard summary stats
curl http://localhost:5080/stylobot/api/summary | jq .

# Full diagnostics (summary + top bots + detections + signatures)
curl http://localhost:5080/stylobot/api/diagnostics | jq .

# Filter detections
curl "http://localhost:5080/stylobot/api/detections?isBot=true&limit=20" | jq .

# Signatures
curl http://localhost:5080/stylobot/api/signatures | jq .
```

Rate limit headers on diagnostics: `X-RateLimit-Limit`, `X-RateLimit-Remaining`. Returns HTTP 429 when exceeded.

## 6. Signature Streaming UI

Open `http://localhost:5080/SignatureDemo` and generate requests from the commands above.
You should see live signature rows and detector contribution details as traffic arrives.

## 7. Run Gateway Locally (Optional)

```bash
dotnet run --project Stylobot.Gateway
```

Gateway default admin endpoint:

```bash
curl http://localhost:8080/admin/health
```

If you provide `ADMIN_SECRET`, include:

```bash
curl -H "X-Admin-Secret: your-secret" http://localhost:8080/admin/health
```

## 8. Docker Compose (Optional)

Create `.env` and set required secrets:

```bash
cp .env.example .env
```

Start minimal stack (`docker-compose.yml`):

```bash
docker compose up -d
```

Start full demo stack (`docker-compose.demo.yml`):

```bash
docker compose -f docker-compose.demo.yml up -d
```

## Troubleshooting

- If build fails with SDK errors, confirm `dotnet --version` is `10.x`.
- If HTTPS warnings appear on `https://localhost:5001`, use HTTP on `5080` for local verification.
- If `/admin/*` returns `401`, you likely configured `ADMIN_SECRET` and must send `X-Admin-Secret`.
- If no signatures appear in the UI, ensure requests are hitting the same demo instance and refresh the page.

## Next Reading

- `README.md`
- `DOCKER_SETUP.md`
- `docs/README.md`
- `Mostlylucid.BotDetection/docs/quickstart.md` — Full integration guide (NuGet, attributes, action policies, dashboard setup)
- `Mostlylucid.BotDetection/docs/api-reference.md`
