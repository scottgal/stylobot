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
- [IBotDetectionService](#ibotdetectionservice)
- [Detection Policies](#detection-policies)

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
    options.Classification.BotFloor = 0.8;
    options.BlockDetectedBots = true;
});
```

### AddBotDetection (IConfiguration overload)

Bind from a non-standard configuration section by name (section name only, not a pre-sliced `IConfigurationSection`).

```csharp
public static IServiceCollection AddBotDetection(
    this IServiceCollection services,
    IConfiguration configuration,
    string sectionName = "BotDetection")
```

```csharp
builder.Services.AddBotDetection(
    builder.Configuration, "MyApp:Security:BotDetection");
```

### AddSimpleBotDetection / AddComprehensiveBotDetection / AddAdvancedBotDetection

Source-compatibility aliases kept so existing integrations (Gateway, Demo, tests, etc.) continue to compile. All three have the exact same signature and behaviour as `AddBotDetection(Action<BotDetectionOptions>?)` -- they do not enable or disable different detector sets, and `AddAdvancedBotDetection` does not take Ollama parameters. Prefer calling `AddBotDetection` directly; configure Ollama/LLM settings via `options.AiDetection` or `appsettings.json` on any of the three.

```csharp
public static IServiceCollection AddSimpleBotDetection(
    this IServiceCollection services,
    Action<BotDetectionOptions>? configure = null)

public static IServiceCollection AddComprehensiveBotDetection(
    this IServiceCollection services,
    Action<BotDetectionOptions>? configure = null)

public static IServiceCollection AddAdvancedBotDetection(
    this IServiceCollection services,
    Action<BotDetectionOptions>? configure = null)
```

```csharp
// All three are equivalent to AddBotDetection(configure):
builder.Services.AddAdvancedBotDetection(options =>
{
    options.EnableLlmDetection = true;
    options.AiDetection.Ollama.Endpoint = "http://ollama-server:11434";
    options.AiDetection.Ollama.Model = "phi3:mini";
});
```

Fail-safe: if Ollama is unavailable, detection continues with heuristics only.

### AddBotDetectionInMemory

Ephemeral mode for integration tests, CI, and the gateway "economy" flag: the full detection pipeline with zero SQLite files on disk. Identity, session learning, and weight learning silently degrade; per-request detection runs unchanged.

```csharp
public static IServiceCollection AddBotDetectionInMemory(
    this IServiceCollection services,
    Action<BotDetectionOptions>? configure = null)
```

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

The middleware stores exactly one key in `HttpContext.Items`:

| Key | Type | Description |
|-----|------|-------------|
| `BotDetection.AggregatedEvidence` | `AggregatedEvidence` | Full orchestrator evidence for the request |

Everything else -- `IsBot`, confidence, bot type/name, category, reasons, policy name/action -- is *not* stored under a separate `Items` key. It's computed on demand from that one `AggregatedEvidence` (or derived/cached lazily) by the `HttpContext` extension methods below (`GetBotDetectionResult()`, `IsBot()`, `GetBotType()`, `GetBotCategory()`, `GetDetectionReasons()`, etc.). Always go through the extension methods rather than reading `HttpContext.Items` directly.

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

### Signal Access

Raw access to every signal the pipeline collected, keyed by the constants on `SignalKeys`.

```csharp
// Full AggregatedEvidence from the pipeline. Null if detection hasn't run.
AggregatedEvidence? GetAggregatedEvidence(this HttpContext context)

// All detection signals as a read-only dictionary.
IReadOnlyDictionary<string, object> GetSignals(this HttpContext context)

// Typed lookup of a single signal by key. Returns default(T) if missing or unconvertible.
T? GetSignal<T>(this HttpContext context, string signalKey)

// Whether a specific signal was raised.
bool HasSignal(this HttpContext context, string signalKey)
```

```csharp
var isVpn = context.GetSignal<bool>(SignalKeys.GeoIsVpn);
var country = context.GetSignal<string>(SignalKeys.GeoCountryCode);
```

### Threat Scoring

Orthogonal to bot probability -- measures malicious *intent*, not bot identity. A human probing `.env` files has low bot probability but high threat score.

```csharp
// Unified threat score (0.0 = benign, 1.0 = malicious).
double GetThreatScore(this HttpContext context)

