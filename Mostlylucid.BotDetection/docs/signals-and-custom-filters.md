# Signals and Custom Filters

StyloBot's detection pipeline produces 100+ signals — typed key-value pairs representing everything the detectors discovered about a request. This document covers how to read signals, filter by them, and build custom protection logic.

## Signal Architecture

Every detector in the pipeline writes signals to a shared blackboard. After detection completes, all signals are merged into `AggregatedEvidence.Signals` and stored in `HttpContext.Items`.

```
Request → Orchestrator
  ├─ UserAgentContributor → ua.is_bot, ua.bot_type, ua.bot_name
  ├─ IpContributor       → ip.is_datacenter, ip.is_known_bot_ip
  ├─ GeoContributor      → geo.country_code, geo.is_vpn, geo.is_tor
  ├─ HeaderContributor   → header.missing_count, header.suspicious_count
  ├─ HeuristicDetector   → heuristic.confidence, heuristic.probability
  └─ ... (21+ detectors)
      ↓
  AggregatedEvidence.Signals (merged dictionary)
      ↓
  HttpContext.Items → accessible via GetSignals(), GetSignal<T>()
```

Signal keys are defined as constants in `SignalKeys` (in `Mostlylucid.BotDetection.Models`).

## Reading Signals from HttpContext

### Generic Signal Access

```csharp
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

// Get all signals as a dictionary
IReadOnlyDictionary<string, object> signals = context.GetSignals();

// Get a typed signal value
string? country = context.GetSignal<string>(SignalKeys.GeoCountryCode);
bool isVpn = context.GetSignal<bool>(SignalKeys.GeoIsVpn);
double heuristicScore = context.GetSignal<double>(SignalKeys.HeuristicConfidence);
bool isDatacenter = context.GetSignal<bool>(SignalKeys.IpIsDatacenter);

// Check if a signal exists
if (context.HasSignal(SignalKeys.GeoCountryCode))
{
    // Geo detection ran and produced a country code
}

// Access the full AggregatedEvidence
var evidence = context.GetAggregatedEvidence();
if (evidence != null)
{
    var processingMs = evidence.TotalProcessingTimeMs;
    var detectors = evidence.ContributingDetectors;
    var contributions = evidence.Contributions; // Per-detector breakdown
}
```

### Geo / Network Helper Methods

Convenience methods for common geo and network checks:

```csharp
// Country
string? country = context.GetCountryCode();     // "US", "CN", "DE", etc.
double botRate = context.GetCountryBotRate();    // 0.0-1.0, higher = more bot traffic

// Network type
bool vpn = context.IsVpn();
bool proxy = context.IsProxy();
bool tor = context.IsTor();
bool datacenter = context.IsDatacenter();        // AWS, Azure, GCP, etc.
```

These require the GeoDetection contributor to be registered. Without it, they return `false`/`null`/`0.0`.

## Signal-Based Filtering

### MVC Attributes

Use `[BlockIfSignal]` to block when a condition is met, or `[RequireSignal]` to block when a condition is NOT met.

```csharp
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Models;

[ApiController]
[Route("[controller]")]
public class PaymentController : ControllerBase
{
    // Block VPN connections
    [HttpPost("charge")]
    [BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True")]
    public IActionResult Charge() => Ok(new { charged = true });

    // Block datacenter IPs (bots running on AWS/Azure/GCP)
    [HttpPost("submit")]
    [BlockIfSignal(SignalKeys.IpIsDatacenter, SignalOperator.Equals, "True")]
    public IActionResult Submit() => Ok(new { submitted = true });

    // Block requests from China
    [HttpGet("restricted")]
    [BlockIfSignal(SignalKeys.GeoCountryCode, SignalOperator.Equals, "CN")]
    public IActionResult Restricted() => Ok(new { data = "restricted" });

    // Combine multiple conditions (AND logic — all must match to block)
    [HttpPost("wire-transfer")]
    [BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True")]
    [BlockIfSignal(SignalKeys.IpIsDatacenter, SignalOperator.Equals, "True")]
    [BlockIfSignal(SignalKeys.GeoIsTor, SignalOperator.Equals, "True")]
    public IActionResult WireTransfer() => Ok(new { transferred = true });

    // Only allow US traffic
    [HttpGet("domestic")]
    [RequireSignal(SignalKeys.GeoCountryCode, SignalOperator.Equals, "US")]
    public IActionResult DomesticOnly() => Ok(new { data = "US only" });

    // Block high-confidence bots (using heuristic score)
    [HttpGet("premium")]
    [BlockIfSignal(SignalKeys.HeuristicConfidence, SignalOperator.GreaterThan, "0.9")]
    public IActionResult Premium() => Ok(new { premium = true });
}
```

