# StyloBot
by ***mostly*lucid**

Bot detection framework for ASP.NET Core. 21 detectors, heuristic AI with continuous learning, zero-PII architecture.


<img src="https://raw.githubusercontent.com/scottgal/stylobot/refs/heads/main/mostlylucid.stylobot.website/src/Stylobot.Website/wwwroot/img/stylowall.svg?raw=true" alt="img" style="max-width:200px; height:auto;" />


```csharp
builder.Services.AddBotDetection();
app.UseBotDetection();

app.MapGet("/api/data", () => "sensitive").BlockBots();
app.MapPost("/api/submit", () => "ok").RequireHuman();
```

## How It Works

Requests flow through a wave-based detector pipeline. Fast-path detectors (Wave 0) run in parallel under 1ms. If signals warrant it, slower detectors and heuristic AI fire in subsequent waves. A learning loop feeds detection outcomes back to improve future accuracy.

```
Request
  |
  v
Wave 0 (parallel, <1ms)
  UserAgent | Header | IP | Behavioral | ClientSide | SecurityTool
  CacheBehavior | TLS/TCP/HTTP2 Fingerprinting | ResponseBehavior
  |
  v
Wave 1+ (triggered by Wave 0 signals)
  VersionAge | Inconsistency | ProjectHoneypot | ReputationBias
  MultiLayerCorrelation | BehavioralWaveform | Heuristic
  |
  v
Final
  HeuristicLate (aggregates all evidence) | LLM (optional escalation)
  |
  v
Result -> Action Policy -> Learning Bus -> Weight/Pattern Updates
```

## Projects

| Project | Purpose |
|---------|---------|
| [`Mostlylucid.BotDetection`](Mostlylucid.BotDetection/) | Core detection library (NuGet package) |
| [`Mostlylucid.BotDetection.UI`](Mostlylucid.BotDetection.UI/) | Dashboard, TagHelpers, SignalR hub |
| [`Mostlylucid.BotDetection.UI.PostgreSQL`](Mostlylucid.BotDetection.UI.PostgreSQL/) | PostgreSQL + TimescaleDB + pgvector storage |
| [`Mostlylucid.BotDetection.Demo`](Mostlylucid.BotDetection.Demo/) | Interactive demo with all 21 detectors |
| [`Mostlylucid.BotDetection.Console`](Mostlylucid.BotDetection.Console/) | Standalone gateway/proxy console |
| [`Stylobot.Gateway`](Stylobot.Gateway/) | Docker-first YARP reverse proxy |
| [`Mostlylucid.GeoDetection`](Mostlylucid.GeoDetection/) | Geographic routing (MaxMind, ip-api) |
| [`Mostlylucid.GeoDetection.Contributor`](Mostlylucid.GeoDetection.Contributor/) | Geo enrichment for bot detection |
| [`Mostlylucid.Common`](Mostlylucid.Common/) | Shared utilities (caching, telemetry) |

Test projects: `Mostlylucid.BotDetection.Test`, `Mostlylucid.BotDetection.Orchestration.Tests`, `Mostlylucid.BotDetection.Demo.Tests`

## Deployment Tiers

Three tiers from zero-setup to production scale. See [deployment-guide.md](Mostlylucid.BotDetection/docs/deployment-guide.md) for full details.

### Tier 1: Minimal (zero setup)

No database, no external services. All 21 detectors run in-memory with SQLite for learned patterns.

```csharp
builder.Services.AddBotDetection();
app.UseBotDetection();
```

### Tier 2: Standard (SQLite + Learning)

Add heuristic AI and continuous learning. Patterns and weights persist across restarts.

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth",
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "Heuristic": { "Enabled": true, "EnableWeightLearning": true }
    },
    "Learning": { "Enabled": true }
  }
}
```

### Tier 3: Production (PostgreSQL + TimescaleDB + pgvector)

Multi-server deployments, real-time dashboard, time-series analytics, ML similarity search.

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotDashboard();
builder.Services.AddStyloBotPostgreSQL(connectionString, options =>
{
    options.EnableTimescaleDB = true;
    options.EnablePgVector = true;
});
```

```bash
docker compose up  # PostgreSQL + TimescaleDB included
```

## Quick Start

```bash
dotnet add package Mostlylucid.BotDetection
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBotDetection();

var app = builder.Build();
app.UseBotDetection();
app.MapBotDetectionEndpoints(); // /bot-detection/check, /stats, /health

app.MapGet("/", (HttpContext ctx) => Results.Ok(new
{
    isBot = ctx.IsBot(),
    confidence = ctx.GetBotConfidence(),
    botType = ctx.GetBotType()?.ToString()
}));

app.Run();
```

Test it:

```bash
# Human browser
curl -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0" \
  -H "Accept: text/html" -H "Accept-Language: en-US" \
  http://localhost:5000/bot-detection/check

# Bot
curl -A "Scrapy/2.5.0" http://localhost:5000/bot-detection/check

# Test mode
curl -H "ml-bot-test-mode: googlebot" http://localhost:5000/bot-detection/check
```

