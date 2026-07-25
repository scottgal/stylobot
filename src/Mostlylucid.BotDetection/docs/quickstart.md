# Enterprise Bot Detection with Minimal Code

StyloBot gives you 57-contributor bot detection with anonymous entity resolution in two lines of code. No external services, no database setup, no API keys. It runs entirely self-contained with SQLite storage, in-process similarity search, and progressive identity resolution that learns who keeps coming back - even when they rotate their fingerprint.

```
NuGet: dotnet add package Mostlylucid.BotDetection
```

---

## Block All Bots (Whole App)

The simplest possible setup: detect and block bots across your entire application with zero per-endpoint config. `.WithBotProtection()` / `.WithHumanOnly()` are the group-level equivalents of the `.BlockBots()` / `.RequireHuman()` endpoint filters shown later - applied to a route group with an empty prefix, they cover every endpoint mapped through that group with no route pattern changes required.

**Option A: Block bots, allow the good ones through**

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBotDetection();

var app = builder.Build();
app.UseBotDetection();

// Applies to every endpoint mapped through `app` below - search engines,
// social media previews, and monitoring bots pass through; everything else is blocked
var protectedApp = app.MapGroup("").WithBotProtection(
    allowSearchEngines: true,
    allowSocialMediaBots: true,
    allowMonitoringBots: true);

protectedApp.MapGet("/", () => Results.Ok("home"));
protectedApp.MapGet("/products", () => Results.Ok(new { catalog = "public" }));

app.Run();
```

**Option B: Humans only, no exceptions**

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBotDetection();

var app = builder.Build();
app.UseBotDetection();

// RequireHuman() blocks every bot, including verified crawlers - no Allow* opt-outs
var humansOnly = app.MapGroup("").WithHumanOnly();
humansOnly.MapGet("/", () => Results.Ok("home"));

app.Run();
```

Every detected bot gets a 403. In Option A, search engines, social media previews, and monitoring bots are allowed through because they're explicitly opted in via the `Allow*` parameters - everything else is blocked. `WithHumanOnly()` in Option B blocks everyone, no exceptions.

---

## Minimal API (Per-Endpoint Control)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBotDetection();          // ← registers all 67 detector atoms

var app = builder.Build();
app.UseBotDetection();                       // ← detection middleware

// Every request now has bot detection results available
app.MapGet("/", (HttpContext ctx) => Results.Ok(new
{
    isBot = ctx.IsBot(),
    probability = ctx.GetBotProbability(),   // 0.0-1.0
    confidence = ctx.GetDetectionConfidence(), // how certain the system is
    type = ctx.GetBotType()?.ToString(),
    name = ctx.GetBotName()
}));

// Block bots from sensitive endpoints
app.MapGet("/api/data", () => Results.Ok(new { data = "sensitive" }))
   .BlockBots();

// Allow search engines through (Googlebot, Bingbot, Yandex)
app.MapGet("/products", () => Results.Ok(new { catalog = "public" }))
   .BlockBots(allowSearchEngines: true);

// Allow search engines + social media link previews (Facebook, Twitter/X)
app.MapGet("/blog/{slug}", (string slug) => Results.Ok(new { post = slug }))
   .BlockBots(allowSearchEngines: true, allowSocialMediaBots: true);

// Health check: let monitoring bots through (UptimeRobot, Pingdom)
app.MapGet("/health", () => Results.Ok("healthy"))
   .BlockBots(allowMonitoringBots: true);

// Only humans (blocks ALL bots, including verified crawlers)
app.MapPost("/api/submit", () => Results.Ok(new { submitted = true }))
   .RequireHuman();

// High-confidence blocking only (reduces false positives)
app.MapGet("/api/lenient", () => Results.Ok("data"))
   .BlockBots(minConfidence: 0.9);

// Geo + network blocking (requires GeoDetection contributor)
app.MapPost("/api/payment", () => Results.Ok("ok"))
   .BlockBots(blockCountries: "CN,RU", blockVpn: true, blockDatacenter: true);

// Country whitelist
app.MapGet("/api/domestic", () => Results.Ok("data"))
   .BlockBots(allowCountries: "US,GB,DE,FR");

