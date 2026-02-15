# Integration Levels

StyloBot supports five integration levels, from lightweight attribute-based protection on individual endpoints up to a full YARP gateway with real-time dashboard. Each level builds on the previous one. Mix and match to suit your architecture.

| Level | What it does | Requires |
|-------|-------------|----------|
| **1. Attributes Only** | Block/allow bots per controller or action | `AddBotDetection()` + `UseBotDetection()` |
| **2. Minimal API Filters** | Fluent bot protection on endpoints | Same as Level 1 |
| **3. Middleware + Persistence** | Detect + save to database | Level 1 + `AddBotDetectionPersistence()` |
| **4. Full Dashboard** | Real-time UI with charts and tables | Level 1 + `AddStyloBotDashboard()` |
| **5. YARP Gateway** | Reverse proxy with cluster routing | Dedicated gateway project |

---

## Level 1: Attribute-Based Protection

No middleware configuration needed beyond the basics. Protect individual controllers or actions with attributes.

### Setup

```csharp
builder.Services.AddBotDetection();

// ...

app.UseRouting();
app.UseBotDetection();
app.MapControllers();
```

### Controller Attributes

```csharp
// Block all bots from this controller
[BlockBots]
public class PaymentController : Controller
{
    // Allow search engines + social media (Open Graph previews)
    [BlockBots(AllowSearchEngines = true, AllowSocialMediaBots = true)]
    public IActionResult ProductPage() => View();

    // Only humans allowed here
    [RequireHuman]
    public IActionResult Checkout() => View();

    // Let monitoring bots through for uptime checks
    [BlockBots(AllowMonitoringBots = true)]
    public IActionResult Health() => Ok("healthy");

    // Skip detection entirely
    [SkipBotDetection]
    public IActionResult Ping() => Ok("pong");
}
```

### Bot Type Filtering

`[BlockBots]` blocks all bots by default. Use `Allow*` properties to whitelist specific types. Every bot type can be individually allowed per-endpoint, including scrapers and malicious bots (useful for honeypots).

| Property | Bot Types Allowed | Default | Use Case |
|----------|------------------|---------|----------|
| `AllowSearchEngines` | Googlebot, Bingbot, Yandex | `false` | SEO, indexing |
| `AllowVerifiedBots` | DNS-verified crawlers | `false` | Trusted automation |
| `AllowSocialMediaBots` | Facebook, Twitter/X, LinkedIn | `false` | Link previews, Open Graph |
| `AllowMonitoringBots` | UptimeRobot, Pingdom, StatusCake | `false` | Health checks, uptime |
| `AllowAiBots` | GPTBot, ClaudeBot, Google-Extended | `false` | AI training (opt-in) |
| `AllowGoodBots` | Feed readers, link checkers | `false` | Benign automation |
| `AllowScrapers` | AhrefsBot, SemrushBot, etc. | `false` | Honeypots, research |
| `AllowMaliciousBots` | Known bad actors | `false` | Honeypots, security research |
| `MinConfidence` | (threshold) | `0.0` | Only block high-confidence detections |

```csharp
// Common patterns:
[BlockBots]                                                    // Block everything
[BlockBots(AllowSearchEngines = true)]                         // SEO-friendly
[BlockBots(AllowSearchEngines = true, AllowSocialMediaBots = true)] // Public content
[BlockBots(AllowMonitoringBots = true)]                        // Health endpoints
[BlockBots(MinConfidence = 0.9)]                               // Lenient blocking
[BlockBots(AllowAiBots = true)]                                // Opt-in to AI crawling
[BlockBots(AllowScrapers = true, AllowMaliciousBots = true)]   // Honeypot
```

### Geographic & Network Blocking

Block requests by country, VPN, proxy, datacenter IP, or Tor exit node. Requires GeoDetection contributor for signal data.

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `BlockCountries` | `string?` | `null` | Comma-separated ISO country codes to block |
| `AllowCountries` | `string?` | `null` | Whitelist mode: only allow listed countries |
| `BlockVpn` | `bool` | `false` | Block VPN connections |
| `BlockProxy` | `bool` | `false` | Block proxy servers |
| `BlockDatacenter` | `bool` | `false` | Block hosting/datacenter IPs |
| `BlockTor` | `bool` | `false` | Block Tor exit nodes |

```csharp
[BlockBots(BlockCountries = "CN,RU,KP")]                      // Country blocking
[BlockBots(AllowCountries = "US,GB,DE")]                       // Country whitelist
[BlockBots(BlockVpn = true, BlockProxy = true)]                // Anti-fraud
[BlockBots(BlockDatacenter = true, BlockTor = true)]           // Block automation infra
[BlockBots(AllowSearchEngines = true, BlockCountries = "CN")]  // SEO-friendly + geo block
```

