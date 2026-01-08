# YARP Learning Mode - Training Data Collection Guide

## Overview

YARP Learning Mode is a specialized configuration for collecting comprehensive bot detection training data in gateway/reverse proxy scenarios. It runs the **full detection pipeline** on every request (except LLM for performance) and outputs detailed signatures to files and console.

**This is a TRAINING/DEBUGGING mode - NOT for production blocking!**

## Key Features

✅ **Full Pipeline Execution** - Runs ALL detectors (fast + slow + ONNX)
✅ **No LLM** - Skips LLM detector for better gateway performance
✅ **Comprehensive Signatures** - Captures all signals, detectors, and outputs
✅ **JSONL Output** - Streaming format for large datasets
✅ **Console Logging** - Real-time visibility of all detection signals
✅ **Shadow Mode** - Never blocks requests (ImmediateBlockThreshold = 1.0)
✅ **Configurable Sampling** - Process only N% of traffic to reduce impact

## Use Cases

1. **Training Data Collection** - Gather labeled examples for ML training
2. **Debugging Detection Logic** - See exactly what each detector contributes
3. **Performance Testing** - Measure full pipeline latency under load
4. **Signature Analysis** - Understand bot patterns in your traffic
5. **Policy Tuning** - Calibrate thresholds based on real data

## Configuration

### Basic Setup

Add to your `appsettings.json`:

```json
{
  "BotDetection": {
    "Enabled": true,
    "DefaultPolicyName": "yarp-learning"  // Use YARP learning policy globally
  },
  "YarpLearningMode": {
    "Enabled": true,
    "OutputPath": "./yarp-learning-data",
    "LogToConsole": true,
    "SamplingRate": 1.0  // 100% of traffic
  }
}
```

### Advanced Configuration

```json
{
  "YarpLearningMode": {
    // === Core Settings ===
    "Enabled": true,
    "OutputPath": "./yarp-learning-data",
    "FileFormat": "jsonl",  // or "json"

    // === Logging ===
    "LogToConsole": true,  // Log all signals to console
    "MinConfidenceToLog": 0.0,  // Log everything (0.0-1.0)

    // === Detector Control ===
    "IncludeLlmDetector": false,  // Skip LLM for performance (recommended)
    "IncludeOnnxDetector": true,  // Include ONNX for ML signals

    // === Performance ===
    "SamplingRate": 0.1,  // Only process 10% of requests
    "BufferSize": 100,  // Flush after 100 signatures

    // === Data Collection ===
    "IncludeFullHttpContext": false,  // Exclude headers/cookies (PII)
    "IncludeRequestBody": false,  // Exclude request body (PII)
    "IncludeDetectorOutputs": true,  // Include per-detector results
    "IncludeBlackboardSignals": true,  // Include all intermediate signals

    // === File Rotation ===
    "UseTimestampedFiles": true,  // signatures_2024-01-15.jsonl
    "Rotation": {
      "MaxSizeBytes": 104857600,  // 100MB
      "MaxAgeHours": 24,  // Daily rotation
      "MaxFiles": 30  // Keep 30 days
    },

    // === Path Filtering ===
    "ExcludePaths": [
      "/health",
      "/healthz",
      "/ping",
      "/metrics"
    ]
  }
}
```

## Output Format

### JSONL Signature File

Each line is a complete JSON object representing one request:

```jsonl
{"signatureId":"sig_abc123","timestamp":"2024-01-15T10:30:00Z","path":"/api/data","method":"GET","clientIp":"203.0.113.42","userAgent":"Mozilla/5.0...","detection":{"isBot":true,"confidence":0.87,"botType":"Scraper","botName":"Unknown","category":"bot","isSearchEngine":false,"isMalicious":true,"isSocialBot":false,"reasons":["High request rate","Missing browser headers"],"policy":"yarp-learning","action":"allow"},"detectorOutputs":{"UserAgent":{"detector":"UserAgent","confidence":0.75,"weight":1.0,"contribution":0.75,"reason":"Bot pattern in UA","suggestedBotType":"Scraper","executionTimeMs":0.5,"wave":0},"Behavioral":{"detector":"Behavioral","confidence":0.95,"weight":1.5,"contribution":1.425,"reason":"Rapid requests","suggestedBotType":"Scraper","executionTimeMs":2.3,"wave":0},"Onnx":{"detector":"Onnx","confidence":0.82,"weight":2.0,"contribution":1.64,"reason":"ML classification","suggestedBotType":"Scraper","executionTimeMs":45.2,"wave":2}},"signals":{"ua:bot_pattern":true,"behavior:rapid_requests":true,"ip:datacenter":true,"onnx:bot_probability":0.82},"responseTimeMs":150,"statusCode":200,"cluster":"backend-api","destination":"https://backend.example.com"}
```