// Honeypot: deliberately allow scrapers and malicious bots in
app.MapGet("/honeypot", () => Results.Ok("welcome"))
   .BlockBots(allowScrapers: true, allowMaliciousBots: true);

// Named action policy (Minimal API equivalent of [BotPolicy] attribute)
app.MapGet("/api/data", () => "sensitive")
   .BotPolicy("default", actionPolicy: "api-throttle");

app.MapPost("/api/submit", () => "ok")
   .BotPolicy("strict", actionPolicy: "block", blockThreshold: 0.8);

// Route group defaults - apply to all endpoints in the group
var api = app.MapGroup("/api").WithBotProtection(allowSearchEngines: true);
api.MapGet("/products", () => "data");
api.MapGet("/categories", () => "cats");

// Humans-only group
var secure = app.MapGroup("/secure").WithHumanOnly();
secure.MapPost("/checkout", () => "ok");

// Diagnostic endpoints for development
app.MapBotDetectionEndpoints();
// GET  /bot-detection/check    → full detection breakdown with all signals
// GET  /bot-detection/stats    → aggregate statistics
// GET  /bot-detection/health   → system health
// POST /bot-detection/feedback → report false positives/negatives

// Policy-aware robots.txt: Disallow lines are derived from live Block-action
// policy rules and merged with any static rules you configure.
app.MapStyloBotRobotsTxt(options =>
{
    options.Rules.Clear();
    options.Rules.Add(new RobotsRule
    {
        UserAgent = "*",
        Disallow = ["/admin/", "/internal/"]
    });
    options.Rules.Add(new RobotsRule
    {
        UserAgent = "Googlebot",
        CrawlDelaySeconds = 1
    });
    // SitemapUrl defaults to {scheme}://{host}/sitemap.xml
});

// Adaptive sitemap.xml: verified crawlers and humans get PublicUrls;
// high-probability bots get a single honeypot path instead.
app.MapStyloBotSitemap(options =>
{
    options.PublicUrls = ["/", "/products", "/about", "/blog"];
    options.HoneypotPath = "/honeypot/admin";
    options.BotProbabilityThreshold = 0.7;
});

app.Run();
```

That's a complete bot-protected API. Every `.BlockBots()` call blocks all bot types by default - you opt specific types *in* with the `Allow*` parameters.

---

## MVC Controllers

Same two-line setup, protection via attributes.

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBotDetection();
builder.Services.AddControllersWithViews();

var app = builder.Build();
app.UseBotDetection();
app.MapControllers();
app.MapBotDetectionEndpoints();
app.Run();
```

### Controller Attributes

```csharp
[ApiController]
[Route("[controller]")]
public class ProductsController : ControllerBase
{
    // No protection - detection runs, results available, nothing blocked
    [HttpGet]
    public IActionResult List() => Ok(new { products = "all" });

    // Block all bots, allow search engines
    [HttpGet("catalog")]
    [BlockBots(AllowSearchEngines = true)]
    public IActionResult Catalog() => Ok(new { catalog = "indexed" });

    // Block all bots, allow search engines + social media previews
    [HttpGet("{id:int}")]
    [BlockBots(AllowSearchEngines = true, AllowSocialMediaBots = true)]
    public IActionResult Detail(int id) => Ok(new { id });
}

[ApiController]
[Route("[controller]")]
[RequireHuman]  // All actions: humans only
public class CheckoutController : ControllerBase
{
    [HttpPost("cart")]
    public IActionResult AddToCart() => Ok();

    [HttpPost("pay")]
    public IActionResult Pay() => Ok();
}

[ApiController]
[Route("[controller]")]
public class InfraController : ControllerBase
{
    // Skip detection entirely (health checks, metrics, internal)
    [HttpGet("health")]
    [SkipBotDetection]
    public IActionResult Health() => Ok("ok");

    // Monitoring bots allowed
    [HttpGet("status")]
    [BlockBots(AllowMonitoringBots = true)]
    public IActionResult Status() => Ok(new { uptime = "99.9%" });
}
```

