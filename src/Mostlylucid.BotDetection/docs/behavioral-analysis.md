# Behavioral Analysis

Monitors request patterns at multiple identity levels to detect automated traffic.

## Identity Levels

The behavioral detector tracks requests at four identity levels simultaneously:

1. **IP Address** (default) - Basic rate limiting per IP
2. **Browser Fingerprint** - Per fingerprint hash (when client-side enabled)
3. **API Key** - Per API key (via configurable header)
4. **User ID** - Per authenticated user (via claim or header)

## Configuration

```json
{
  "BotDetection": {
    "MaxRequestsPerMinute": 60,
    "Behavioral": {
      "ApiKeyHeader": "X-Api-Key",
      "ApiKeyRateLimit": 120,
      "UserIdClaim": "sub",
      "UserIdHeader": "X-User-Id",
      "UserRateLimit": 180,
      "EnableAnomalyDetection": true,
      "SpikeThresholdMultiplier": 5.0,
      "NewPathAnomalyThreshold": 0.8
    }
  }
}
```

## Settings Reference

| Setting                    | Default | Description                                            |
|----------------------------|---------|--------------------------------------------------------|
| `ApiKeyHeader`             | `null`  | Header name for API key extraction                     |
| `ApiKeyRateLimit`          | `0`     | Rate limit per API key (0 = 2x `MaxRequestsPerMinute`) |
| `UserIdClaim`              | `null`  | JWT claim for user ID                                  |
| `UserIdHeader`             | `null`  | Header name for user ID (fallback)                     |
| `UserRateLimit`            | `0`     | Rate limit per user (0 = 3x `MaxRequestsPerMinute`)    |
| `EnableAnomalyDetection`   | `true`  | Detect behavior changes                                |
| `SpikeThresholdMultiplier` | `5.0`   | Request spike = this × normal rate                     |
| `NewPathAnomalyThreshold`  | `0.8`   | Flag if 80%+ recent requests hit new paths             |

## Detection Signals

### Rate Limiting

- Excessive requests per IP
- Excessive requests per API key
- Excessive requests per user
- Excessive requests per fingerprint hash

### Timing Analysis

- Rapid sequential requests (<100ms between requests)
- Suspiciously regular intervals (low standard deviation = bot-like)

### Anomaly Detection

- Sudden request spikes (5x normal rate by default)
- Accessing many new endpoints suddenly
- Behavior profile changes

### Session Consistency

- Missing cookies across multiple requests
- Missing referrer on non-initial requests

## Example: API Gateway

For API gateways with strict per-key limits:

```json
{
  "BotDetection": {
    "MaxRequestsPerMinute": 100,
    "Behavioral": {
      "ApiKeyHeader": "Authorization",
      "ApiKeyRateLimit": 1000,
      "EnableAnomalyDetection": true,
      "SpikeThresholdMultiplier": 10.0
    }
  }
}
```

## Example: Multi-Tenant SaaS

Track by tenant ID from JWT:

```json
{
  "BotDetection": {
    "Behavioral": {
      "UserIdClaim": "tenant_id",
      "UserRateLimit": 500
    }
  }
}
```

## Accessing Results

```csharp
var reasons = context.GetDetectionReasons();
var behavioralReasons = reasons.Where(r => r.Category == "Behavioral");

foreach (var reason in behavioralReasons)
{
    if (reason.Detail.Contains("API key rate limit"))
        return Results.StatusCode(429);

    if (reason.Detail.Contains("Sudden request spike"))
        return Results.StatusCode(503);
}
```