### Console Output

When `LogToConsole: true`, you'll see real-time detection details:

```
[YARP Learning] sig_abc123 | /api/data | Bot: YES (0.87) | Scraper
  [UserAgent] 0.75 * 1.0 = 0.75 | Bot pattern in UA
  [Behavioral] 0.95 * 1.5 = 1.43 | Rapid requests
  [Onnx] 0.82 * 2.0 = 1.64 | ML classification
  Signals: ua:bot_pattern, behavior:rapid_requests, ip:datacenter
  Total: 0.87 | Action: allow (learning mode)
```

## Integration with YARP

### Option 1: Global Policy (Simplest)

```json
{
  "BotDetection": {
    "DefaultPolicyName": "yarp-learning"  // All requests use learning mode
  }
}
```

### Option 2: Path-Based Policy

```json
{
  "BotDetection": {
    "PathPolicies": {
      "/api/**": "yarp-learning",  // Only API paths
      "/static/**": "static",  // Static assets use fast policy
      "/**": "default"  // Everything else uses default
    }
  }
}
```

### Option 3: Conditional via YARP Transform

```csharp
services.AddReverseProxy()
    .LoadFromConfig(configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        context.AddRequestTransform(async transformContext =>
        {
            // Add learning mode header conditionally
            if (IsLearningModeEnabled())
            {
                transformContext.HttpContext.Items["BotDetection.Policy"] = "yarp-learning";
            }

            // Add bot detection headers to backend
            transformContext.HttpContext.AddBotDetectionHeadersVerbose(
                (name, value) => transformContext.ProxyRequest.Headers
                    .TryAddWithoutValidation(name, value));
        });
    });
```

## Sampling Strategy

For high-traffic scenarios, use sampling to reduce impact:

### Fixed Sampling Rate

```json
{
  "YarpLearningMode": {
    "SamplingRate": 0.1  // Process 10% of requests
  }
}
```

### Conditional Sampling (Code)

```csharp
// Only sample suspicious traffic
var confidence = httpContext.GetBotConfidence();
var shouldSample = confidence > 0.5 || Random.Shared.NextDouble() < 0.01;
if (shouldSample)
{
    httpContext.Items["BotDetection.Policy"] = "yarp-learning";
}
```

## Performance Impact

**Latency per request** (approximate):
- Fast-path detectors: ~5-10ms
- Slow-path detectors: ~10-20ms
- ONNX detector: ~30-50ms
- **Total: ~50-80ms** additional latency

**Mitigation strategies**:
1. Use `SamplingRate: 0.1` (10% of traffic)
2. Set `IncludeOnnxDetector: false` if ONNX not needed
3. Apply only to specific paths via `PathPolicies`
4. Exclude health/metrics via `ExcludePaths`

## Data Analysis

### Load Signatures into Python/Pandas

```python
import json
import pandas as pd

signatures = []
with open('yarp-learning-data/signatures_2024-01-15.jsonl', 'r') as f:
    for line in f:
        signatures.append(json.loads(line))

df = pd.DataFrame(signatures)
print(df['detection.isBot'].value_counts())
print(df.groupby('detection.botType').size())
```

### Analyze Detector Contributions

```python
# Flatten detector outputs
detectors = []
for sig in signatures:
    for name, output in sig['detectorOutputs'].items():
        detectors.append({
            'signatureId': sig['signatureId'],
            'detector': name,
            'confidence': output['confidence'],
            'weight': output['weight'],
            'contribution': output['contribution']
        })

det_df = pd.DataFrame(detectors)
print(det_df.groupby('detector')['contribution'].mean().sort_values(ascending=False))
```

## Security Considerations

⚠️ **WARNING: Learning mode collects sensitive data!**

### PII Protection

By default, the following are **EXCLUDED** to prevent PII collection:
- Request headers (may contain auth tokens)
- Cookies (may contain session IDs)
- Request body (may contain user data)

To include (only in secure environments):
```json
{
  "YarpLearningMode": {
    "IncludeFullHttpContext": true,  // ⚠️ May contain PII
    "IncludeRequestBody": true  // ⚠️ May contain PII
  }
}
```

### Access Control

