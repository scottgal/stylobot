# Cache Behavior Detection

## Overview

The **CacheBehavior** contributor detects bots by analyzing HTTP caching behavior patterns. Real browsers implement
sophisticated HTTP caching mechanisms (ETags, Last-Modified headers, compression support), while many bots and scrapers
ignore or skip these features to simplify their implementation.

## How It Works

### Detection Signals

The contributor analyzes four key aspects of HTTP caching behavior:

#### 1. Missing Cache Validation Headers

**Signal:** `cache.validation_missing`

Real browsers send cache validation headers on subsequent requests:

- `If-None-Match` (ETag validation)
- `If-Modified-Since` (Last-Modified validation)

Missing these headers on repeated resource requests is suspicious, as it indicates:

- The client is not caching resources
- The client is ignoring standard HTTP caching mechanisms
- Likely a bot fetching pages without optimization

**Weight:** 1.3
**Confidence Delta:** 0.3

#### 2. Missing Compression Support

**Signal:** `cache.compression_missing`

Modern browsers universally support HTTP compression:

- `Accept-Encoding: gzip, deflate, br`

Missing this header entirely suggests:

- Legacy or custom HTTP client
- Bot/scraper without compression support
- Simplified client implementation

**Weight:** 1.2
**Confidence Delta:** 0.25

#### 3. Rapid Repeated Requests Without Caching

**Signal:** `cache.rapid_repeated`

When a client makes multiple requests to the same resource path within a short time window (30 seconds) WITHOUT sending
cache validation headers, it indicates:

- The client is not caching at all
- Each request is treated as a fresh fetch
- Typical bot behavior (fetching pages repeatedly without state)

**Weight:** 1.4
**Confidence Delta:** 0.35

#### 4. Low Cache Validation Rate

**Signal:** `cache.behavior_anomaly`

Tracks the ratio of cache-validated requests to total requests over a session window. If the validation rate is < 20%:

- The client rarely validates cached resources
- Inconsistent with normal browser behavior
- Suggests simplified or non-compliant HTTP client

**Weight:** 1.2
**Confidence Delta:** 0.25

### Positive Signals (Human-like Behavior)

When a client consistently shows good caching behavior:

- Sends validation headers appropriately
- Supports compression
- Validates 30%+ of requests

**Weight:** 1.0
**Confidence Delta:** -0.15 (reduces bot probability)

## Privacy & Security

The CacheBehavior contributor is **privacy-preserving**:

- Uses **hashed identity keys** (IP + salt) - NO PII storage
- Identity signatures are deterministic (same IP always produces same hash)
- The salt is configurable via `Behavioral.IdentityHashSalt`
- Tracking windows are SHORT - entries clear quickly

## Configuration

```json
{
  "BotDetection": {
    "Behavioral": {
      "AnalysisWindow": "00:15:00",           // 15-minute tracking window
      "IdentityHashSalt": "your-secret-salt",  // Keep this secret!
      "EnableAdvancedPatternDetection": true   // Enables statistical analysis
    }
  }
}
```

### Configuration Options

| Option                           | Description                                  | Default     |
|----------------------------------|----------------------------------------------|-------------|
| `AnalysisWindow`                 | Time window for tracking caching behavior    | 15 minutes  |
| `IdentityHashSalt`               | Salt for hashing IP addresses (KEEP SECRET!) | Random GUID |
| `EnableAdvancedPatternDetection` | Enable cache behavior analysis               | true        |

## Example Detection

### Bot Pattern

```http
GET /page.html HTTP/1.1
Host: example.com
User-Agent: python-requests/2.28.1
# Missing Accept-Encoding
# Missing cache validation headers

GET /page.html HTTP/1.1  (30 seconds later - same resource)
Host: example.com
User-Agent: python-requests/2.28.1
# Still no cache validation
# No If-None-Match or If-Modified-Since
```

**Detection Result:**