// Threat band: None, Low, Elevated, High, Critical.
ThreatBand GetThreatBand(this HttpContext context)

// True when ThreatBand >= High, or BotProbability >= 0.5 and ThreatBand >= Elevated.
bool IsMalicious(this HttpContext context)
```

### Geographic / Network

Populated when a geo/network detector (e.g. `IpAtom`) contributed to the request. Return safe defaults (`null`/`false`/`0.0`) when no signal is present.

```csharp
// ISO 3166-1 alpha-2 country code, or null.
string? GetCountryCode(this HttpContext context)

// VPN / proxy / Tor / datacenter (hosting provider) origin.
bool IsVpn(this HttpContext context)
bool IsProxy(this HttpContext context)
bool IsTor(this HttpContext context)
bool IsDatacenter(this HttpContext context)

// Historical bot rate (0.0-1.0) for the request's country of origin.
double GetCountryBotRate(this HttpContext context)
```

### API Key / Impersonation

For requests authenticated with a rich API key (see `ApiKeyConfig`).

```csharp
// API key context for the current request, if a rich key was validated.
ApiKeyContext? GetApiKeyContext(this HttpContext context)

// True if the request has a valid API key (rich or legacy bypass).
bool HasApiKey(this HttpContext context)

// Impersonation target (primary_signature to pin detection identity to), or null.
// Honoured only when the key has AllowImpersonation set; the "X-SB-Impersonate"
// header wins, falling back to the key's bound identity.
string? GetImpersonationTarget(this HttpContext context)

// True when the current request is impersonating a target identity.
bool IsImpersonating(this HttpContext context)

// True when the request's API key (or active impersonation) suppresses
// learning writes -- detection still runs, but reputation/weight updates
// are skipped so debug/impersonated traffic can't poison the model.
bool IsLearningSuppressedByApiKey(this HttpContext context)
```

---

## Endpoint Filters (Minimal API)

**Namespace:** `Mostlylucid.BotDetection.Extensions`

### BlockBots

Block bots from accessing an endpoint. By default blocks ALL bots -- use the `allow*` parameters to whitelist specific types and the `block*`/geo parameters to add geographic or network restrictions.

```csharp
public static RouteHandlerBuilder BlockBots(
    this RouteHandlerBuilder builder,
    bool allowVerifiedBots = false,
    bool allowSearchEngines = false,
    bool allowSocialMediaBots = false,
    bool allowMonitoringBots = false,
    bool allowAiBots = false,
    bool allowGoodBots = false,
    bool allowScrapers = false,
    bool allowMaliciousBots = false,
    bool allowTools = false,
    double minConfidence = 0.0,
    int statusCode = 403,
    string? blockCountries = null,
    string? allowCountries = null,
    bool blockVpn = false,
    bool blockProxy = false,
    bool blockDatacenter = false,
    bool blockTor = false)
```

```csharp
app.MapGet("/api/data", () => "sensitive")
    .BlockBots();

app.MapGet("/api/public", () => "ok")
    .BlockBots(allowSearchEngines: true, minConfidence: 0.8);

app.MapGet("/api/restricted", () => "data")
    .BlockBots(blockCountries: "CN,RU", blockVpn: true);
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

Maps four diagnostic endpoints under a configurable prefix, plus the PoW challenge-verification endpoints via `endpoints.MapChallengeEndpoints()`.

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

#### `POST /bot-detection/feedback`

Submit detection feedback (false positive/negative) for logging.

```json
// Request body
{
  "outcome": "Human",
  "requestId": "abc123",
  "notes": "Flagged wrongly for missing Accept-Language"
}
```

```json
// Response
{
  "accepted": true,
  "outcome": "Human",
  "requestId": "abc123"
}
```