### Geographic & Network Blocking (MVC)

```csharp
// Block specific countries
[BlockBots(BlockCountries = "CN,RU,KP")]
public IActionResult SensitiveApi() => Ok();

// Country whitelist
[BlockBots(AllowCountries = "US,GB,DE,FR")]
public IActionResult DomesticOnly() => Ok();

// Block VPNs and proxies
[BlockBots(BlockVpn = true, BlockProxy = true)]
public IActionResult Payment() => Ok();

// Block datacenter IPs (AWS, Azure, GCP) + Tor
[BlockBots(BlockDatacenter = true, BlockTor = true)]
public IActionResult FormSubmission() => Ok();

// Combine everything
[BlockBots(AllowSearchEngines = true, BlockCountries = "CN,RU", BlockVpn = true)]
public IActionResult ProtectedContent() => Ok();
```

---

## Action Policies (Separate Detection from Response)

Instead of binary block/allow, assign named response policies from config. This separates *what* you detect from *how* you respond.

### appsettings.json

```json
{
  "BotDetection": {
    "Classification": { "BotFloor": 0.7 },
    "ActionPolicies": {
      "api-block": {
        "Type": "Block",
        "StatusCode": 403,
        "Message": "Bot traffic is not allowed."
      },
      "api-throttle": {
        "Type": "Throttle",
        "BaseDelayMs": 500,
        "MaxDelayMs": 5000,
        "ScaleByRisk": true,
        "JitterPercent": 0.3
      },
      "shadow-mode": {
        "Type": "LogOnly",
        "AddResponseHeaders": true,
        "LogFullEvidence": true
      }
    }
  }
}
```

### Usage

```csharp
// MVC: bots get stealth-throttled (progressively slower responses)
[BotPolicy("default", ActionPolicy = "api-throttle")]
public IActionResult Browse() => Ok();

// MVC: bots get hard-blocked
[BotPolicy("default", ActionPolicy = "api-block")]
public IActionResult Confirm() => Ok();

// MVC: shadow mode - log everything, block nothing
[BotPolicy("default", ActionPolicy = "shadow-mode")]
public IActionResult PublicApi() => Ok();
```

Policy types: `Block`, `Throttle`, `Challenge`, `Redirect`, `LogOnly`. See [action-policies.md](action-policies.md).

---

## HttpContext Extensions

Available everywhere after `UseBotDetection()` runs:

```csharp
// Boolean checks
context.IsBot()                    // true if bot probability >= threshold
context.IsHuman()                  // inverse
context.IsSearchEngineBot()        // Googlebot, Bingbot, etc.
context.IsVerifiedBot()            // DNS-verified bots
context.IsMaliciousBot()           // known bad actors
context.IsSocialMediaBot()         // Facebook, Twitter/X, LinkedIn

// Scoring (two independent dimensions)
context.GetBotProbability()        // 0.0-1.0: how likely it's a bot
context.GetDetectionConfidence()   // 0.0-1.0: how certain the system is

// Details
context.GetBotType()               // BotType enum
context.GetBotName()               // "Googlebot", "Scrapy", etc.
context.GetRiskBand()              // Low, Elevated, Medium, High
context.GetRecommendedAction()     // Allow, Challenge, Throttle, Block

// Full result with per-detector breakdown
var result = context.GetBotDetectionResult();
```

---

## Configuration

### Minimum (Zero Config)

No `appsettings.json` section needed. Defaults:

| Setting | Default | Effect |
|---------|---------|--------|
| `Classification.BotFloor` | 0.7 | 70%+ bot probability = classified as bot |
| `DefaultActionPolicyName` | `throttle-stealth` | Bots crossing the threshold are auto-throttled - no endpoint config needed. Set to `"block"` for strict default-deny |
| `EnableLlmDetection` | false | No LLM needed |
| Storage | SQLite (auto) | File-based, self-creating `botdetection.db` |

### Ephemeral Mode (Tests and CI)

`AddBotDetectionInMemory()` replaces all SQLite stores with null/in-memory stubs so no `.db` files are created. Identity, session learning, entity resolution, and content-sequence detection are silently degraded; per-request detection runs unchanged. Use this in integration test fixtures and CI pipelines where a persistent file is a problem.