### Minimal API Filters

```csharp
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Models;

// Block VPN connections
app.MapPost("/api/payment", () => Results.Ok())
   .BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True");

// Block datacenter IPs
app.MapPost("/api/submit", () => Results.Ok())
   .BlockIfSignal(SignalKeys.IpIsDatacenter, SignalOperator.Equals, "True");

// Only allow US traffic
app.MapGet("/api/domestic", () => Results.Ok())
   .RequireSignal(SignalKeys.GeoCountryCode, SignalOperator.Equals, "US");

// Combine with bot blocking — block bots AND VPNs
app.MapPost("/api/checkout", () => Results.Ok())
   .BlockBots()
   .BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True");
```

### Custom Inline Logic

For complex conditions that don't fit the attribute model:

```csharp
app.MapPost("/api/transfer", (HttpContext ctx) =>
{
    // Custom multi-signal logic
    if (ctx.IsVpn() && ctx.IsDatacenter())
        return Results.Json(new { error = "VPN + datacenter blocked" }, statusCode: 403);

    if (ctx.GetCountryBotRate() > 0.7)
        return Results.Json(new { error = "High-risk country" }, statusCode: 403);

    var heuristicScore = ctx.GetSignal<double>(SignalKeys.HeuristicConfidence);
    if (heuristicScore > 0.95 && ctx.IsBot())
        return Results.Json(new { error = "Very high confidence bot" }, statusCode: 403);

    return Results.Ok(new { transferred = true });
});
```

## Available Signal Operators

| Operator | Description | Example |
|----------|-------------|---------|
| `Equals` | Exact match (case-insensitive) | Country == "US" |
| `NotEquals` | Inverse match | Country != "CN" |
| `GreaterThan` | Numeric comparison | Confidence > 0.9 |
| `LessThan` | Numeric comparison | BotRate < 0.3 |
| `GreaterThanOrEqual` | Numeric comparison | Score >= 0.5 |
| `LessThanOrEqual` | Numeric comparison | Score <= 0.1 |
| `Contains` | Substring match (case-insensitive) | UA contains "bot" |
| `Exists` | Signal key exists (any value) | Geo data present |

## Common Signal Keys

### Core Detection (`SignalKeys`)

| Key | Type | Source | Description |
|-----|------|--------|-------------|
| `ua.is_bot` | `bool` | UserAgent | User-Agent matches a known bot pattern |
| `ua.bot_type` | `string` | UserAgent | Bot type (SearchEngine, Scraper, etc.) |
| `ua.bot_name` | `string` | UserAgent | Bot name (Googlebot, AhrefsBot, etc.) |
| `ip.is_datacenter` | `bool` | Ip | IP belongs to a cloud/hosting provider |
| `heuristic.confidence` | `double` | Heuristic | Heuristic model confidence (0.0-1.0) |
| `heuristic.probability` | `double` | Heuristic | Bot probability from heuristic model |
| `header.missing_count` | `int` | Header | Number of expected headers missing |
| `header.suspicious_count` | `int` | Header | Number of suspicious header patterns |
| `behavioral.request_rate` | `double` | Behavioral | Requests per second from this client |

### Geographic (`SignalKeys` — requires GeoDetection contributor)

| Key | Type | Source | Description |
|-----|------|--------|-------------|
| `geo.country_code` | `string` | Geo | ISO 3166-1 alpha-2 country code |
| `geo.is_vpn` | `bool` | Geo | VPN connection detected |
| `geo.is_proxy` | `bool` | Geo | Proxy server detected |
| `geo.is_tor` | `bool` | Geo | Tor exit node detected |
| `geo.is_hosting` | `bool` | Geo | Datacenter/hosting IP detected |
| `geo.country_bot_rate` | `double` | Geo | Country's bot traffic ratio (0.0-1.0) |
| `geo.country_bot_rank` | `int` | Geo | Country's rank in bot traffic |

### Advanced (separate contributor packages)

| Key | Source | Description |
|-----|--------|-------------|
| `geo.bot_verified` | GeoContributor | Bot origin matches claimed identity |
| `geo.bot_origin_mismatch` | GeoContributor | Bot claims one origin but comes from another |
| `geo.locale_mismatch` | GeoContributor | Accept-Language doesn't match country |
| `geo.timezone_mismatch` | GeoContributor | Client timezone doesn't match geo location |

