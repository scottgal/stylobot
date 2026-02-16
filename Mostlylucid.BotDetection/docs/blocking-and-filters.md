# Blocking and Filters

Multiple ways to block or allow bots at different levels.

## MVC Attributes

### Block Bots

```csharp
// Block all bots (scrapers, AI bots, malicious - everything)
[BlockBots]
public IActionResult SensitiveData() { ... }

// SEO-friendly: allow search engines through
[BlockBots(AllowSearchEngines = true)]
public IActionResult Indexable() { ... }

// Public content: search engines + social media link previews
[BlockBots(AllowSearchEngines = true, AllowSocialMediaBots = true)]
public IActionResult ProductPage() { ... }

// Health endpoint: let monitoring bots (UptimeRobot, Pingdom) through
[BlockBots(AllowMonitoringBots = true)]
public IActionResult Health() { ... }

// Opt-in to AI crawling (GPTBot, ClaudeBot, etc.)
[BlockBots(AllowAiBots = true, AllowSearchEngines = true)]
public IActionResult PublicDocs() { ... }

// Allow benign automation (feed readers, link checkers)
[BlockBots(AllowGoodBots = true)]
public IActionResult RssFeed() { ... }

// Only block high-confidence detections
[BlockBots(MinConfidence = 0.9)]
public IActionResult ModerateProtection() { ... }

// Custom status code and message
[BlockBots(StatusCode = 429, Message = "Too many requests")]
public IActionResult RateLimited() { ... }

// Honeypot: deliberately allow scrapers and malicious bots in
[BlockBots(AllowScrapers = true, AllowMaliciousBots = true)]
public IActionResult Honeypot() { ... }
```

**Note:** All bot types are blocked by default. Scrapers and malicious bots default to blocked but CAN be allowed per-endpoint via `AllowScrapers` and `AllowMaliciousBots` (useful for honeypots, research, security monitoring).

### Bot Type Allow Properties

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

### Geographic & Network Blocking

Block by country, VPN, proxy, datacenter, or Tor. Requires GeoDetection to be registered for signal data.

```csharp
// Block traffic from specific countries
[BlockBots(BlockCountries = "CN,RU,KP")]
public IActionResult SensitiveApi() { ... }

// Whitelist mode: only allow specific countries
[BlockBots(AllowCountries = "US,GB,DE,FR")]
public IActionResult DomesticOnly() { ... }

// Block VPNs and proxies (anti-fraud)
[BlockBots(BlockVpn = true, BlockProxy = true)]
public IActionResult Payment() { ... }

// Block datacenter IPs (AWS, Azure, GCP) and Tor
[BlockBots(BlockDatacenter = true, BlockTor = true)]
public IActionResult FormSubmission() { ... }

// Combine bot type + geo + network blocking
[BlockBots(AllowSearchEngines = true, BlockCountries = "CN,RU", BlockVpn = true)]
public IActionResult ProtectedContent() { ... }
```

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `BlockCountries` | `string?` | `null` | Comma-separated ISO country codes to block |
| `AllowCountries` | `string?` | `null` | Whitelist mode: only these countries allowed |
| `BlockVpn` | `bool` | `false` | Block VPN connections |
| `BlockProxy` | `bool` | `false` | Block proxy servers |
| `BlockDatacenter` | `bool` | `false` | Block hosting/datacenter IPs |
| `BlockTor` | `bool` | `false` | Block Tor exit nodes |

### Allow Bots

```csharp
// Explicitly allow all bots
[AllowBots]
public IActionResult RobotsFile() { ... }

// Only allow verified bots
[AllowBots(OnlyVerified = true)]
public IActionResult Sitemap() { ... }
```

### Require Human

```csharp
// Blocks ALL bots including verified ones
[RequireHuman]
public IActionResult SubmitForm() { ... }

[RequireHuman(StatusCode = 403, Message = "Human verification required")]
public IActionResult SecureAction() { ... }
```

## Minimal API Filters

```csharp
// Block all bots
app.MapGet("/api/data", () => "sensitive")
   .BlockBots();

// SEO-friendly: search engines + social previews allowed
app.MapGet("/products", () => "catalog")
   .BlockBots(allowSearchEngines: true, allowSocialMediaBots: true);

// Health check: monitoring bots allowed
app.MapGet("/health", () => "ok")
   .BlockBots(allowMonitoringBots: true);

// High-confidence blocking only
app.MapGet("/api/protected", () => "protected")
   .BlockBots(minConfidence: 0.8);

// Honeypot: let scrapers and malicious bots in
app.MapGet("/honeypot", () => "welcome")
   .BlockBots(allowScrapers: true, allowMaliciousBots: true);

// Geo + network blocking
app.MapGet("/api/restricted", () => "data")
   .BlockBots(blockCountries: "CN,RU", blockVpn: true, blockTor: true);

// Country whitelist + datacenter blocking
app.MapPost("/api/payment", () => "ok")
   .BlockBots(allowCountries: "US,GB", blockDatacenter: true);

// Require human
app.MapPost("/api/submit", () => "submitted")
   .RequireHuman();

// Named action policy
app.MapGet("/api/data", () => "sensitive")
   .BotPolicy("default", actionPolicy: "throttle-stealth");

app.MapPost("/api/checkout", () => "ok")
   .BotPolicy("strict", actionPolicy: "block", blockThreshold: 0.8);
```