`outcome` must be `"Human"` or `"Bot"`. `notes` is capped at 500 characters, `requestId` at 128.

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
    "Classification": {
      "HumanCeiling": 0.30,
      "BotFloor": 0.70
    },
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
| `Classification.HumanCeiling` | `double` | `0.30` | `bot_probability < HumanCeiling` ⇒ Human |
| `Classification.BotFloor` | `double` | `0.70` | `bot_probability >= BotFloor` ⇒ Bot; the single "is a bot" cut used everywhere, including `IsBot()`. Replaces the obsolete `BotThreshold` property, which is no longer read by classification. |
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
| `DefaultActionPolicyName` | `string?` | `"throttle-stealth"` | Default action policy when detection triggers blocking and no per-`BotType` entry in `BotTypeActionPolicies` matches |
| `ResponsePiiMasking` | `ResponsePiiMaskingOptions` | defaults | Response mutation settings for `mask-pii`/`strip-pii` (disabled by default) |
| `StorageProvider` | `StorageProvider` | `Sqlite` | Storage backend for bot patterns and IP ranges: `Sqlite` or `Json`. PostgreSQL persistence is a commercial (non-FOSS) feature and is not configured through this enum. |

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
    public double ProcessingTimeMs { get; set; }         // Fractional; Stopwatch.Elapsed.TotalMilliseconds
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

Full orchestrator output. Prefer `context.GetAggregatedEvidence()` over reading `HttpContext.Items["BotDetection.AggregatedEvidence"]` directly.

```csharp
public sealed record AggregatedEvidence
{
    public DetectionLedger? Ledger { get; init; }                  // Underlying ledger (source of truth)
    public required double BotProbability { get; init; }          // 0.0=human, 1.0=bot
    public required double Confidence { get; init; }              // Classification certainty
    public required RiskBand RiskBand { get; init; }
    public string RiskJustification { get; init; }
    public BotType? PrimaryBotType { get; init; }
    public string? PrimaryBotName { get; init; }
    public bool EarlyExit { get; init; }
    public EarlyExitVerdict? EarlyExitVerdict { get; init; }
    public bool AiRan { get; init; }
    public double ThreatScore { get; init; }                      // 0.0=benign, 1.0=malicious; orthogonal to BotProbability
    public ThreatBand ThreatBand { get; init; }
    public double TotalProcessingTimeMs { get; init; }
    public IReadOnlySet<string> ContributingDetectors { get; init; }
    public IReadOnlySet<string> FailedDetectors { get; init; }
    public IReadOnlyDictionary<string, object> Signals { get; init; }
    public IReadOnlyDictionary<string, CategoryScore> CategoryBreakdown { get; init; }
    public IReadOnlyList<DetectionContribution> Contributions { get; }  // Computed from Ledger, read-only
    public string? PolicyName { get; init; }
    public DetectionPolicyAction? PolicyAction { get; init; }
    public string? TriggeredActionPolicyName { get; init; }
}
```

### ThreatBand

```csharp
public enum ThreatBand
{
    None,       // 0.0 - 0.15
    Low,        // 0.15 - 0.35
    Elevated,   // 0.35 - 0.55
    High,       // 0.55 - 0.80
    Critical    // 0.80 - 1.0
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
    AiBot,              // GPTBot, ClaudeBot, etc.
    Tool,               // Legitimate CLI/dev tools (curl, Postman, etc.)
    ExploitScanner,     // Vulnerability/CVE scanners
    ClickFraud,         // Ad-fraud click bots
    Internal            // Loopback/RFC1918/docker-bridge traffic the operator owns
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

### DetectionPolicyAction

```csharp
public enum DetectionPolicyAction
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

> Renamed from the bare name `PolicyAction` so the new authored-rule
> action record at `Mostlylucid.BotDetection.Policies.Rules.PolicyAction`
> is unambiguous inside the `Mostlylucid.BotDetection.Policies.*`
> namespace tree.

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

**Namespace:** `Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms` (interface/base class, external package) and `Mostlylucid.BotDetection.Orchestration.Atoms` (registration extension)

Detectors are `IDetectorAtom` implementations from the `Mostlylucid.Ephemeral.Atoms.Taxonomy` package -- the same interface every built-in detector (`HeaderAtom`, `UserAgentAtom`, `IpAtom`, etc., under `Orchestration/Atoms/`) implements. There is no `BlackboardState`/`ContributeAsync` shape in the current codebase; that was a legacy contract removed in favour of atoms reading/writing a `SignalSink` directly.

### IDetectorAtom Interface

```csharp
public interface IDetectorAtom
{
    string Name { get; }
    string Category { get; }
    int Priority { get; }
    bool IsEnabled { get; }
    TimeSpan Timeout { get; }
    bool IsOptional { get; }
    IReadOnlyList<string> RequiredSignals { get; }

    Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default);
}
```

### DetectorAtomBase