### Available Attributes

| Attribute | Purpose | Key Properties |
|-----------|---------|---------------|
| `[BlockBots]` | Block detected bots | `AllowSearchEngines`, `AllowSocialMediaBots`, `AllowMonitoringBots`, `AllowAiBots`, `AllowGoodBots`, `AllowScrapers`, `AllowMaliciousBots`, `AllowVerifiedBots`, `MinConfidence`, `StatusCode`, `BlockCountries`, `AllowCountries`, `BlockVpn`, `BlockProxy`, `BlockDatacenter`, `BlockTor` |
| `[RequireHuman]` | Block ALL bots including verified | `StatusCode`, `Message` |
| `[AllowBots]` | Mark endpoint as bot-friendly | `OnlyVerified`, `OnlySearchEngines` |
| `[SkipBotDetection]` | Bypass detection entirely | - |
| `[BotPolicy("name")]` | Apply named detection policy | `PolicyName`, `Skip`, `BlockAction`, `BlockThreshold`, `ActionPolicy` |
| `[BotDetector("UA,Header")]` | Run specific detectors inline | `Detectors`, `Weight`, `BlockThreshold`, `TimeoutMs`, `ActionPolicy` |
| `[BotAction("block")]` | Override response action | `PolicyName`, `FallbackAction`, `MinRiskThreshold` |

### Fine-Grained Control

```csharp
// Strict policy with stealth throttling
[BotPolicy("strict", ActionPolicy = "throttle-stealth")]
public IActionResult ApiEndpoint() => Ok();

// Run only specific detectors, custom threshold
[BotDetector("UserAgent,Header,Behavioral", BlockThreshold = 0.8)]
public IActionResult CustomDetection() => Ok();

// Named action policy with fallback
[BotAction("challenge", FallbackAction = "block")]
public IActionResult Checkout() => View();
```

---

## Level 2: Minimal API Endpoint Filters

Fluent protection for minimal API endpoints using the builder pattern.

### Setup

Same as Level 1. No additional registration needed.

### Usage

```csharp
// Block all bots from sensitive endpoints
app.MapGet("/api/data", () => Results.Ok("sensitive"))
   .BlockBots();

// SEO-friendly: allow search engines + social media previews
app.MapGet("/api/products", () => Results.Ok("products"))
   .BlockBots(allowSearchEngines: true, allowSocialMediaBots: true);

// Health endpoint: let monitoring bots through
app.MapGet("/health", () => Results.Ok("ok"))
   .BlockBots(allowMonitoringBots: true);

// Only humans can submit forms
app.MapPost("/api/submit", (FormData data) => Results.Ok())
   .RequireHuman();
```

### HttpContext Extensions

In any endpoint or middleware, query detection results directly:

```csharp
app.MapGet("/", (HttpContext ctx) =>
{
    if (ctx.IsBot()) return Results.StatusCode(403);
    if (ctx.IsSearchEngineBot()) return Results.Ok("welcome, crawler");
    if (ctx.ShouldChallengeRequest()) return Results.StatusCode(429);

    return Results.Ok(new
    {
        botProbability = ctx.GetBotProbability(),          // 0.0-1.0: likelihood of being a bot
        detectionConfidence = ctx.GetDetectionConfidence(), // 0.0-1.0: certainty of the verdict
        confidence = ctx.GetBotConfidence(),                // backward compat (returns bot probability)
        riskBand = ctx.GetRiskBand().ToString(),
        action = ctx.GetRecommendedAction().ToString()
    });
});
```

### Diagnostic Endpoints

```csharp
// GET /bot-detection/check  - Full detection analysis
// GET /bot-detection/stats  - Aggregate statistics
// GET /bot-detection/health - Service health
app.MapBotDetectionEndpoints();
```

### Tag Helper (Razor Views)

```html
<!-- Expose detection data to client-side JavaScript -->
<bot-detection-result />

<!-- Result: window.__botDetection = { risk: 0.15, isBot: false, ... } -->
```

---

## Level 3: Middleware + Persistence (Gateway Pattern)

Detection results are saved to a shared database and broadcast via SignalR. Use this when a gateway/proxy runs detection and a separate application serves the dashboard.

### Setup