For a complete list of all signal keys, see the `SignalKeys` class in `Mostlylucid.BotDetection.Models.DetectionContext`.

## GeoDetection Integration

### How It Works

The GeoDetection contributor (`Mostlylucid.GeoDetection.Contributor`) runs as a Wave 0 detector alongside UserAgent and IP detection. It:

1. Looks up the client IP in a geo database (MaxMind GeoLite2 or ip-api.com)
2. Determines country, city, coordinates, timezone
3. Checks for VPN, proxy, Tor, and datacenter connections
4. Verifies bot origins (e.g., "Googlebot should come from Google's IP ranges")
5. Detects locale/timezone mismatches
6. Writes all results as signals to the blackboard

### Registration

```csharp
// Register GeoDetection contributor alongside bot detection
builder.Services.AddBotDetection();
builder.Services.AddGeoDetection(options =>
{
    options.DatabasePath = "/path/to/GeoLite2-City.mmdb";
    // OR use ip-api.com (free for non-commercial, no database needed)
    options.UseIpApi = true;
});
```

### Using Geo Signals

Once registered, geo signals are available everywhere:

```csharp
// In controllers
[BlockBots(AllowSearchEngines = true, BlockCountries = "CN,RU")]
public IActionResult Products() => View();

// With signal-based filtering
[BlockIfSignal(SignalKeys.GeoIsVpn, SignalOperator.Equals, "True")]
public IActionResult Payment() => View();

// In middleware or inline code
app.MapGet("/api/info", (HttpContext ctx) => Results.Ok(new
{
    country = ctx.GetCountryCode(),
    isVpn = ctx.IsVpn(),
    isTor = ctx.IsTor(),
    isDatacenter = ctx.IsDatacenter(),
    countryBotRate = ctx.GetCountryBotRate()
}));
```

### Bot Origin Verification

GeoDetection cross-references bot claims against known IP ranges. If a request claims to be Googlebot but comes from a Chinese datacenter, GeoDetection flags this:

```
Signal: geo.bot_verified = false
Signal: geo.bot_origin_mismatch = true
```

This feeds into the overall bot probability, increasing confidence that the request is a spoofed bot.

### Without GeoDetection

If GeoDetection is not registered:
- Geo signals (`geo.*`) will not exist
- `context.GetCountryCode()` returns `null`
- `context.IsVpn()` returns `false`
- `[BlockBots(BlockCountries = "CN")]` still works via upstream headers (YARP gateway)
- `[BlockIfSignal(SignalKeys.GeoCountryCode, ...)]` won't match (signal absent)

## Writing a Custom Filter Attribute

For complex logic that doesn't fit `BlockIfSignal`/`RequireSignal`, write a custom attribute:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

/// <summary>
///     Blocks requests from high-risk countries when coming through VPN or datacenter.
///     Allows the same countries when connecting directly (residential IP).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BlockSuspiciousGeoAttribute : ActionFilterAttribute
{
    private static readonly HashSet<string> HighRiskCountries = ["CN", "RU", "KP", "IR"];

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;
        var country = httpContext.GetCountryCode();

        if (country != null
            && HighRiskCountries.Contains(country)
            && (httpContext.IsVpn() || httpContext.IsDatacenter() || httpContext.IsTor()))
        {
            context.Result = new ObjectResult(new
            {
                error = "Access denied",
                reason = "Suspicious network from high-risk region"
            })
            { StatusCode = 403 };
            return;
        }

        base.OnActionExecuting(context);
    }
}

// Usage:
[BlockSuspiciousGeo]
public IActionResult SensitiveEndpoint() => Ok();
```

### Custom Endpoint Filter (Minimal API)

```csharp
public class BlockSuspiciousGeoFilter : IEndpointFilter
{
    private static readonly HashSet<string> HighRiskCountries = ["CN", "RU", "KP", "IR"];

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var country = http.GetCountryCode();

        if (country != null
            && HighRiskCountries.Contains(country)
            && (http.IsVpn() || http.IsDatacenter()))
        {
            return Results.Json(new { error = "Suspicious network" }, statusCode: 403);
        }

        return await next(context);
    }
}

// Usage:
app.MapPost("/api/payment", () => Results.Ok())
   .AddEndpointFilter<BlockSuspiciousGeoFilter>();
```

## Further Reading

- [blocking-and-filters.md](blocking-and-filters.md) — Bot type filtering, geo blocking via attributes
- [quickstart.md](quickstart.md) — Getting started with zero dependencies
- [action-policies.md](action-policies.md) — Config-driven response policies
- [configuration.md](configuration.md) — Full `BotDetectionOptions` reference