### Route Group Defaults

```csharp
// All /api routes: block bots, allow search engines
var api = app.MapGroup("/api").WithBotProtection(allowSearchEngines: true);
api.MapGet("/products", () => "data");
api.MapGet("/categories", () => "cats");

// Humans-only group
var secure = app.MapGroup("/secure").WithHumanOnly();
secure.MapPost("/submit", () => "ok");
```

## Global Blocking

Configure automatic blocking in appsettings:

```json
{
  "BotDetection": {
    "BlockDetectedBots": false,
    "BlockStatusCode": 403,
    "BlockMessage": "Access denied",
    "MinConfidenceToBlock": 0.8,
    "AllowVerifiedSearchEngines": true,
    "AllowSocialMediaBots": true,
    "AllowMonitoringBots": true
  }
}
```

## Bot Probability vs Detection Confidence

StyloBot exposes two independent scores:

- **Bot Probability** (`GetBotProbability()`) — How likely is this request from a bot? Range 0.0 (definitely human) to 1.0 (definitely bot).
- **Detection Confidence** (`GetDetectionConfidence()`) — How certain is the system in its verdict? Range 0.0 (guessing) to 1.0 (certain). Based on detector coverage, agreement between detectors, and total evidence weight.

These are independent. You can be 95% confident that something is human (low probability, high confidence). Or you can see a suspicious request but have low confidence because only one detector ran.

### Confidence-Gated Blocking

Use `MinConfidence` on policies or attributes to require a minimum confidence before blocking:

```json
{
  "Policies": {
    "strict": {
      "ImmediateBlockThreshold": 0.7,
      "MinConfidence": 0.9
    }
  }
}
```

```csharp
// Only block when we're sure (confidence >= 0.9) AND bot probability >= 0.7
[BotPolicy("strict", BlockThreshold = 0.7, MinConfidence = 0.9)]
public IActionResult Payment() { }

// Inline detector with confidence gate
[BotDetector("UserAgent,Header", BlockThreshold = 0.7, MinConfidence = 0.85)]
public IActionResult SensitiveEndpoint() { }
```

### Confidence Calculation

Confidence is computed from three factors (independent of bot probability):

1. **Agreement** (40%) — What fraction of detector evidence points in the same direction. If all detectors agree, agreement = 1.0.
2. **Weight Coverage** (35%) — Total evidence weight collected vs expected baseline. More weighted evidence = more confident.
3. **Detector Count** (25%) — Number of distinct detectors that contributed. 4+ detectors = full count score.

Then `ComputeCoverageConfidence()` caps the final value based on which specific detectors ran (e.g., UserAgent, Header, ClientSide, Behavioral, Heuristic have higher weight).

## HttpContext Extensions

```csharp
// Basic checks
bool isBot = context.IsBot();
bool isHuman = context.IsHuman();

// Two-dimensional scoring
double probability = context.GetBotProbability();   // How likely it's a bot
double confidence = context.GetDetectionConfidence(); // How sure we are

// Legacy (returns bot probability — prefer GetBotProbability())
double legacyConf = context.GetBotConfidence();

// Bot type checks
bool isSearchEngine = context.IsSearchEngineBot();
bool isMalicious = context.IsMaliciousBot();
bool isSocial = context.IsSocialMediaBot();
bool isVerified = context.IsVerifiedBot();

// Threshold-based
bool highConfidence = context.IsBotWithConfidence(0.9);
bool shouldAllow = context.ShouldAllowRequest();
bool shouldBlock = context.ShouldBlockRequest();

// Risk assessment
var risk = context.GetRiskBand();
var action = context.GetRecommendedAction();
bool challenge = context.ShouldChallengeRequest();
```

## Risk Bands

| Band     | Bot Probability | Recommended Action    |
|----------|----------------|-----------------------|
| Low      | < 0.3          | Allow                 |
| Elevated | 0.3 - 0.5      | Challenge or Throttle |
| Medium   | 0.5 - 0.7      | Challenge             |
| High     | > 0.7          | Block                 |

## Custom Middleware

```csharp
public class BotActionMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context)
    {
        var action = context.GetRecommendedAction();

        switch (action)
        {
            case RecommendedAction.Block:
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Blocked" });
                return;

            case RecommendedAction.Challenge:
                context.Response.Headers["X-Challenge-Required"] = "true";
                break;

            case RecommendedAction.Throttle:
                context.Response.Headers["Retry-After"] = "60";
                break;
        }

        await _next(context);
    }
}
```

## Signal-Based Filtering

For filtering based on specific detector signals (VPN, datacenter, country, heuristic score, etc.), see [signals-and-custom-filters.md](signals-and-custom-filters.md).

```csharp
// MVC: Block VPN connections
[BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True")]
public IActionResult Payment() { ... }

// Minimal API: Block datacenter IPs
app.MapPost("/api/submit", () => Results.Ok())
   .BlockIfSignal(SignalKeys.IpIsDatacenter, SignalOperator.Equals, "True");
```