```csharp
builder.Services.AddBotDetection();
builder.Services.AddBotDetectionPersistence(); // Event store + SignalR hub

// Optional: PostgreSQL replaces the default in-memory store
var pgConn = builder.Configuration["StyloBotDashboard:PostgreSQL:ConnectionString"];
if (!string.IsNullOrEmpty(pgConn))
{
    builder.Services.AddStyloBotPostgreSQL(pgConn, options =>
    {
        options.EnableTimescaleDB = true;
        options.AutoInitializeSchema = true;
        options.RetentionDays = 90;
    });
}

// ...

app.UseRouting();
app.UseBotDetection();
app.UseBotDetectionPersistence(); // Saves detections + maps SignalR hub
```

### What Gets Registered

`AddBotDetectionPersistence()` registers only what is needed to persist and broadcast:

- `IDashboardEventStore` (in-memory default, replaced by `AddStyloBotPostgreSQL`)
- `VisitorListCache` (server-side cache for connected dashboard clients)
- `ILlmResultCallback` (forwards LLM classification results via SignalR)
- SignalR hub at `/stylobot/hub`

It does **not** register the dashboard UI, simulator, or ViewComponent data extraction. This keeps the gateway lightweight.

### Architecture

```
Internet --> Gateway (detects, persists, broadcasts)
                |
                +--> SignalR hub (/stylobot/hub)
                |
                +--> PostgreSQL (shared)
                |
         Website (reads from same DB, serves dashboard UI)
```

---

## Level 4: Full Dashboard

The complete package: detection + real-time dashboard with charts, tables, signal inspection, and export.

### Setup

```csharp
builder.Services.AddBotDetection();

// Full dashboard with authorization
builder.Services.AddStyloBotDashboard(options =>
{
    options.BasePath = "/_stylobot";
    options.HubPath = "/_stylobot/hub";
    options.MaxEventsInMemory = 1000;

    // Authorization: header secret, dev localhost, or custom logic
    options.AuthorizationFilter = context =>
    {
        if (context.Request.Headers.TryGetValue("X-StyloBot-Secret", out var secret)
            && secret == "your-secret")
            return Task.FromResult(true);

        return Task.FromResult(false);
    };
});

// Optional: durable storage
builder.Services.AddStyloBotPostgreSQL(connectionString);

// ...

app.UseRouting();
app.UseBotDetection();
app.UseStyloBotDashboard(); // Dashboard UI + broadcast middleware + SignalR hub
```

### Quick Setup (Shorthand)

```csharp
// One-liner with auth filter
builder.Services.AddStyloBotDashboard(
    authFilter: ctx => Task.FromResult(ctx.Request.Headers["X-Secret"] == "s3cret"));
```

### Dashboard Features

- Real-time detection table with row-click signal expansion
- Time-series charts (detections/minute, bot vs human)
- Top bot countries and cluster visualization
- Signature list with similarity search
- Detection export (JSON/CSV)
- SignalR live updates

### Dashboard API

All endpoints are under the configured `BasePath` (default: `/_stylobot`):

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/` | GET | Dashboard HTML page |
| `/api/detections` | GET | Detection list (filterable) |
| `/api/signatures` | GET | Signature feed |
| `/api/summary` | GET | Statistics summary |
| `/api/timeseries` | GET | Time-series chart data |
| `/api/export` | GET | Export detections (JSON/CSV) |
| `/hub` | WebSocket | SignalR real-time hub |

---

## Level 5: YARP Gateway with Cluster Routing

A dedicated reverse proxy that runs bot detection on all traffic and routes based on bot classification. The gateway forwards detection results as HTTP headers to backend applications.

### Setup (Gateway)

```csharp
// Detection + persistence
builder.Services.AddBotDetection();
builder.Services.AddBotDetectionPersistence();
builder.Services.AddStyloBotPostgreSQL(pgConnectionString);

// YARP reverse proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        context.AddRequestTransform(tc =>
        {
            // Forward detection results as headers to backend
            tc.HttpContext.AddBotDetectionHeaders(
                (name, value) => tc.ProxyRequest!.Headers
                    .TryAddWithoutValidation(name, value));
            return default;
        });
    });

// ...

app.UseRouting();
app.UseBotDetection();
app.UseBotDetectionPersistence();
app.MapReverseProxy();
```

### Cluster Routing

Route bots to different backend clusters:

```csharp
// In YARP transform
var cluster = httpContext.GetBotAwareCluster(
    defaultCluster: "website",
    crawlerCluster: "crawler-optimized",
    blockCluster: "honeypot");
