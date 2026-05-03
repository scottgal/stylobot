# StyloBot API Reference

Complete API reference for the `Mostlylucid.BotDetection` NuGet package.

---

## Table of Contents

- [Installation](#installation)
- [Quick Start](#quick-start)
- [Service Registration](#service-registration)
- [Middleware](#middleware)
- [HttpContext Extensions](#httpcontext-extensions)
- [Endpoint Filters (Minimal API)](#endpoint-filters-minimal-api)
- [Diagnostic Endpoints](#diagnostic-endpoints)
- [Attributes (MVC)](#attributes-mvc)
- [Configuration](#configuration)
- [Models & Enums](#models--enums)
- [Action Policies](#action-policies)
- [YARP Integration](#yarp-integration)
- [Custom Detectors](#custom-detectors)
- [Tag Helpers](#tag-helpers)

---

## Installation

```bash
dotnet add package Mostlylucid.BotDetection
```

NuGet: [mostlylucid.botdetection](https://www.nuget.org/packages/mostlylucid.botdetection)

---

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register bot detection (uses appsettings.json "BotDetection" section)
builder.Services.AddBotDetection();

var app = builder.Build();

// Add middleware to the pipeline
app.UseBotDetection();

// Optional: map diagnostic endpoints
app.MapBotDetectionEndpoints("/bot-detection");

app.Run();
```

Access results anywhere via HttpContext:

```csharp
app.MapGet("/", (HttpContext ctx) =>
{
    if (ctx.IsBot())
        return Results.Text($"Bot detected: {ctx.GetBotName()} ({ctx.GetBotConfidence():P0})");

    return Results.Text("Hello, human!");
});
```

---

## Service Registration

**Namespace:** `Mostlylucid.BotDetection.Extensions`

All registration methods return `IServiceCollection` for chaining.

### AddBotDetection

Primary registration. Enables all heuristic detection (UA, headers, IP, behavioral). LLM disabled by default.

```csharp
public static IServiceCollection AddBotDetection(
    this IServiceCollection services,
    Action<BotDetectionOptions>? configure = null)
```

```csharp
// Minimal (defaults + appsettings.json)
builder.Services.AddBotDetection();

// With custom configuration
builder.Services.AddBotDetection(options =>
{
    options.BotThreshold = 0.8;
    options.BlockDetectedBots = true;
});
```

### AddBotDetection (IConfiguration overload)

Bind from a non-standard configuration section.

```csharp
public static IServiceCollection AddBotDetection(
    this IServiceCollection services,
    IConfiguration configuration,
    Action<BotDetectionOptions>? configure = null)
```

```csharp
builder.Services.AddBotDetection(
    builder.Configuration.GetSection("MyApp:Security:BotDetection"));
```

### AddSimpleBotDetection

User-agent pattern matching only. Fastest option.

```csharp
public static IServiceCollection AddSimpleBotDetection(
    this IServiceCollection services,
    Action<BotDetectionOptions>? configure = null)
```

Disables: header analysis, IP detection, behavioral analysis, LLM.

### AddComprehensiveBotDetection

All heuristic detectors, no LLM. **Recommended for most production apps.**

```csharp
public static IServiceCollection AddComprehensiveBotDetection(
    this IServiceCollection services,
    Action<BotDetectionOptions>? configure = null)
```

Enables: UA, headers, IP, behavioral. Disables: LLM.

### AddAdvancedBotDetection

Full pipeline including LLM escalation. Requires Ollama.

```csharp
public static IServiceCollection AddAdvancedBotDetection(
    this IServiceCollection services,
    string ollamaEndpoint = "http://localhost:11434",
    string model = "qwen2.5:1.5b",
    Action<BotDetectionOptions>? configure = null)
```

```csharp
// Default Ollama
builder.Services.AddAdvancedBotDetection();

// Custom endpoint
builder.Services.AddAdvancedBotDetection(
    ollamaEndpoint: "http://ollama-server:11434",
    model: "phi3:mini");
```

Fail-safe: if Ollama is unavailable, detection continues with heuristics only.

### ConfigureBotDetection

Post-registration customisation.

```csharp
public static IServiceCollection ConfigureBotDetection(
    this IServiceCollection services,
    Action<BotDetectionOptions> configure)
```

---

## Middleware

**Namespace:** `Mostlylucid.BotDetection.Middleware`

### UseBotDetection

Registers the detection middleware. Must be called after `UseRouting()` and before `UseAuthorization()`.

```csharp
public static IApplicationBuilder UseBotDetection(this IApplicationBuilder builder)
```

```csharp
app.UseRouting();
app.UseBotDetection();
app.UseAuthorization();
```

The middleware stores results in `HttpContext.Items` using these keys:

| Key | Type | Description |
|-----|------|-------------|
| `BotDetectionResult` | `BotDetectionResult` | Full detection result |
| `BotDetection.AggregatedEvidence` | `AggregatedEvidence` | Full orchestrator evidence |
| `BotDetection.IsBot` | `bool` | Bot classification |
| `BotDetection.Confidence` | `double` | Confidence score (0.0-1.0) |
| `BotDetection.BotType` | `BotType?` | Detected bot type |
| `BotDetection.BotName` | `string?` | Bot name if known |
| `BotDetection.Category` | `string?` | Primary detection category |
| `BotDetection.Reasons` | `IReadOnlyList<DetectionReason>` | All detection reasons |
| `BotDetection.PolicyName` | `string` | Policy used |
| `BotDetection.PolicyAction` | `PolicyAction?` | Action taken |

---

## HttpContext Extensions

**Namespace:** `Mostlylucid.BotDetection.Extensions`

All methods extend `HttpContext`. Safe to call before middleware runs (return safe defaults).

### Basic Detection

```csharp
// Get full result object (null if middleware hasn't run)
BotDetectionResult? GetBotDetectionResult(this HttpContext context)

// Is this request from a bot?
bool IsBot(this HttpContext context)

// Is this a verified good bot (e.g. Googlebot with DNS verification)?
bool IsVerifiedBot(this HttpContext context)

// Is this a search engine bot?
bool IsSearchEngineBot(this HttpContext context)

// Is this a malicious bot?
bool IsMaliciousBot(this HttpContext context)

// Is this a human visitor?
bool IsHuman(this HttpContext context)

// Is this a social media crawler?
bool IsSocialMediaBot(this HttpContext context)

// Is this a bot with confidence at or above threshold?
bool IsBotWithConfidence(this HttpContext context, double threshold)
```

### Scores & Classification

```csharp
// Bot probability (0.0 to 1.0) - how likely this request is from a bot.
double GetBotProbability(this HttpContext context)

// Detection confidence (0.0 to 1.0) - how certain the system is in its verdict.
// Independent of bot probability: high confidence + low probability = "definitely human".
// Based on detector coverage, agreement between detectors, and evidence weight.
double GetDetectionConfidence(this HttpContext context)

// Legacy: returns bot probability (same as GetBotProbability). Prefer the explicit methods above.
double GetBotConfidence(this HttpContext context)

// Bot type enum, or null
BotType? GetBotType(this HttpContext context)

// Bot name string (e.g. "Googlebot"), or null
string? GetBotName(this HttpContext context)

// Primary detection category (e.g. "UserAgent", "IP", "Header")
string? GetBotCategory(this HttpContext context)

// All detection reason objects
IReadOnlyList<DetectionReason> GetDetectionReasons(this HttpContext context)
```

### Risk Assessment

```csharp
// Risk band: Unknown, VeryLow, Low, Elevated, Medium, High, VeryHigh, Verified
RiskBand GetRiskBand(this HttpContext context)

// Recommended action: Allow, Throttle, Challenge, Block
RecommendedAction GetRecommendedAction(this HttpContext context)

// Should this request be challenged (CAPTCHA/PoW)?
// True for Elevated and Medium risk.
bool ShouldChallengeRequest(this HttpContext context)

// Should this request be throttled?
// True for Elevated risk and above.
bool ShouldThrottleRequest(this HttpContext context)
```

### Decision Helpers

```csharp
// Allow humans and verified bots. True if not a bot or verified.
bool ShouldAllowRequest(this HttpContext context)

// Block if bot detected AND not verified.
bool ShouldBlockRequest(this HttpContext context)
```

### Client-Side Fingerprinting

```csharp
// Inconsistency score (0-100). 0 = consistent, 100 = highly inconsistent.
int GetInconsistencyScore(this HttpContext context)

// Browser integrity score from client-side fingerprinting. Null if unavailable.
int? GetBrowserIntegrityScore(this HttpContext context)

// Headless browser likelihood from client-side fingerprinting. Null if unavailable.
double? GetHeadlessLikelihood(this HttpContext context)
```

---

## Endpoint Filters (Minimal API)

**Namespace:** `Mostlylucid.BotDetection.Extensions`

### BlockBots

Block bots from accessing an endpoint.

```csharp
public static RouteHandlerBuilder BlockBots(
    this RouteHandlerBuilder builder,
    bool allowVerifiedBots = false,
    bool allowSearchEngines = false,
    double minConfidence = 0.0,
    int statusCode = 403)
```

```csharp
app.MapGet("/api/data", () => "sensitive")
    .BlockBots();

app.MapGet("/api/public", () => "ok")
    .BlockBots(allowSearchEngines: true, minConfidence: 0.8);
```

### RequireHuman

Block all bots including verified. For endpoints that must only serve humans.

```csharp
public static RouteHandlerBuilder RequireHuman(
    this RouteHandlerBuilder builder,
    int statusCode = 403)
```

```csharp
app.MapPost("/api/submit", () => "submitted")
    .RequireHuman();
```

---

## Diagnostic Endpoints

### MapBotDetectionEndpoints

Maps three diagnostic endpoints under a configurable prefix.

```csharp
public static IEndpointRouteBuilder MapBotDetectionEndpoints(
    this IEndpointRouteBuilder endpoints,
    string prefix = "/bot-detection")
```

```csharp
app.MapBotDetectionEndpoints("/bot-detection");
```

**Endpoints created:**

#### `GET /bot-detection/check`

Returns full detection evidence for the current request.

```json
{
  "policy": "default",
  "isBot": false,
  "isHuman": true,
  "isVerifiedBot": false,
  "isSearchEngineBot": false,
  "humanProbability": 0.996,
  "botProbability": 0.004,
  "confidence": 0.906,
  "botType": null,
  "botName": null,
  "riskBand": "VeryLow",
  "recommendedAction": { "action": "Allow", "reason": "Very low risk (probability: <1%)" },
  "processingTimeMs": 0.42,
  "aiRan": true,
  "detectorsRan": ["UserAgent", "Header", "Ip", "Behavioral", "Heuristic", "..."],
  "detectorCount": 13,
  "failedDetectors": [],
  "earlyExit": false,
  "signals": { "ua.available": true, "ip.detected": true, "..." : "..." },
  "categoryBreakdown": {
    "UserAgent": { "score": -0.2, "weight": 2.0 },
    "Heuristic": { "score": -0.84, "weight": 1.5 }
  },
  "contributions": [
    {
      "detector": "HeuristicEarly",
      "category": "Heuristic",
      "priority": 50,
      "processingMs": 0.12,
      "impact": -0.84,
      "weight": 1.5,
      "weightedImpact": -1.26,
      "reason": "92% human likelihood",
      "signals": {}
    }
  ]
}
```

#### `GET /bot-detection/stats`

Returns aggregate detection statistics.

```json
{
  "totalRequests": 14523,
  "botsDetected": 2341,
  "botPercentage": 16.12,
  "verifiedBots": 891,
  "maliciousBots": 47,
  "averageProcessingTimeMs": 0.83,
  "botTypeBreakdown": {
    "SearchEngine": 891,
    "Scraper": 1203,
    "MaliciousBot": 47,
    "AiBot": 200
  }
}
```

#### `GET /bot-detection/health`

Health check endpoint.

```json
{
  "status": "healthy",
  "service": "BotDetection",
  "totalRequests": 14523,
  "averageResponseMs": 0.83
}
```

---

## Attributes (MVC)

**Namespace:** `Mostlylucid.BotDetection.Attributes`

### [BotPolicy]

Apply a named detection policy to a controller or action.

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BotPolicyAttribute : Attribute, IFilterMetadata
{
    public BotPolicyAttribute(string policyName);

    public string PolicyName { get; }
    public bool Skip { get; set; }                              // Skip detection entirely
    public BotBlockAction BlockAction { get; set; }             // Default: BotBlockAction.Default
    public int BlockStatusCode { get; set; }                    // Default: 403
    public string? BlockRedirectUrl { get; set; }
    public double BlockThreshold { get; set; }                  // -1 = use policy default
    public string? ActionPolicy { get; set; }                   // Named action policy override
}
```

```csharp
[BotPolicy("strict")]
public class PaymentController : Controller { }

[BotPolicy("strict", ActionPolicy = "throttle-stealth")]
public IActionResult ProtectedApi() => Ok();

[BotPolicy("relaxed")]
public IActionResult PublicProfile() => Ok();
```

Built-in policies: `default`, `strict`, `relaxed`, `demo`, `static`, `learning`, `monitor`, `api`.

### [BotDetector]

Run specific detectors inline without a full policy.

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BotDetectorAttribute : Attribute, IFilterMetadata
{
    public BotDetectorAttribute(string detectors);              // Comma-separated

    public string Detectors { get; }
    public double Weight { get; set; }                          // Default: 1.0
    public double BlockThreshold { get; set; }                  // Default: 0.85
    public double AllowThreshold { get; set; }                  // Default: 0.3
    public BotBlockAction BlockAction { get; set; }             // Default: StatusCode
    public int BlockStatusCode { get; set; }                    // Default: 403
    public bool Skip { get; set; }
    public int TimeoutMs { get; set; }                          // Default: 1000
    public string? ActionPolicy { get; set; }

    public IReadOnlyList<string> GetDetectorList();
}
```

Available detectors: `UserAgent`, `Header`, `Ip`, `Behavioral`, `Inconsistency`, `ClientSide`, `Onnx`, `Llm`.

```csharp
[BotDetector("UserAgent")]
public IActionResult QuickCheck() => Ok();

[BotDetector("UserAgent,Header,Ip", BlockAction = BotBlockAction.Throttle)]
public IActionResult MultiDetector() => Ok();

[BotDetector("Behavioral", Weight = 2.0, BlockThreshold = 0.8)]
public IActionResult RateLimited() => Ok();
```

### [BotAction]

Specify a named action policy for bot handling.

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BotActionAttribute : Attribute, IFilterMetadata
{
    public BotActionAttribute(string policyName);

    public string PolicyName { get; }
    public string? FallbackAction { get; set; }
    public double MinRiskThreshold { get; set; }                // Default: 0
}
```

```csharp
[BotPolicy("strict")]
[BotAction("block-hard")]
public IActionResult Login() => Ok();

[BotAction("throttle-stealth")]
public IActionResult Api() => Ok();

[BotAction("challenge", FallbackAction = "block")]
public IActionResult Checkout() => Ok();
```

### [SkipBotDetection]

Skip detection entirely for an endpoint.

```csharp
[SkipBotDetection]
public IActionResult HealthCheck() => Ok("healthy");
```

### BotBlockAction Enum

```csharp
public enum BotBlockAction
{
    Default,        // Use policy's default
    StatusCode,     // Return HTTP status (default 403)
    Redirect,       // Redirect to URL
    Challenge,      // CAPTCHA/challenge page
    Throttle,       // Rate limit
    LogOnly         // Log only, don't block (shadow mode)
}
```

---

## Configuration

**Namespace:** `Mostlylucid.BotDetection.Models`

Configuration binds from `appsettings.json` section `"BotDetection"`.

### BotDetectionOptions

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "SignatureHashKey": "base64-encoded-hmac-key",
    "EnableTestMode": false,

    "EnableUserAgentDetection": true,
    "EnableHeaderAnalysis": true,
    "EnableIpDetection": true,
    "EnableBehavioralAnalysis": true,
    "EnableLlmDetection": false,

    "BlockDetectedBots": false,
    "BlockStatusCode": 403,
    "BlockMessage": "Access denied",
    "MinConfidenceToBlock": 0.8,
    "AllowVerifiedSearchEngines": true,
    "AllowSocialMediaBots": true,
    "AllowMonitoringBots": true,

    "MaxRequestsPerMinute": 60,
    "BehavioralWindowSeconds": 60,

    "CacheDurationSeconds": 300,
    "MaxCacheEntries": 10000,

    "EnableBackgroundUpdates": true,
    "UpdateSchedule": {
      "Cron": "0 2 * * *",
      "Timezone": "UTC",
      "RunOnStartup": true
    },

    "AiDetection": {
      "Provider": "Ollama",
      "Ollama": {
        "Endpoint": "http://localhost:11434",
        "Model": "qwen2.5:1.5b",
        "TimeoutMs": 15000,
        "MaxConcurrentRequests": 5
      }
    },

    "DefaultActionPolicyName": "throttle-stealth"
  }
}
```

#### Key Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BotThreshold` | `double` | `0.7` | Confidence threshold for bot classification (0.0-1.0) |
| `SignatureHashKey` | `string?` | auto-generated | Base64 HMAC key for zero-PII signatures |
| `EnableTestMode` | `bool` | `false` | Allow `ml-bot-test-mode` header overrides (dev only) |
| `EnableUserAgentDetection` | `bool` | `true` | UA pattern matching |
| `EnableHeaderAnalysis` | `bool` | `true` | HTTP header analysis |
| `EnableIpDetection` | `bool` | `true` | Datacenter IP detection |
| `EnableBehavioralAnalysis` | `bool` | `true` | Request rate/pattern analysis |
| `EnableLlmDetection` | `bool` | `false` | AI-based detection (requires Ollama) |
| `BlockDetectedBots` | `bool` | `false` | Auto-block detected bots |
| `BlockStatusCode` | `int` | `403` | HTTP status when blocking |
| `MinConfidenceToBlock` | `double` | `0.8` | Min confidence to trigger block |
| `AllowVerifiedSearchEngines` | `bool` | `true` | Allow Googlebot, Bingbot, etc. |
| `AllowSocialMediaBots` | `bool` | `true` | Allow Facebook, Twitter crawlers |
| `AllowMonitoringBots` | `bool` | `true` | Allow UptimeRobot, Pingdom |
| `MaxRequestsPerMinute` | `int` | `60` | Behavioral rate limit per IP |
| `CacheDurationSeconds` | `int` | `300` | Detection result cache TTL |
| `DefaultActionPolicyName` | `string?` | `null` | Default action policy for all requests |
| `ResponsePiiMasking` | `ResponsePiiMaskingOptions` | defaults | Response mutation settings for `mask-pii`/`strip-pii` (disabled by default) |
| `StorageProvider` | `StorageProvider` | `Sqlite` | Storage backend (Sqlite or PostgreSQL) |
| `PostgreSQLConnectionString` | `string?` | `null` | Auto-enables PostgreSQL when set |

#### AI Detection Settings

```csharp
public class AiDetectionOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Ollama;
    public OllamaOptions Ollama { get; set; } = new();
    public OnnxOptions Onnx { get; set; } = new();
}

public enum AiProvider { Ollama, Onnx }

public class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:1.5b";
    public int TimeoutMs { get; set; } = 15000;
    public int MaxConcurrentRequests { get; set; } = 5;
}
```

#### Response PII Masking Settings

```csharp
public sealed class ResponsePiiMaskingOptions
{
    public bool Enabled { get; set; } = false;
    public bool AutoApplyForHighConfidenceMalicious { get; set; } = true;
    public double AutoApplyBotProbabilityThreshold { get; set; } = 0.90;
    public double AutoApplyConfidenceThreshold { get; set; } = 0.75;
}
```

See [response-pii-masking.md](response-pii-masking.md) for production rollout examples.

---

## Models & Enums

### BotDetectionResult

```csharp
public class BotDetectionResult
{
    public double ConfidenceScore { get; set; }         // 0.0-1.0
    public bool IsBot { get; set; }
    public List<DetectionReason> Reasons { get; set; }
    public BotType? BotType { get; set; }
    public string? BotName { get; set; }
    public long ProcessingTimeMs { get; set; }
}
```

### DetectionReason

```csharp
public class DetectionReason
{
    public required string Category { get; set; }       // "UserAgent", "IP", "Header", etc.
    public required string Detail { get; set; }         // Human-readable detail
    public double ConfidenceImpact { get; set; }        // 0.0-1.0
}
```

### AggregatedEvidence

Full orchestrator output, available from `HttpContext.Items["BotDetection.AggregatedEvidence"]`.

```csharp
public sealed record AggregatedEvidence
{
    public required double BotProbability { get; init; }          // 0.0=human, 1.0=bot
    public required double Confidence { get; init; }              // Classification certainty
    public required RiskBand RiskBand { get; init; }
    public BotType? PrimaryBotType { get; init; }
    public string? PrimaryBotName { get; init; }
    public bool EarlyExit { get; init; }
    public EarlyExitVerdict? EarlyExitVerdict { get; init; }
    public bool AiRan { get; init; }
    public double TotalProcessingTimeMs { get; init; }
    public IReadOnlySet<string> ContributingDetectors { get; init; }
    public IReadOnlySet<string> FailedDetectors { get; init; }
    public IReadOnlyDictionary<string, object> Signals { get; init; }
    public IReadOnlyDictionary<string, CategoryScore> CategoryBreakdown { get; init; }
    public IReadOnlyList<DetectionContribution> Contributions { get; init; }
    public string? PolicyName { get; init; }
    public PolicyAction? PolicyAction { get; init; }
    public string? TriggeredActionPolicyName { get; init; }
}
```

### BotType

```csharp
public enum BotType
{
    Unknown,
    SearchEngine,       // Googlebot, Bingbot
    SocialMediaBot,     // Facebook, Twitter crawlers
    MonitoringBot,      // UptimeRobot, Pingdom
    Scraper,            // Generic scrapers
    MaliciousBot,       // Attack tools (sqlmap, etc.)
    GoodBot,            // Benign automation
    VerifiedBot,        // DNS-verified good bot
    AiBot               // GPTBot, ClaudeBot, etc.
}
```

### RiskBand

```csharp
public enum RiskBand
{
    Unknown = 0,
    VeryLow = 1,
    Low = 2,
    Elevated = 3,
    Medium = 4,
    High = 5,
    VeryHigh = 6,
    Verified = 7
}
```

### RecommendedAction

```csharp
public enum RecommendedAction
{
    Allow,
    Throttle,
    Challenge,
    Block
}
```

### PolicyAction

```csharp
public enum PolicyAction
{
    Continue,
    Allow,
    Block,
    Challenge,
    Throttle,
    LogOnly,
    EscalateToSlowPath,
    EscalateToAi
}
```

### EarlyExitVerdict

```csharp
public enum EarlyExitVerdict
{
    VerifiedGoodBot,
    VerifiedBadBot,
    Whitelisted,
    Blacklisted,
    PolicyAllowed,
    PolicyBlocked
}
```

---

## Action Policies

Action policies control **what happens** when a bot is detected. They are separate from detection (WHAT) -- action policies handle the HOW.

### Built-in Action Policies

| Name | Type | Description |
|------|------|-------------|
| `block` | Block | HTTP 403 Forbidden |
| `block-hard` | Block | HTTP 403 with no-cache headers |
| `block-soft` | Block | HTTP 403 with friendly message |
| `block-debug` | Block | HTTP 403 with full detection details in body |
| `throttle` | Throttle | Rate limit with configurable delay |
| `throttle-gentle` | Throttle | Light delay (100-500ms) |
| `throttle-moderate` | Throttle | Medium delay (500-2000ms) |
| `throttle-aggressive` | Throttle | Heavy delay (2000-5000ms) |
| `throttle-stealth` | Throttle | Silent delay, scaled by risk band |
| `challenge` | Challenge | Generic challenge page |
| `challenge-captcha` | Challenge | CAPTCHA challenge |
| `challenge-js` | Challenge | JavaScript challenge |
| `challenge-pow` | Challenge | Proof-of-work challenge |
| `redirect` | Redirect | Redirect to URL |
| `redirect-honeypot` | Redirect | Redirect to honeypot trap |
| `redirect-tarpit` | Redirect | Redirect to tarpit (slow response) |
| `redirect-error` | Redirect | Redirect to error page |
| `logonly` | LogOnly | Log only, no blocking |
| `shadow` | LogOnly | Shadow mode (detect, never block) |
| `debug` | LogOnly | Full debug logging |

### Custom Action Policies

Configure via `appsettings.json`:

```json
{
  "BotDetection": {
    "DefaultActionPolicyName": "throttle-stealth",
    "ActionPolicies": {
      "my-custom-block": {
        "Type": "Block",
        "StatusCode": 429,
        "Message": "Too many requests",
        "Headers": {
          "Retry-After": "60"
        }
      },
      "my-custom-throttle": {
        "Type": "Throttle",
        "BaseDelayMs": 1000,
        "MaxDelayMs": 10000,
        "ScaleByRisk": true,
        "IncludeRetryAfter": true
      }
    }
  }
}
```

### IActionPolicy Interface

Implement custom action policies:

```csharp
public interface IActionPolicy
{
    string Name { get; }
    ActionType ActionType { get; }

    Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default);
}

public enum ActionType
{
    Block,
    Throttle,
    Challenge,
    Redirect,
    LogOnly,
    Custom
}
```

---

## YARP Integration

**Namespace:** `Mostlylucid.BotDetection.Extensions`

Extensions for passing bot detection results through YARP reverse proxy.

### AddBotDetectionHeaders

Basic headers: `X-Bot-Detected`, `X-Bot-Confidence`, `X-Bot-Type`, `X-Bot-Name`, `X-Bot-Category`, `X-Is-Search-Engine`, `X-Is-Malicious-Bot`, `X-Is-Social-Bot`.

```csharp
public static void AddBotDetectionHeaders(
    this HttpContext httpContext,
    Action<string, string> addHeader)
```

### AddBotDetectionHeadersVerbose

All basic headers plus `X-Bot-Detection-Reasons`.

```csharp
public static void AddBotDetectionHeadersVerbose(
    this HttpContext httpContext,
    Action<string, string> addHeader)
```

### AddBotDetectionHeadersFull

Comprehensive headers for dashboard display. Includes probabilities, contributions, signals, policy info.

```csharp
public static void AddBotDetectionHeadersFull(
    this HttpContext httpContext,
    Action<string, string> addHeader)
```

### AddTlsFingerprintingHeaders

Network-layer metadata (TLS, TCP/IP, HTTP/2) for advanced fingerprinting.

```csharp
public static void AddTlsFingerprintingHeaders(
    this HttpContext httpContext,
    Action<string, string> addHeader)
```

### AddComprehensiveBotHeaders

Combines `AddBotDetectionHeadersFull` + `AddTlsFingerprintingHeaders` in one call.

```csharp
public static void AddComprehensiveBotHeaders(
    this HttpContext httpContext,
    Action<string, string> addHeader)
```

### GetBotAwareCluster

Route to different YARP clusters based on bot type.

```csharp
public static string GetBotAwareCluster(
    this HttpContext httpContext,
    string defaultCluster,
    string? crawlerCluster = null,
    string? blockCluster = null)
```

### ShouldBlockBot

Decision helper for YARP transforms.

```csharp
public static bool ShouldBlockBot(
    this HttpContext httpContext,
    double minConfidence = 0.7,
    bool allowSearchEngines = true,
    bool allowSocialBots = true)
```

### YARP Transform Example

```csharp
builder.Services.AddReverseProxy()
    .LoadFromConfig(configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        context.AddRequestTransform(transformContext =>
        {
            transformContext.HttpContext.AddBotDetectionHeaders(
                (name, value) => transformContext.ProxyRequest
                    .Headers.TryAddWithoutValidation(name, value));
            return ValueTask.CompletedTask;
        });
    });
```

---

## Custom Detectors

**Namespace:** `Mostlylucid.BotDetection.Orchestration`

Implement `IContributingDetector` to create custom detectors.

### IContributingDetector Interface

```csharp
public interface IContributingDetector
{
    string Name { get; }
    int Priority => 100;                    // Lower = runs first
    bool IsEnabled => true;
    TimeSpan TriggerTimeout => TimeSpan.FromMilliseconds(500);
    TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(2);
    bool IsOptional => true;
    IReadOnlyList<TriggerCondition> TriggerConditions => [];

    Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default);
}
```

### ContributingDetectorBase

Abstract base class with helpers.

```csharp
public abstract class ContributingDetectorBase : IContributingDetector
{
    // Implement these:
    public abstract string Name { get; }
    public abstract Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default);

    // Helpers:
    protected static IReadOnlyList<DetectionContribution> Single(DetectionContribution c);
    protected static IReadOnlyList<DetectionContribution> Multiple(params DetectionContribution[] c);
    protected static IReadOnlyList<DetectionContribution> None();
}
```

### BlackboardState

Request context provided to detectors. Contains signals from other detectors and the raw HttpContext.

```csharp
public sealed class BlackboardState
{
    public required HttpContext HttpContext { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public double CurrentRiskScore { get; init; }
    public required IReadOnlySet<string> CompletedDetectors { get; init; }
    public required IReadOnlySet<string> FailedDetectors { get; init; }
    public required IReadOnlyList<DetectionContribution> Contributions { get; init; }
    public required string RequestId { get; init; }
    public TimeSpan Elapsed { get; init; }

    // PII access (in-memory only, never persisted)
    public string UserAgent { get; }
    public string? ClientIp { get; }
    public string Path { get; }

    public T? GetSignal<T>(string key);
    public bool HasSignal(string key);
}
```

### Trigger Conditions

Control when detectors run based on signals from earlier detectors.

```csharp
// Factory methods
Triggers.WhenSignalExists("ip.detected")
Triggers.WhenSignalEquals("ua.is_bot", true)
Triggers.WhenRiskExceeds(0.5)
Triggers.WhenDetectorCount(3)
Triggers.AnyOf(condition1, condition2)
Triggers.AllOf(condition1, condition2)

// Built-in triggers
Triggers.WhenDatacenterIp
Triggers.WhenUaIsBot
Triggers.WhenRiskMediumOrHigher
```

### Example Custom Detector

```csharp
public class GeoFenceDetector : ContributingDetectorBase
{
    public override string Name => "GeoFence";
    public override int Priority => 50;

    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
        [Triggers.WhenSignalExists("geo.country_code")];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken ct)
    {
        var country = state.GetSignal<string>("geo.country_code");

        if (country is "XX") // Blocked country
        {
            return Task.FromResult(Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "GeoFence",
                ConfidenceDelta = 0.3,
                Weight = 1.5,
                Reason = $"Request from blocked country: {country}",
                Signals = new Dictionary<string, object>
                {
                    ["geofence.blocked"] = true,
                    ["geofence.country"] = country
                }
            }));
        }

        return Task.FromResult(None());
    }
}

// Register:
services.AddSingleton<IContributingDetector, GeoFenceDetector>();
```

---

## Tag Helpers

**Namespace:** `Mostlylucid.BotDetection.TagHelpers`

### `<bot-detection-result>`

Injects detection results into client-side JavaScript.

```html
<!-- Injects window.__botDetection = { ... } -->
<bot-detection-result />

<!-- Custom variable name -->
<bot-detection-result variable-name="botResult" />

<!-- Full result with all contributions -->
<bot-detection-result full="true" />

<!-- Output as data-* attributes instead of script -->
<bot-detection-result output-data-prefix="bot" />
```

---

## IBotDetectionService

Direct service injection for programmatic access.

```csharp
public interface IBotDetectionService
{
    Task<BotDetectionResult> DetectAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);

    BotDetectionStatistics GetStatistics();
}

public class BotDetectionStatistics
{
    public int TotalRequests { get; set; }
    public int BotsDetected { get; set; }
    public int VerifiedBots { get; set; }
    public int MaliciousBots { get; set; }
    public double AverageProcessingTimeMs { get; set; }
    public Dictionary<string, int> BotTypeBreakdown { get; set; }
}
```

```csharp
app.MapGet("/detect", async (HttpContext ctx, IBotDetectionService svc) =>
{
    var result = await svc.DetectAsync(ctx);
    return Results.Ok(new { result.IsBot, result.ConfidenceScore, result.BotType });
});
```

---

## Detection Policies

Named detection policies define which detectors run, thresholds, and escalation rules.

### Built-in Policies

| Policy | Description |
|--------|-------------|
| `DetectionPolicy.Default` | Fast path with early bailout |
| `DetectionPolicy.Strict` | Deep analysis, all detectors |
| `DetectionPolicy.Relaxed` | Minimal detection for public content |
| `DetectionPolicy.Static` | Extremely permissive for static assets |
| `DetectionPolicy.Demo` | Full pipeline for demonstration |
| `DetectionPolicy.Learning` | Full pipeline with ONNX + LLM, no blocking |
| `DetectionPolicy.Monitor` | Shadow mode (detect but never block) |
| `DetectionPolicy.Api` | Optimised for API endpoints |
| `DetectionPolicy.FastWithOnnx` | Fast path + ONNX inference |
| `DetectionPolicy.FastWithAi` | Fast path + ONNX + LLM |

### Policy Structure

```csharp
public sealed record DetectionPolicy
{
    public required string Name { get; init; }
    public ImmutableList<string> FastPathDetectors { get; init; }
    public ImmutableList<string> SlowPathDetectors { get; init; }
    public ImmutableList<string> AiPathDetectors { get; init; }
    public bool UseFastPath { get; init; } = true;
    public bool ForceSlowPath { get; init; }
    public bool EscalateToAi { get; init; }
    public double AiEscalationThreshold { get; init; } = 0.6;
    public double EarlyExitThreshold { get; init; } = 0.3;
    public double ImmediateBlockThreshold { get; init; } = 0.95;
    public ImmutableDictionary<string, double> WeightOverrides { get; init; }
    public ImmutableList<PolicyTransition> Transitions { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}
```

### Policy Transitions

Define automatic escalation and routing rules.

```csharp
// Escalate to AI when risk exceeds 60%
PolicyTransition.OnHighRisk(0.6, "full-analysis")

// Block immediately when risk exceeds 95%
PolicyTransition.OnHighRisk(0.95, PolicyAction.Block)

// Allow when risk is below 10%
PolicyTransition.OnLowRisk(0.1, PolicyAction.Allow)

// Escalate when specific signal is present
PolicyTransition.OnSignal("ip.is_datacenter", "datacenter-policy")
```