```csharp
// Integration test fixture or CI pipeline
builder.Services.AddBotDetectionInMemory();
app.UseBotDetection();
```

This is intentionally weaker than the default SQLite-backed mode. For production use `AddBotDetection()`.

### Optional Tuning

```json
{
  "BotDetection": {
    "Classification": { "BotFloor": 0.7 },
    "EnableLlmDetection": false,
    "Qdrant": { "Enabled": false },
    "PathPolicies": {
      "/api/login": "strict",
      "/api/checkout/*": "strict",
      "/sitemap.xml": "allowVerifiedBots",
      "/robots.txt": "allowVerifiedBots"
    }
  }
}
```

---

## Dashboard & Monitoring

The StyloBot Dashboard provides a real-time monitoring UI with SignalR live updates, an interactive world map, country/cluster/user-agent analytics, and JSON API endpoints for programmatic access. It's part of the `Mostlylucid.BotDetection.UI` package.

### Installation

```bash
dotnet add package Mostlylucid.BotDetection
dotnet add package Mostlylucid.BotDetection.UI
```

### Setup

Use `AddStyloBot()` + `UseStyloBot()` from the UI package. This is the recommended approach - it registers detection, the dashboard, and all tag helpers in one call with correct middleware ordering guaranteed.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddStyloBot(
    configureDashboard: dashboard =>
    {
        dashboard.BasePath = "/stylobot";
        dashboard.AllowUnauthenticatedAccess = true; // dev only
    });

var app = builder.Build();
app.UseRouting();
app.UseStyloBot(); // detection + broadcast + dashboard, correct order
app.MapHub<StyloBotDashboardHub>("/stylobot/hub");
app.Run();
```

If you need detection without the dashboard UI (e.g. on a gateway node), use `AddBotDetection()` + `UseBotDetection()` from `Mostlylucid.BotDetection` directly.

The dashboard UI is at `/stylobot/`. No separate frontend build required - it's entirely self-contained.

### Authorization

```csharp
// Option 1: Custom filter
builder.Services.AddStyloBotDashboard(
    authFilter: async ctx => ctx.User.Identity?.IsAuthenticated == true,
    configure: options => { options.BasePath = "/stylobot"; });

// Option 2: Named policy
builder.Services.AddStyloBotDashboard(options =>
{
    options.RequireAuthorizationPolicy = "AdminOnly";
});
```

### Dashboard API Endpoints

All endpoints are under `{BasePath}/api/`:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/stylobot/api/summary` | GET | Aggregate statistics (total, bots, humans, rates) |
| `/stylobot/api/detections` | GET | Recent detection events with filtering |
| `/stylobot/api/signatures` | GET | Unique visitor signatures |
| `/stylobot/api/timeseries` | GET | Time-bucketed detection counts |
| `/stylobot/api/topbots` | GET | Top detected bots ranked by hit count |
| `/stylobot/api/countries` | GET | Top bot source countries with reputation data |
| `/stylobot/api/clusters` | GET | Leiden bot clusters with similarity scores |
| `/stylobot/api/useragents` | GET | User agent family aggregation with version/country breakdown |
| `/stylobot/api/sparkline/{sig}` | GET | Sparkline history for a specific signature |
| `/stylobot/api/export` | GET | Export detections as CSV/JSON |
| `/stylobot/api/diagnostics` | GET | Comprehensive diagnostics (rate-limited: 10/min) |
| `/stylobot/api/me` | GET | Current visitor's cached detection (for "Your Detection" panel) |

All API endpoints are **rate-limited** to 60 requests per minute per IP (diagnostics: 10/min). Rate limit headers (`X-RateLimit-Limit`, `X-RateLimit-Remaining`) are included in responses. HTTP 429 is returned when exceeded.

The dashboard page (`/stylobot/`) is **server-side rendered** - all data is embedded in the HTML on first load. SignalR provides live updates only. No XHR calls are made on page load.

### Embed Mode