Learning mode files may contain:
- IP addresses (can identify users)
- User-Agent strings (can fingerprint devices)
- Request paths (may reveal user activity)

**Recommendations**:
1. Store learning data in secure location with restricted access
2. Implement file encryption for sensitive environments
3. Set `MaxFiles` to limit data retention
4. Regularly purge old signature files
5. Never commit signature files to version control

## Troubleshooting

### No Signatures Being Written

**Check:**
1. `YarpLearningMode.Enabled: true`
2. Output directory exists and is writable
3. Requests aren't excluded by `ExcludePaths`
4. `MinConfidenceToLog` isn't too high
5. `SamplingRate` isn't too low

**Debug:**
```json
{
  "Logging": {
    "LogLevel": {
      "Mostlylucid.BotDetection": "Debug"
    }
  }
}
```

### High Memory Usage

**Cause:** Large buffer size or slow file I/O

**Solutions:**
- Reduce `BufferSize` (default: 100)
- Decrease `SamplingRate`
- Increase file rotation frequency
- Disable `IncludeFullHttpContext` and `IncludeRequestBody`

### Performance Degradation

**Cause:** Full pipeline running on too many requests

**Solutions:**
```json
{
  "YarpLearningMode": {
    "SamplingRate": 0.05,  // 5% sampling
    "IncludeOnnxDetector": false,  // Skip expensive ONNX
    "ExcludePaths": ["/static/**", "/images/**"]  // Skip static assets
  }
}
```

## Differences from Standard Learning Mode

| Feature | Standard Learning | YARP Learning |
|---------|------------------|---------------|
| **LLM Detector** | Included | **Excluded** (performance) |
| **ONNX Detector** | Included | Included (configurable) |
| **Timeout** | 10 seconds | **5 seconds** (faster) |
| **Output Format** | Internal queue | **JSONL files** |
| **Console Logging** | Minimal | **Comprehensive** |
| **Sampling** | N/A | **Built-in** sampling |
| **Use Case** | Async background | **Real-time gateway** |

## Example: Complete YARP Setup

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);

// Add bot detection with YARP learning
builder.Services.AddBotDetection(builder.Configuration);

// Add YARP with bot-aware routing
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        context.AddRequestTransform(async transformContext =>
        {
            var httpContext = transformContext.HttpContext;

            // Add comprehensive bot detection headers
            httpContext.AddBotDetectionHeadersVerbose(
                (name, value) => transformContext.ProxyRequest.Headers
                    .TryAddWithoutValidation(name, value));

            // Route based on bot detection
            var cluster = httpContext.GetBotAwareCluster(
                defaultCluster: "backend",
                crawlerCluster: "crawler-backend",
                blockCluster: "block");

            // Override cluster if needed
            if (cluster != context.RouteConfig.ClusterId)
            {
                transformContext.ProxyRequest.RequestUri =
                    new Uri($"https://{cluster}.example.com{httpContext.Request.Path}");
            }
        });
    });

var app = builder.Build();

// Enable bot detection middleware
app.UseBotDetection();

// Enable YARP
app.MapReverseProxy();

app.Run();
```

```json
// appsettings.json
{
  "BotDetection": {
    "Enabled": true,
    "DefaultPolicyName": "yarp-learning"
  },
  "YarpLearningMode": {
    "Enabled": true,
    "OutputPath": "./learning-data",
    "LogToConsole": true,
    "SamplingRate": 0.2,  // 20% sampling
    "IncludeLlmDetector": false,
    "IncludeOnnxDetector": true
  },
  "ReverseProxy": {
    "Routes": {
      "api-route": {
        "ClusterId": "backend",
        "Match": {
          "Path": "/api/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "backend": {
        "Destinations": {
          "destination1": {
            "Address": "https://backend.example.com"
          }
        }
      }
    }
  }
}
```

## Next Steps

1. **Enable YARP Learning Mode** with low sampling rate (5-10%)
2. **Monitor performance** impact and adjust sampling
3. **Collect data** for 24-48 hours minimum
4. **Analyze signatures** to understand bot patterns
5. **Label data** (bot/human) for training
6. **Train ML models** using collected signatures
7. **Tune thresholds** based on analysis
8. **Disable learning mode** and deploy optimized policies

## Related Documentation

- [Bot Detection Policies](docs/policies.md)
- [YARP Integration](docs/yarp-integration.md)
- [Training System Design](BOTDETECTION_TRAINING_SYSTEM.md)
- [Detection Strategies](docs/detection-strategies.md)
