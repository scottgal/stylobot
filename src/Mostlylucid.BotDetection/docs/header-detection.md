# Header Detection

Header detection analyzes HTTP request headers for patterns that distinguish real browsers from automated clients.

## How It Works

Real browsers send a consistent set of headers with specific patterns. The header detector looks for:

1. **Missing standard headers** - Headers that real browsers always send
2. **Suspicious values** - Generic or malformed header values
3. **Header ordering** - Unusual header order (browser fingerprint)
4. **Automation markers** - Headers added by automation tools

## Expected Browser Headers

Real browsers typically send:

| Header                    | Purpose                  | Absence Impact |
|---------------------------|--------------------------|----------------|
| Accept                    | Content types accepted   | +0.15          |
| Accept-Encoding           | Compression support      | +0.15          |
| Accept-Language           | User language preference | +0.2           |
| Cache-Control             | Caching directives       | +0.15          |
| Connection                | Connection handling      | +0.15          |
| Upgrade-Insecure-Requests | HTTPS preference         | +0.15          |

Missing multiple headers compounds: up to +0.6 confidence.

## Detection Signals

### Missing Accept-Language

Bots often omit locale information:

```
Accept-Language: en-US,en;q=0.9,es;q=0.8  (human)
Accept-Language: *                         (suspicious)
[missing]                                   (bot +0.2)
```

### Generic Accept Header

The `*/*` accept header is suspicious when alone:

```
Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8  (human)
Accept: */*                                                               (bot +0.2)
```

### Automation Headers

Direct markers of automation tools:

| Header           | Indicates                 | Impact |
|------------------|---------------------------|--------|
| X-Requested-With | XMLHttpRequest/automation | +0.4   |
| X-Automation     | Automation framework      | +0.4   |
| X-Bot            | Self-identified bot       | +0.4   |

### Header Ordering

Browsers send headers in consistent order. If User-Agent appears after position 5:

```
Normal: Host, Connection, Accept, Accept-Language, User-Agent, ...
Suspicious: Host, X-Custom, Accept, Content-Type, Cache-Control, User-Agent (position 6+)
```

Impact: +0.1 confidence

### Very Few Headers

Minimal headers indicate automated clients:

```
Real browser: 8-15+ headers
Bot request: 2-4 headers
```

Impact: +0.3 for < 4 headers

## Configuration

```json
{
  "BotDetection": {
    "EnableHeaderAnalysis": true
  }
}
```

Header detection is enabled by default with comprehensive bot detection.

## Integration with Inconsistency Detection

Header signals feed into inconsistency detection:

| Inconsistency                      | Example                          | Impact |
|------------------------------------|----------------------------------|--------|
| Chrome UA without sec-ch-ua        | Modern Chrome sends Client Hints | +0.4   |
| Browser UA without Accept-Language | All browsers send locale         | +0.5   |
| Mobile UA with desktop viewport    | Mismatch in claims               | +0.3   |

## Modern Browser Features

Modern browsers add security headers that bots often miss:

### Sec-Fetch Headers (Chrome 76+)

```
Sec-Fetch-Dest: document
Sec-Fetch-Mode: navigate
Sec-Fetch-Site: none
Sec-Fetch-User: ?1
```

### Client Hints (Chrome 89+)

```
sec-ch-ua: "Google Chrome";v="119", "Chromium";v="119"
sec-ch-ua-mobile: ?0
sec-ch-ua-platform: "Windows"
```

Absence of these with modern User-Agent is suspicious.

## Performance

Header detection is very fast:

| Operation             | Typical Time |
|-----------------------|--------------|
| Header presence check | < 0.01ms     |
| Value analysis        | < 0.1ms      |
| Order analysis        | < 0.05ms     |
| **Total**             | **< 0.2ms**  |

## Accessing Results

```csharp
// Get header-specific reasons
var reasons = context.GetDetectionReasons();
var headerReasons = reasons.Where(r => r.Category == "Headers");

// Example reasons:
// "Missing common browser headers: Accept-Language, Accept-Encoding"
// "Generic Accept header with no Accept-Language"
// "Very few headers present (3)"
```

## Extending Header Detection

Add custom header checks:

```csharp
public class CustomHeaderDetector : IDetector
{
    public string Name => "Custom Header Detector";
    public DetectorStage Stage => DetectorStage.RawSignals;

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken ct)
    {
        var result = new DetectorResult();
        var headers = context.Request.Headers;

        // Check for required API header
        if (!headers.ContainsKey("X-Api-Version"))
        {
            result.Confidence = 0.3;
            result.Reasons.Add(new DetectionReason
            {
                Category = "Custom",
                Detail = "Missing required API version header"
            });
        }

        return Task.FromResult(result);
    }
}
```

## Best Practices

1. **Combine with UA detection** - Headers alone can false-positive on legitimate CLI tools
2. **Consider API clients** - Programmatic access may legitimately have minimal headers
3. **Whitelist paths** - `/api/*` endpoints may need different thresholds
4. **Log for analysis** - Header patterns help identify new bot signatures