Pass `?embed=1` to hide the brand header when embedding the dashboard in an iframe:

```html
<iframe src="/stylobot?embed=1" class="w-full" style="min-height: 80vh;"></iframe>
```

This is useful for embedding the dashboard inside admin portals or marketing pages. The `X-Frame-Options: SAMEORIGIN` header is set automatically, so the iframe must be on the same origin.

### Diagnostics API

The `/stylobot/api/diagnostics` endpoint returns a comprehensive snapshot of all detection data in a single call. It's rate-limited to 10 requests per minute per IP.

```bash
curl http://localhost:5001/stylobot/api/diagnostics
```

Response includes:
- `summary` - aggregate counts and rates
- `filterCounts` - breakdown by bot type, risk band, action
- `topBots` - top 10 detected bots with sparkline histories
- `detections` - recent detection events with per-detector contributions and signals
- `signatures` - unique visitor fingerprints

Rate limit headers: `X-RateLimit-Limit`, `X-RateLimit-Remaining`. Returns HTTP 429 when exceeded.

Query parameters (same as `/api/detections`):
- `?isBot=true` - filter by bot/human
- `?riskBand=High` - filter by risk band
- `?botType=Scraper` - filter by bot type
- `?limit=100` - max results (capped at 500)

### Persistence-Only Mode (Gateway/Proxy)

If you run detection on a gateway but serve the dashboard elsewhere:

```csharp
// Gateway - saves detections, no UI
builder.Services.AddBotDetection();
builder.Services.AddBotDetectionPersistence();
// ...
app.UseBotDetection();
app.UseBotDetectionPersistence();

// Dashboard host - serves UI, reads shared database
builder.Services.AddStyloBotDashboard();
builder.Services.AddStyloBotPostgreSQL(connectionString);
```

---

## Testing

```bash
# Normal browser request (low bot score, allowed)
curl -H "Accept: text/html" -H "Accept-Language: en-US" \
  -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0" \
  http://localhost:5090/

# Googlebot (allowed where AllowSearchEngines=true, blocked by RequireHuman)
curl -A "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)" \
  http://localhost:5090/products

# Scraper (blocked by BlockBots)
curl -A "Scrapy/2.7" http://localhost:5090/api/data

# Full detection breakdown (development)
curl http://localhost:5090/bot-detection/check

# Test mode: simulate bot types without real traffic
curl -H "ml-bot-test-mode: malicious" http://localhost:5090/bot-detection/check
curl -H "ml-bot-test-mode: scraper" http://localhost:5090/api/data
```

---

## Bot Type Allow Properties Reference

All blocked by default. Opt specific types in per-endpoint:

| Property | Bot Types Allowed | Use Case |
|----------|------------------|----------|
| `AllowSearchEngines` | Googlebot, Bingbot, Yandex | SEO, indexing |
| `AllowVerifiedBots` | DNS-verified crawlers | Trusted automation |
| `AllowSocialMediaBots` | Facebook, Twitter/X, LinkedIn | Link previews, Open Graph |
| `AllowMonitoringBots` | UptimeRobot, Pingdom, StatusCake | Health checks, uptime |
| `AllowAiBots` | GPTBot, ClaudeBot, Google-Extended | AI training (opt-in) |
| `AllowGoodBots` | Feed readers, link checkers | Benign automation |
| `AllowScrapers` | AhrefsBot, SemrushBot | Honeypots, research |
| `AllowMaliciousBots` | Known bad actors | Honeypots, security research |
| `MinConfidence` | *(threshold)* | Only block high-confidence detections |

---

## How StyloBot Scales

StyloBot is designed to start with zero dependencies and scale to a full production stack. Every tier uses the same detection pipeline - you're only changing storage and enrichment.

### Tier 1: Self-Contained (You Are Here)

```
Your App + AddBotDetection()
    └── SQLite (auto-created botdetection.db)
    └── In-process HNSW similarity search
    └── 67 detector atoms, <1ms per request
    └── No external services
```

