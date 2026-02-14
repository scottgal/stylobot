# Running Locally

This guide is for getting a local environment running fast, then validating detection output.

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
- `https://localhost:7038/docs`
- `https://localhost:7038/_stylobot` (dashboard)
- `https://localhost:7038/Home/LiveDemo`

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

## Quick simulation checks

```bash
curl -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0" https://localhost:7038/bot-detection/check -k
curl -A "curl/8.4.0" https://localhost:7038/bot-detection/check -k
```

## What good output looks like

- Browser-like request: lower probability, lower risk band
- Bot-like request: higher probability and stronger detector reasons
- Dashboard feed: detections appear in near real time

## Local troubleshooting

- If frontend styles look stale, run `npm run build` in `src/Stylobot.Website`
- If `/_stylobot` is empty, generate traffic against `/bot-detection/check`
- If build fails due locked binaries, stop running `dotnet` processes and retry