Abstract base class with helpers. `RequiredSignals` gates *when* the orchestrator schedules the atom (which wave); an empty list means it can run in the first wave.

```csharp
public abstract class DetectorAtomBase : IDetectorAtom
{
    protected DetectorAtomBase(string name, string category);

    public string Name { get; }
    public string Category { get; }
    public virtual int Priority => 50;
    public virtual bool IsEnabled => true;
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(2);
    public virtual bool IsOptional => false;
    public virtual IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public abstract Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink, string sessionId, CancellationToken ct = default);

    // Helpers:
    protected IReadOnlyList<DetectionContribution> Single(DetectionContribution c);
    protected IReadOnlyList<DetectionContribution> Multiple(params DetectionContribution[] c);
    protected IReadOnlyList<DetectionContribution> None();
    protected DetectionContribution Bot(double confidence, string reason, double weight = 1.0, string? botType = null, string? botName = null, Dictionary<string, object>? signals = null);
    protected DetectionContribution Human(double confidence, string reason, double weight = 1.0, Dictionary<string, object>? signals = null);
    protected bool HasSignal(SignalSink sink, string pattern);
    protected IReadOnlyList<SignalEvent> GetSignals(SignalSink sink, string pattern);
}
```

### DetectionContribution

```csharp
public sealed record DetectionContribution
{
    public string DetectorName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public double ConfidenceDelta { get; init; }   // Positive = more bot-like, negative = more human-like
    public double Weight { get; init; } = 1.0;
    public string Reason { get; init; } = string.Empty;
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public IReadOnlyDictionary<string, object> Signals { get; init; }

    public static DetectionContribution Bot(string detectorName, string category, double confidence, string reason, double weight = 1.0, string? botType = null, string? botName = null, Dictionary<string, object>? signals = null);
    public static DetectionContribution Human(string detectorName, string category, double confidence, string reason, double weight = 1.0, Dictionary<string, object>? signals = null);
    public static DetectionContribution Info(string detectorName, string category, string reason, Dictionary<string, object>? signals = null);
}
```

### Example Custom Detector

Modelled on the shape of the built-in `HeaderAtom` (`Orchestration/Atoms/HeaderAtom.cs`):

```csharp
public sealed class GeoFenceAtom : DetectorAtomBase
{
    public GeoFenceAtom() : base(name: "GeoFence", category: "GeoFence") { }

    public override int Priority => 50;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.GeoCountryCode };

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink, string sessionId, CancellationToken ct = default)
    {
        var country = sink.ReadHint(SignalKeys.GeoCountryCode);

        if (country is "XX") // Blocked country
        {
            return Task.FromResult(Single(Bot(
                confidence: 0.3,
                reason: $"Request from blocked country: {country}",
                weight: 1.5,
                signals: new Dictionary<string, object>
                {
                    ["geofence.blocked"] = true,
                    ["geofence.country"] = country
                })));
        }

        return Task.FromResult(None());
    }
}
```

Register with `AddDetectorAtom<T>()` (from `Mostlylucid.BotDetection.Orchestration.Atoms`), which wires both the `IDetectorAtom` binding and the name-marker the orchestrator uses to avoid double-registering built-ins:

```csharp
services.AddDetectorAtom<GeoFenceAtom>();
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

    // Record a detection result against the statistics counters. Called by the
    // middleware after the orchestrator produces a verdict, by DetectAsync
    // internally, and by anyone running detection out-of-band (demo preload,
    // on-demand endpoint filter, test harnesses). Idempotent per call; the
    // caller decides whether to record a verdict at all.
    void RecordDetection(BotDetectionResult result);
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
| `DetectionPolicy.AllowVerifiedBots` | Allows verified good bots (search engines, social media) through |
| `DetectionPolicy.YarpLearning` | YARP gateway learning: full pipeline without LLM, for training data collection |
| `DetectionPolicy.Profile` | Fingerprint-only detection for threshold calibration; never blocks inline |

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
PolicyTransition.OnHighRisk(0.95, DetectionPolicyAction.Block)

// Allow when risk is below 10% -- route to a more permissive named policy
// (OnLowRisk only takes a goToPolicy name, not a DetectionPolicyAction)
PolicyTransition.OnLowRisk(0.1, "relaxed")

// Escalate when specific signal is present
PolicyTransition.OnSignal("ip.is_datacenter", "datacenter-policy")
```
