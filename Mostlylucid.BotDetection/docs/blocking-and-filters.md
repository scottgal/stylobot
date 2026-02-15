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

## HttpContext Extensions

```csharp
// Basic checks
bool isBot = context.IsBot();
bool isHuman = context.IsHuman();
double confidence = context.GetBotConfidence();

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

| Band     | Confidence | Recommended Action    |
|----------|------------|-----------------------|
| Low      | < 0.3      | Allow                 |
| Elevated | 0.3 - 0.5  | Challenge or Throttle |
| Medium   | 0.5 - 0.7  | Challenge             |
| High     | > 0.7      | Block                 |

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
