# Running Locally

This guide is intentionally simple.

## Prerequisites

- .NET SDK 10
- Node.js 22+

## Start the site

From repo root:

```bash
dotnet run --project mostlylucid.stylobot.website/src/Stylobot.Website/Stylobot.Website.csproj
```

Open:

- `https://localhost:7038`
- `https://localhost:7038/_stylobot` (dashboard)

## Optional frontend watch mode

```bash
cd mostlylucid.stylobot.website/src/Stylobot.Website
npm install
npm run watch
```

## Quick health checks

```bash
curl https://localhost:7038/health -k
curl https://localhost:7038/bot-detection/health -k
```

## Quick bot simulation

```bash
curl -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0" https://localhost:7038/bot-detection/check -k
curl -A "curl/8.4.0" https://localhost:7038/bot-detection/check -k
```
