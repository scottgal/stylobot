# StyloBot Gateway — API Service Protection

Protects REST APIs from:

- **Credential stuffing** on `/api/auth/**` and `/api/login` — automated login attempts
  using leaked username/password lists. Blocked at 50% confidence.
- **API key probing** — bots trying to discover valid API keys through brute force.
- **Scraping** — bots consuming your API endpoints at machine speed.

## API-Specific Detection

Pure API traffic behaves differently from browser traffic: no cookies, no page-load
sequences, no referrer headers. The `api` policy skips behavioral analysis detectors
that rely on page-load patterns (CacheBehavior, AdvancedBehavioral) to avoid false
positives on legitimate API clients.

Auth endpoints get the `auth-strict` policy, which re-enables behavioral analysis
since credential stuffing has a distinctive pattern: many rapid sequential POST
requests to the same endpoint.

## Quick Start

```bash
cp .env.example .env   # set ADMIN_SECRET
docker compose up -d
```

Replace the stub `api` service with your actual API container.

## What Your API Backend Receives

```
X-Bot-Detected: true
X-Bot-Confidence: 0.87
X-Bot-Detection-RiskBand: High
X-Bot-Type: CredentialStuffer
X-Is-Malicious-Bot: true
```

Your API can read these headers to apply additional rate limiting:

```javascript
// Express.js
app.post('/api/auth/login', (req, res) => {
  if (req.headers['x-is-malicious-bot'] === 'true') {
    return res.status(429).json({ error: 'rate_limited' });
  }
  // ... normal login logic
});
```

## Tuning

- Too many false positives on mobile clients? Raise `ImmediateBlockThreshold` in
  `auth-strict` to `0.65`.
- Machine clients with API keys? Add `X-SB-Api-Key` header to exempt your own
  monitoring and health-check traffic from detection.
