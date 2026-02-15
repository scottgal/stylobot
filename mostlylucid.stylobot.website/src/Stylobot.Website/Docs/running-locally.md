# Running Locally

Two paths: **add StyloBot to your own app** (most teams) or **run the demo site** (evaluation / contributor dev).

---

## Path 1 -- Add to your ASP.NET Core app

### Install

```bash
dotnet add package Mostlylucid.BotDetection
```

### Register services

```csharp
// Program.cs -- minimal setup (all heuristic detectors, no external deps)
builder.Services.AddBotDetection();

// Or with options
builder.Services.AddBotDetection(options =>
{
    options.BotThreshold = 0.7;          // probability above this = bot
    options.BlockDetectedBots = false;    // start in observe mode
    options.LogDetailedReasons = true;
});
```

### Add middleware

```csharp
app.UseRouting();
app.UseBotDetection();   // after UseRouting, before MapControllers
```

### Read results in your code

```csharp
app.MapGet("/", (HttpContext ctx) =>
{
    var isBot      = ctx.IsBot();
    var confidence = ctx.GetBotConfidence();
    var botType    = ctx.GetBotType();
    return Results.Ok(new { isBot, confidence, botType });
});
```

### Check response headers

Every response includes detection headers (enabled by default):

```
X-Bot-Detection: true
X-Bot-Probability: 0.82
X-Bot-Confidence: 0.88
X-Bot-RiskBand: VeryHigh
X-Bot-Type: Scraper
X-Bot-Detectors: UserAgent,Ip,Heuristic
X-Bot-ProcessingTime: 0.8ms
```

### Registration variants

| Method | What it does |
|--------|-------------|
| `AddBotDetection()` | All detectors + heuristic AI. No external deps. |
| `AddSimpleBotDetection()` | User-Agent matching only. Fastest, minimal. |
| `AddAdvancedBotDetection(endpoint, model)` | All detectors + Ollama LLM escalation. |

For full configuration options see [GitHub: configuration.md](https://github.com/scottgal/stylobot/blob/main/Mostlylucid.BotDetection/docs/configuration.md).

---

## Path 2 -- Run the demo site

This runs the full StyloBot website with dashboard, live demo, and all detectors enabled.

### Prerequisites

| Tool | Version | Check |
|------|---------|-------|
| .NET SDK | 10+ | `dotnet --version` |
| Node.js | 22+ | `node --version` |
| Git | any | `git --version` |

### Clone

```bash
git clone https://github.com/scottgal/stylobot.git
cd stylobot
```

### Install frontend dependencies

```bash
cd mostlylucid.stylobot.website/src/Stylobot.Website
npm install
cd ../../..
```

### Run

```bash
dotnet run --project mostlylucid.stylobot.website/src/Stylobot.Website/Stylobot.Website.csproj
```

The site starts on **https://localhost:7038**. Vite watch mode starts automatically in Development.

> No PostgreSQL needed for local dev -- the site uses in-memory storage by default.

### Key URLs

| URL | What it shows |
|-----|---------------|
| [https://localhost:7038](https://localhost:7038) | Home page |
| [https://localhost:7038/docs](https://localhost:7038/docs) | Documentation |
| [https://localhost:7038/_stylobot](https://localhost:7038/_stylobot) | Live detection dashboard |
| [https://localhost:7038/Home/LiveDemo](https://localhost:7038/Home/LiveDemo) | Interactive live demo |
| [https://localhost:7038/bot-detection/check](https://localhost:7038/bot-detection/check) | JSON detection endpoint |
| [https://localhost:7038/health](https://localhost:7038/health) | Health check |

### Smoke test with curl

Test a normal browser request:

```bash
curl -k -H "Accept-Language: en-US" \
     -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0 Safari/537.36" \
     https://localhost:7038/bot-detection/check
```

Expected: low `botProbability`, `riskBand: "VeryLow"`, top reason mentions "human likelihood".

Test a bot-like request:

```bash
curl -k -A "curl/8.4.0" https://localhost:7038/bot-detection/check
```

Expected: higher `botProbability`, `riskBand: "Medium"` or above, reasons include "curl" pattern match.

Test an obvious scraper:

```bash
curl -k -A "Python/3.13 aiohttp/3.11.18" -X HEAD https://localhost:7038/.env
```

Expected: `botProbability: 0.8`, `riskBand: "VeryHigh"`, reasons include "Known bot pattern: aiohttp" and "Datacenter IP" (if applicable).

### What to look for in the dashboard

Open [https://localhost:7038/_stylobot](https://localhost:7038/_stylobot) in a browser tab, then generate traffic with curl in another terminal. You should see:

- **Detections appearing in real time** via SignalR
- **Probability and confidence scores** for each request
- **Top reasons** explaining why each request was classified
- **Risk bands** colour-coded from green (VeryLow) to red (VeryHigh)
- **Signature grouping** -- repeat visitors share a signature hash

### Optional: add Ollama for LLM escalation

If you want to test the AI-escalation path:

```bash
# Install and start Ollama (https://ollama.ai)
ollama pull llama3.2:1b

# Then set environment variables before running
$env:BOTDETECTION_AI_PROVIDER="Ollama"
$env:BOTDETECTION_OLLAMA_ENDPOINT="http://localhost:11434"
$env:BOTDETECTION_OLLAMA_MODEL="llama3.2:1b"
```

This is optional. The default heuristic model works without any external dependencies.

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Frontend styles look stale | Run `npm run build` in `mostlylucid.stylobot.website/src/Stylobot.Website` |
| Dashboard (`/_stylobot`) shows no data | Generate traffic with curl or open pages in the browser -- detections appear in real time |
| Build fails with locked binaries | Stop any running `dotnet` processes (`taskkill /f /im dotnet.exe` on Windows) |
| HTTPS certificate warning | Expected for localhost. Use `-k` with curl or accept the browser warning |
| Port 7038 already in use | Stop the other process or change the port in `launchSettings.json` |
| "PostgreSQL connection string required" error | This only happens in Production mode. Make sure `ASPNETCORE_ENVIRONMENT=Development` |

## Next steps

- [How StyloBot Works](/docs/how-stylobot-works) -- scoring, risk bands, policy flow
- [Detectors In Depth](/docs/detectors-in-depth) -- what each detector checks and how to tune it
- [Deploy on Server](/docs/deploy-on-server) -- Docker Compose production rollout
- [GitHub Docs Map](/docs/github-docs-map) -- deeper technical docs in the GitHub repo