## Service Registration

```csharp
builder.Services.AddBotDetection();                    // All detectors, heuristic AI
builder.Services.AddSimpleBotDetection();              // User-Agent only (<1ms)
builder.Services.AddComprehensiveBotDetection();       // All heuristics, no LLM
builder.Services.AddAdvancedBotDetection(endpoint, model); // + LLM escalation (Ollama)
```

## Endpoint Protection

```csharp
// Minimal API filters
app.MapGet("/api/data", handler).BlockBots();
app.MapGet("/api/data", handler).BlockBots(allowSearchEngines: true);
app.MapGet("/api/data", handler).BlockBots(minConfidence: 0.9);
app.MapPost("/api/submit", handler).RequireHuman();

// MVC attributes
[BlockBots(AllowVerifiedBots = true)]
public IActionResult Index() => View();
```

## HttpContext Extensions

```csharp
context.IsBot()                  // bool
context.IsHuman()                // bool
context.IsSearchEngineBot()      // Googlebot, Bingbot, etc.
context.IsVerifiedBot()          // DNS-verified bots
context.GetBotConfidence()       // 0.0 - 1.0
context.GetBotType()             // SearchEngine, Scraper, Automation, etc.
context.GetBotName()             // "Googlebot", "Scrapy", etc.
context.GetRiskBand()            // VeryLow .. VeryHigh
context.GetRecommendedAction()   // Allow, Challenge, Throttle, Block
context.GetBotDetectionResult()  // Full result with reasons
```

## Action Policies

Separate detection (WHAT) from response (HOW):

| Policy | Effect |
|--------|--------|
| `block` | HTTP 403 |
| `throttle-stealth` | Silent delay (bots don't notice) |
| `challenge` | CAPTCHA / proof-of-work |
| `redirect-honeypot` | Trap redirect |
| `logonly` | Shadow mode (detect but allow) |

## Architecture

**Blackboard pattern** via StyloFlow. Detectors write signals to an ephemeral blackboard per request. Raw PII (IP, UA) stays in `DetectionContext`, never on the blackboard. Signals use hierarchical keys: `request.ip.is_datacenter`, `detection.useragent.confidence`.

**Zero-PII**: Signatures use HMAC-SHA256 hashing. No raw IP or User-Agent is ever persisted.

**Learning loop**: Detection outcomes feed back through `LearningEventBus` to update pattern reputation and detector weights. Patterns progress through Neutral -> Suspect -> ConfirmedBad states.

## Build

```bash
dotnet build mostlylucid.stylobot.sln
dotnet test
dotnet run --project Mostlylucid.BotDetection.Demo
# Visit: https://localhost:5001/SignatureDemo
```

## Documentation

Full docs in [`Mostlylucid.BotDetection/docs/`](Mostlylucid.BotDetection/docs/):

| Topic | Link |
|-------|------|
| Deployment Guide | [deployment-guide.md](Mostlylucid.BotDetection/docs/deployment-guide.md) |
| Configuration Reference | [configuration.md](Mostlylucid.BotDetection/docs/configuration.md) |
| AI Detection & Learning | [ai-detection.md](Mostlylucid.BotDetection/docs/ai-detection.md) |
| Learning & Reputation | [learning-and-reputation.md](Mostlylucid.BotDetection/docs/learning-and-reputation.md) |
| Action Policies | [action-policies.md](Mostlylucid.BotDetection/docs/action-policies.md) |
| Detection Strategies | [detection-strategies.md](Mostlylucid.BotDetection/docs/detection-strategies.md) |
| Extensibility | [extensibility.md](Mostlylucid.BotDetection/docs/extensibility.md) |
| YARP Integration | [yarp-integration.md](Mostlylucid.BotDetection/docs/yarp-integration.md) |
| Telemetry & Metrics | [telemetry-and-metrics.md](Mostlylucid.BotDetection/docs/telemetry-and-metrics.md) |
| PostgreSQL Storage | [PostgreSQL README](Mostlylucid.BotDetection.UI.PostgreSQL/README.md) |
| TimescaleDB Guide | [TIMESCALEDB_GUIDE.md](Mostlylucid.BotDetection.UI.PostgreSQL/TIMESCALEDB_GUIDE.md) |
| pgvector Guide | [PGVECTOR_GUIDE.md](Mostlylucid.BotDetection.UI.PostgreSQL/PGVECTOR_GUIDE.md) |

## External Dependencies

The solution uses local project references for development. Related repos cloned as siblings:

```
D:\Source\
  mostlylucid.stylobot\     # This repo
  styloflow\                 # StyloFlow.Core, StyloFlow.Retrieval.Core
  mostlylucid.atoms\         # mostlylucid.ephemeral and atoms
```

## Requirements

- .NET 8.0, 9.0, or 10.0
- Optional: [Ollama](https://ollama.ai/) for LLM escalation
- Optional: PostgreSQL 16+ for production storage
- Optional: TimescaleDB for time-series analytics

## License

[The Unlicense](https://unlicense.org/) - Public Domain