**What runs:** All 67 detector atoms execute in a wave-based pipeline. Fast-path detectors (UserAgent, Header, IP, SecurityTool, VersionAge, AiScraper, VerifiedBot, etc.) run in parallel in <1ms. Protocol fingerprinting (TLS, TCP/IP, HTTP/2, HTTP/3) catches bots that spoof everything else. Heuristic scoring extracts ~50 features and runs a lightweight scoring model. Everything persists to SQLite for learning across restarts.

**Good for:** Single app, <100K requests/day, getting started.

### Tier 2: Add GeoDetection

```
Your App + AddBotDetection() + AddGeoRouting() + AddGeoDetectionContributor()
    └── SQLite
    └── Local IP database (DataHubCsv free, no account) or MaxMind GeoLite2
    └── 20+ geo signals: country, VPN, proxy, Tor, datacenter
    └── Bot origin verification (Googlebot from China = suspicious)
    └── All lookups local - no per-request HTTP calls
```

```csharp
builder.Services.AddBotDetection();
builder.Services.AddGeoRoutingSimple();  // DataHubCsv: free, no account, local database
builder.Services.AddGeoDetectionContributor(options =>
{
    options.FlagVpnIps = true;
    options.FlagHostingIps = true;
});
```

DataHubCsv downloads a ~27MB IP database on first start and auto-updates weekly. All lookups are local. For city-level precision, use `GeoProvider.MaxMindLocal` with a free MaxMind account.

Now `BlockCountries`, `BlockVpn`, `BlockDatacenter`, `BlockTor`, and all `geo.*` signals work. See [signals-and-custom-filters.md](signals-and-custom-filters.md).

### Tier 3: PostgreSQL (Production) -Commercial

> Requires `Mostlylucid.BotDetection.UI.PostgreSQL` from the `stylobot-commercial` repo. `AddStyloBotPostgreSQL` is not available in the FOSS package.

```
Your App
    └── PostgreSQL (replaces SQLite)
    └── Multi-server shared learning and detection history
    └── pgvector for similarity search (optional)
    └── Concurrent access from multiple gateway/app nodes
```

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotDashboard();        // real-time UI (see Dashboard & Monitoring above)
builder.Services.AddStyloBotPostgreSQL(connectionString, options =>
{
    options.RetentionDays = 90;
    options.EnableAutomaticCleanup = true;
});
```

```yaml
# docker-compose.yml
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_DB: stylobot
      POSTGRES_USER: stylobot
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U stylobot"]
      interval: 10s
      timeout: 5s
      retries: 5

  app:
    build: .
    environment:
      ConnectionStrings__BotDetection: "Host=postgres;Database=stylobot;Username=stylobot;Password=${DB_PASSWORD}"
    depends_on:
      postgres:
        condition: service_healthy

volumes:
  postgres-data:
```

**Good for:** >100K requests/day, multiple servers, need shared learning state.

### Tier 4: Full Stack - YARP Gateway + Qdrant + LLM -Commercial

```
Internet → Caddy (TLS) → Stylobot Gateway (YARP) → Your App
                              │
                              ├── PostgreSQL (shared learning, analytics)
                              ├── Qdrant (vector similarity search)
                              └── LLamaSharp CPU LLM (bot classification)
```

The gateway runs detection on all traffic and forwards results as HTTP headers. Your app reads headers - no SDK needed, any language.

```yaml
# docker-compose.yml (production)
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    environment:
      DEFAULT_UPSTREAM: "http://app:8080"
      # PostgreSQL (commercial persistence)
      StyloBotDashboard__PostgreSQL__ConnectionString: "Host=postgres;Database=stylobot;Username=stylobot;Password=${DB_PASSWORD}"
      # Qdrant vector search
      BotDetection__Qdrant__Enabled: true
      BotDetection__Qdrant__Endpoint: http://qdrant:6334
      BotDetection__Qdrant__EnableEmbeddings: true
      BotDetection__Qdrant__AutoDownloadEmbeddingModel: false
      # CPU-only LLM for bot classification
      BotDetection__AiDetection__Provider: LlamaSharp
      BotDetection__AiDetection__LlamaSharp__ModelPath: "Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q4_k_m.gguf"

  app:
    build: .
    environment:
      BOTDETECTION_TRUST_UPSTREAM: true  # trust gateway headers, skip re-detection

  qdrant:
    image: qdrant/qdrant:latest

  postgres:
    image: postgres:16
    environment:
      POSTGRES_DB: stylobot
      POSTGRES_USER: stylobot
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes:
      - postgres-data:/var/lib/postgresql/data

  caddy:
    image: caddy:latest

