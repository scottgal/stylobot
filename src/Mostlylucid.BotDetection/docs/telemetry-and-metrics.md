# Telemetry and Metrics

Built-in observability via OpenTelemetry.

## Tracing

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("Mostlylucid.BotDetection");
    });
```

### Activity Tags

| Tag                                           | Description                 |
|-----------------------------------------------|-----------------------------|
| `http.client_ip`                              | Client IP address           |
| `http.user_agent`                             | User-Agent header           |
| `mostlylucid.botdetection.is_bot`             | Whether detected as bot     |
| `mostlylucid.botdetection.confidence`         | Confidence score            |
| `mostlylucid.botdetection.bot_type`           | Type of bot                 |
| `mostlylucid.botdetection.bot_name`           | Identified bot name         |
| `mostlylucid.botdetection.processing_time_ms` | Processing time             |
| `mostlylucid.botdetection.reason_count`       | Number of detection reasons |

## Metrics

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("Mostlylucid.BotDetection");
    });
```

### Available Metrics

| Metric                                | Type      | Description                   |
|---------------------------------------|-----------|-------------------------------|
| `botdetection.requests.total`         | Counter   | Total requests processed      |
| `botdetection.bots.detected`          | Counter   | Requests classified as bots   |
| `botdetection.humans.detected`        | Counter   | Requests classified as humans |
| `botdetection.errors.total`           | Counter   | Detection errors              |
| `botdetection.detection.duration`     | Histogram | Detection time (ms)           |
| `botdetection.pattern.match.duration` | Histogram | Pattern matching time (ms)    |
| `botdetection.cidr.match.duration`    | Histogram | CIDR matching time (ms)       |
| `botdetection.cache.patterns.count`   | Gauge     | Compiled patterns in cache    |
| `botdetection.cache.cidr.count`       | Gauge     | Parsed CIDR ranges in cache   |
| `botdetection.confidence.average`     | Gauge     | Recent average confidence     |

## Diagnostic Endpoints

```csharp
app.MapBotDetectionEndpoints("/bot-detection");

// Creates:
//   GET /bot-detection/check   - Check current request
//   GET /bot-detection/stats   - Get detection statistics
//   GET /bot-detection/health  - Health check
```

### Statistics Response

```json
{
  "totalRequests": 1000,
  "botsDetected": 150,
  "botPercentage": 15.0,
  "verifiedBots": 50,
  "maliciousBots": 10,
  "averageProcessingTimeMs": 2.5,
  "botTypeBreakdown": {
    "SearchEngine": 50,
    "SocialMediaBot": 30,
    "Scraper": 60,
    "MaliciousBot": 10
  }
}
```

## Logging

### Log Levels

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Mostlylucid.BotDetection": "Information",
      "Mostlylucid.BotDetection.Services.BotDetectionService": "Debug",
      "Mostlylucid.BotDetection.Services.BotListUpdateService": "Information",
      "Mostlylucid.BotDetection.Data.BotListDatabase": "Warning",
      "Mostlylucid.BotDetection.Middleware": "Information"
    }
  }
}
```

### Logging Options

```json
{
  "BotDetection": {
    "LogAllRequests": false,
    "LogDetailedReasons": true,
    "LogPerformanceMetrics": false,
    "LogIpAddresses": true,
    "LogUserAgents": true
  }
}
```