```

### Backend Trust (Website)

The website behind the gateway trusts upstream detection headers instead of re-running detection:

```csharp
// Website Program.cs - trusts gateway headers
builder.Services.AddBotDetection();
builder.Services.Configure<BotDetectionOptions>(o =>
    o.TrustUpstreamDetection = true);

// OR via environment variable
// BOTDETECTION_TRUST_UPSTREAM=true

// Full dashboard on the website (reads from same PostgreSQL)
builder.Services.AddStyloBotDashboard();
builder.Services.AddStyloBotPostgreSQL(pgConnectionString);
```

### Forwarded Headers

The gateway sends these headers to backends (via `AddBotDetectionHeaders`):

| Header | Example | Purpose |
|--------|---------|---------|
| `X-Bot-Detected` | `true` | Bot/human classification |
| `X-Bot-Confidence` | `0.91` | Detection confidence (how certain the system is) |
| `X-Bot-Detection-Probability` | `0.87` | Bot probability (likelihood of being a bot) |
| `X-Bot-Type` | `Scraper` | Bot category |
| `X-Bot-Name` | `AhrefsBot` | Identified bot name |
| `X-Bot-Detection-Country` | `CN` | Source country (geo enrichment) |
| `X-Bot-Detection-RiskBand` | `High` | Risk classification |
| `X-Bot-Detection-Reasons` | JSON array | Top 5 detection reasons |
| `X-Bot-Detection-Contributions` | JSON | Detector breakdown |
| `X-Bot-Detection-ProcessingMs` | `2.3` | Detection latency |

### Docker Compose

```yaml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    environment:
      - BotDetection__Qdrant__Enabled=true
      - BotDetection__Qdrant__Endpoint=http://qdrant:6334
      - BotDetection__Qdrant__EnableEmbeddings=true
      - BotDetection__EnableLlmDetection=true
      - BotDetection__AiDetection__Ollama__Endpoint=http://ollama:11434
      - StyloBotDashboard__PostgreSQL__ConnectionString=Host=timescaledb;...
    depends_on:
      - timescaledb
      - qdrant
      - ollama

  website:
    image: your-app:latest
    environment:
      - BOTDETECTION_TRUST_UPSTREAM=true
      - StyloBotDashboard__PostgreSQL__ConnectionString=Host=timescaledb;...

  qdrant:
    image: qdrant/qdrant:latest

  ollama:
    image: ollama/ollama:latest

  timescaledb:
    image: timescale/timescaledb:latest-pg16
```

---

## Choosing an Integration Level

| Use Case | Recommended Level |
|----------|------------------|
| Protect a few endpoints from scraping | **Level 1** - Attributes |
| API-first app, minimal APIs | **Level 2** - Endpoint filters |
| Microservices, need centralized detection data | **Level 3** - Persistence |
| Single app, want visibility into traffic | **Level 4** - Full dashboard |
| Multi-app architecture, high traffic | **Level 5** - YARP gateway |
| Just need `ctx.IsBot()` in code | **Level 1** with HttpContext extensions |

### Registration Methods Summary

| Method | Project | Purpose |
|--------|---------|---------|
| `AddSimpleBotDetection()` | Core | UA-only detection |
| `AddBotDetection()` | Core | All heuristic detectors |
| `AddComprehensiveBotDetection()` | Core | Alias for AddBotDetection |
| `AddAdvancedBotDetection(endpoint, model)` | Core | Heuristic + LLM escalation |
| `AddBotDetectionPersistence()` | UI | Lightweight persistence (no dashboard) |
| `AddStyloBotDashboard(configure)` | UI | Full dashboard + persistence |
| `AddStyloBotPostgreSQL(conn)` | UI.PostgreSQL | Durable storage |

### Middleware Methods Summary

| Method | Purpose |
|--------|---------|
| `UseBotDetection()` | Run detection pipeline |
| `UseBotDetectionPersistence()` | Save to DB + SignalR broadcast |
| `UseStyloBotDashboard()` | Dashboard UI + persistence + SignalR |
| `MapBotDetectionEndpoints()` | Diagnostic API endpoints |

### Qdrant Vector Search (Optional)

Any level can enable Qdrant for similarity-based detection:

```json
{
  "BotDetection": {
    "Qdrant": {
      "Enabled": true,
      "Endpoint": "http://localhost:6334",
      "EnableEmbeddings": true
    }
  }
}
```

When embeddings are enabled, each detection generates a 384-dim semantic vector via ONNX (all-MiniLM-L6-v2, runs CPU-only, ~1-3ms per embedding) alongside the 64-dim heuristic vector. Both vectors are stored in Qdrant for dual-vector similarity matching.