volumes:
  postgres-data:
```

**Headers forwarded to your app:**

| Header | Example | Purpose |
|--------|---------|---------|
| `X-Bot-Detected` | `true` | Bot/human classification |
| `X-Bot-Confidence` | `0.91` | Detection confidence |
| `X-Bot-Detection-Probability` | `0.87` | Bot probability |
| `X-Bot-Type` | `Scraper` | Bot category |
| `X-Bot-Name` | `AhrefsBot` | Identified bot |
| `X-Bot-Detection-RiskBand` | `High` | Risk classification |

**Your app (any language) just reads headers:**

```csharp
// ASP.NET Core - trust upstream detection
builder.Services.AddBotDetection();
builder.Services.Configure<BotDetectionOptions>(o =>
{
    o.TrustUpstreamDetection = true;
    // Optional: HMAC signature verification for defense-in-depth
    o.UpstreamSignatureHeader = "X-Bot-Signature";
    o.UpstreamSignatureSecret = "base64-encoded-shared-secret";
});
```

```python
# Python/Flask - read headers directly
@app.route('/api/data')
def api_data():
    if request.headers.get('X-Bot-Detected') == 'true':
        return jsonify(error='blocked'), 403
    return jsonify(data='sensitive')
```

```javascript
// Node.js/Express (with @stylobot/node middleware)
import { styloBotMiddleware } from '@stylobot/node'
app.use(styloBotMiddleware({ mode: 'headers' }))

app.get('/api/data', (req, res) => {
  if (req.stylobot.verdict.riskBand === 'High') {
    return res.status(403).json({ error: 'blocked' });
  }
  res.json({ data: 'sensitive' });
});
```

**What each component adds:**

| Component | Purpose | Required? |
|-----------|---------|-----------|
| **PostgreSQL** | Shared learning state, detection history, multi-server access (commercial) | Recommended |
| **Qdrant** | Vector similarity - find bots even when they rotate UAs | Optional |
| **LLamaSharp** | CPU-only LLM for bot cluster naming and classification | Optional |
| **Caddy/Nginx** | TLS termination, static files | Your choice |
| **Gateway** | Centralized detection for multi-app / non-.NET backends | For multi-service |

### Choosing Your Tier

```
Starting a new project?
├── Single ASP.NET app → Tier 1 (two lines of code)
│   └── Need geo blocking? → Add Tier 2 (one more line)
│       └── Need analytics? → Add Tier 3 (PostgreSQL, commercial)
└── Multiple apps or non-.NET? → Tier 4 (Gateway)
```

All tiers use the same detection pipeline. Moving between them is a DI registration change - your endpoint protection code stays the same.

---

## Further Reading

- [tutorial.md](tutorial.md) - Step-by-step walkthrough: sample storefront with behaviour-aware UX patterns
- [ui-components.md](ui-components.md) - All tag helpers and view components with attribute reference
- [blocking-and-filters.md](blocking-and-filters.md) - Full attribute and filter reference
- [signals-and-custom-filters.md](signals-and-custom-filters.md) - Signal access API, custom filters, signal-based endpoint filtering
- [action-policies.md](action-policies.md) - Block, Throttle, Challenge, Redirect, LogOnly
- [configuration.md](configuration.md) - Full `BotDetectionOptions` reference
- [integration-levels.md](integration-levels.md) - Detailed 5-level integration guide
- [deployment-guide.md](deployment-guide.md) - Docker, Kubernetes, production deployment
- [ai-detection.md](ai-detection.md) - Heuristic model and LLM escalation
- [yarp-integration.md](yarp-integration.md) - YARP reverse proxy setup
- [Node SDK](../../../sdk/node/README.md) - Express/Fastify middleware, SSR widgets, web components