- Missing validation headers: +0.3 confidence
- Missing compression: +0.25 confidence
- Rapid repeated without cache: +0.35 confidence
- **Total contribution: ~0.7 bot probability**

### Human Pattern

```http
GET /page.html HTTP/1.1
Host: example.com
User-Agent: Mozilla/5.0 ...
Accept-Encoding: gzip, deflate, br
# ETag: "abc123" stored from response

GET /page.html HTTP/1.1  (5 minutes later)
Host: example.com
User-Agent: Mozilla/5.0 ...
Accept-Encoding: gzip, deflate, br
If-None-Match: "abc123"
# Returns 304 Not Modified
```

**Detection Result:**

- Good caching behavior detected: -0.15 confidence
- **Contributes to human classification**

## Priority & Wave Execution

- **Priority:** 15 (runs early in detection pipeline)
- **Trigger Conditions:** None (runs in Wave 0 alongside basic detectors)
- **Execution:** Lightweight, completes in <1ms

This allows cache behavior signals to influence downstream detectors through the blackboard architecture.

## Use Cases

### 1. Scraper Detection

Scrapers often skip caching to ensure they get fresh content on every fetch, making them easy to identify.

### 2. Bot Framework Detection

Many bot frameworks (Puppeteer, Selenium in headless mode) implement partial caching, leading to suspicious patterns.

### 3. API Client Detection

Programmatic API clients often don't implement full HTTP caching semantics, revealing themselves through missing
headers.

## Technical Details

### Hashing & Identity

```csharp
private string HashIdentity(string identityKey)
{
    // Combine identity with salt for deterministic hashing
    var salted = $"{identityKey}:{_salt}";
    var bytes = Encoding.UTF8.GetBytes(salted);
    var hash = XxHash64.Hash(bytes);
    return Convert.ToHexString(hash);  // This IS the signature for lookups
}
```

- **Algorithm:** XxHash64 (fast, non-cryptographic)
- **Salt:** User-configurable for multi-tenant isolation
- **Output:** 16-character hex string
- **Collision Resistance:** Extremely low probability with 64-bit hash space

### Tracking Window

The contributor uses ephemeral.complete's SHORT tracking semantics:

- Entries are tracked for `AnalysisWindow` duration (default 15 minutes)
- Entries automatically expire after window elapses
- Memory efficient - no long-term storage
- Adapts to request pressure

## Integration

The CacheBehavior contributor is automatically registered when using bot detection. To ensure it runs:

1. **Add to policy paths** in `appsettings.json`:

```json
{
  "Policies": {
    "default": {
      "FastPath": [
        "FastPathReputation",
        "CacheBehavior",    // <-- Add here
        "Behavioral",
        "..."
      ]
    }
  }
}
```

2. **Verify it's running** - check detection output:

```json
{
  "detectorsRan": ["...", "CacheBehavior", "..."],
  "contributions": [
    {
      "detectorName": "CacheBehavior",
      "category": "CacheBehavior",
      "confidenceDelta": 0.3,
      "reason": "Missing cache validation headers"
    }
  ]
}
```

## Best Practices

1. **Set a stable salt** - Don't use the default random GUID in production
2. **Monitor validation rates** - Track how often your users validate caches
3. **Tune thresholds** - Adjust confidence deltas based on your traffic patterns
4. **Combine with other detectors** - Cache behavior is one signal among many

## Limitations

1. **First Request Exemption** - The first request to a resource naturally has no validation headers
2. **Private Browsing** - Incognito/private modes may exhibit less caching
3. **Mobile Browsers** - Some mobile browsers are more aggressive about cache invalidation
4. **CDN Caching** - CDN-level caching may mask client-side caching behavior

The contributor accounts for these by using weighted contributions rather than binary decisions.

## See Also

- [Advanced Behavioral Detection](./advanced-behavioral-detection.md) - Statistical pattern analysis
- [Behavioral Detection](./behavioral-analysis.md) - Rate limiting and session tracking
- [Privacy & Security](./privacy-security.md) - PII protection and hashing strategies
