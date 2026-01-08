# StyloBot: Mostlylucid.BotDetection

**DESTROY ALL ROBOTS!** (politely, with HTTP 403s)

Built on **StyloFlow**, the ephemeral workflow engine.

Bot detection middleware for ASP.NET Core with multi-signal detection, **AI-powered classification with continuous
learning**, auto-updated blocklists, YARP integration, and full observability.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.botdetection.svg)](https://www.nuget.org/packages/mostlylucid.botdetection)

## Key Features

- **Multi-signal detection**: User-Agent + headers + IP ranges + behavioral analysis + client-side fingerprinting
- **AI-powered classification**: Heuristic model (<1ms) with optional LLM escalation for complex cases
- **Continuous learning**: Heuristic weights adapt over time based on detection feedback
- **Composable policies**: Separate detection (WHAT) from action (HOW) for maximum flexibility
- **Stealth responses**: Throttle, challenge, or honeypot bots without revealing detection
- **Auto-updated threat intel**: Pulls isbot patterns and cloud IP ranges automatically
- **First-class YARP support**: Bot-aware routing and header injection
- **Full observability**: OpenTelemetry traces and metrics baked in

## Why Use This?

**When commercial WAF isn't an option:**

- Self-hosted apps without Cloudflare/AWS/Azure
- Compliance requirements prohibiting third-party request inspection
- Cost-sensitive projects where $3K+/month WAF isn't justified

**When you need more than User-Agent matching:**

- Bots spoofing browser User-Agents
- Scripts missing Accept-Language, cookies, or timing signals
- API abuse from datacenter IPs

**When you want adaptive protection:**

- Detection that improves over time with learning
- Different policies per endpoint (strict for checkout, relaxed for static content)
- Stealth throttling that bots can't detect

> **Note**: For enterprise applications with stringent security requirements, consider commercial services
> like [Cloudflare Bot Management](https://www.cloudflare.com/products/bot-management/)
> or [AWS WAF Bot Control](https://aws.amazon.com/waf/features/bot-control/).

## Quick Start

### 1. Install

```bash
dotnet add package Mostlylucid.BotDetection
```

### 2. Configure Services

```csharp
using Mostlylucid.BotDetection.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBotDetection();

var app = builder.Build();

app.UseBotDetection();
app.Run();
```

### 3. Recommended Configuration (appsettings.json)

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth",

    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "Heuristic": {
        "Enabled": true,
        "EnableWeightLearning": true
      }
    },

    "Learning": {
      "Enabled": true,
      "EnableDriftDetection": true
    },

    "PathPolicies": {
      "/api/login": "strict",
      "/api/checkout/*": "strict",
      "/sitemap.xml": "allowVerifiedBots"
    }
  }
}
```

This enables:

- **AI detection** with Heuristic model (sub-millisecond, learns from feedback)
- **Learning system** that improves detection over time
- **Stealth throttling** (bots don't know they're being slowed)
- **Path-based policies** (strict for sensitive endpoints)

## Basic Usage

### HttpContext Extensions

```csharp
if (context.IsBot())
    return Results.StatusCode(403);

var confidence = context.GetBotConfidence();
var botType = context.GetBotType();
```

### Endpoint Filters

```csharp
app.MapGet("/api/data", () => "sensitive")
   .BlockBots();

app.MapPost("/api/submit", () => "ok")
   .RequireHuman();
```

### MVC Attributes

```csharp
[BlockBots(AllowVerifiedBots = true)]
public IActionResult Index() => View();
```

## Detection Methods

| Method               | Description                                              | Latency  |
|----------------------|----------------------------------------------------------|----------|
| **User-Agent**       | Pattern matching against known bots                      | <1ms     |
| **Headers**          | Suspicious/missing header detection                      | <1ms     |
| **IP**               | Datacenter IP range identification                       | <1ms     |
| **Version Age**      | Browser/OS version staleness detection                   | <1ms     |
| **Security Tools**   | Penetration testing tool detection (Nikto, sqlmap, etc.) | <1ms     |
| **Project Honeypot** | HTTP:BL IP reputation via DNS lookup                     | ~100ms   |
| **Behavioral**       | Rate limiting + anomaly detection                        | 1-5ms    |
| **Inconsistency**    | Cross-signal mismatch detection                          | 1-5ms    |
| **Heuristic AI**     | Feature-weighted classification with learning            | <1ms     |
| **LLM**              | Full reasoning (escalation only)                         | 50-500ms |
| **HeuristicLate**    | Post-AI refinement with all evidence                     | <1ms     |

## AI Detection & Learning (Key Differentiator)

The AI detection and learning system is what sets this library apart:

```
Request → Fast Detectors → Heuristic Model → Decision → Learning Bus
                ↓                                ↓            ↓
           Quick signals                   Risk score    Pattern Reputation
                                                ↓            ↓
                                         Action Policy   Weight Updates
```

### Enable with:

```json
{
  "BotDetection": {
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "Heuristic": { "Enabled": true, "EnableWeightLearning": true }
    },
    "Learning": { "Enabled": true }
  }
}
```

See [ai-detection.md](docs/ai-detection.md) and [learning-and-reputation.md](docs/learning-and-reputation.md) for
details.

## Action Policies

Control HOW to respond to detected bots:

| Policy              | Description                        |
|---------------------|------------------------------------|
| `block`             | Return 403 Forbidden               |
| `throttle-stealth`  | Delay response (bots don't notice) |
| `challenge`         | Present CAPTCHA or proof-of-work   |
| `redirect-honeypot` | Silent redirect to trap            |
| `logonly`           | Shadow mode (log but allow)        |

See [action-policies.md](docs/action-policies.md) for full details.

## Architecture: StyloFlow & Entity Types

BotDetection is built on **StyloFlow**, a YAML-driven orchestration framework. Each detector is configured via a manifest file that defines its inputs, outputs, and behavior.

### Entity Types

Entity types define the data contracts between detectors:

| Entity Type | Description | Persistence |
|-------------|-------------|-------------|
| `botdetection.request` | HTTP request with all detection signals | Ephemeral |
| `botdetection.signature` | Aggregated signals for classification | Ephemeral |
| `botdetection.contribution` | Single detector contribution | Ephemeral |
| `botdetection.ledger` | Accumulated detection evidence | Ephemeral |
| `botdetection.result` | Final classification result | JSON |
| `botdetection.learning_record` | Training data for learning system | Database |
| `botdetection.embedding` | Vector embedding for similarity search | Embedded |
| `botdetection.multivector_embedding` | Multi-vector ColBERT-style embedding | Embedded |

### Detector Manifests

Each detector has a YAML manifest defining its input/output contracts:

```yaml
# useragent.detector.yaml
name: UserAgentContributor
priority: 10
enabled: true

input:
  accepts:
    - type: botdetection.request
      required: true
      signal_pattern: request.headers.*
  required_signals:
    - request.headers.user-agent

output:
  produces:
    - type: botdetection.contribution
    - type: botdetection.ua_signal
  signals:
    - key: detection.useragent.confidence
      entity_type: number
      salience: 0.8

defaults:
  weights:
    bot_signal: 1.5
    verified: 2.0
  confidence:
    bot_detected: 0.3
    strong_signal: 0.85
  parameters:
    min_ua_length: 10
    verify_known_bots: true
```

### Overriding Configuration

Override detector defaults via `appsettings.json`:

```json
{
  "BotDetection": {
    "Detectors": {
      "UserAgentContributor": {
        "Weights": {
          "BotSignal": 2.0
        },
        "Parameters": {
          "min_ua_length": 20
        }
      }
    }
  }
}
```

### Multi-Vector Embeddings

For advanced similarity-based detection, embeddings support named vectors:

```yaml
# In botdetection.entity.yaml
- type: botdetection.multivector_embedding
  persistence: embedded
  schema:
    properties:
      vectors:
        items:
          properties:
            name:
              description: Vector identifier (e.g., "ua", "ip", "tls")
            vector:
              type: array
            weight:
              description: Relative importance for MaxSim scoring
      aggregation:
        enum: [maxsim, avgpool, concat]
```

## Documentation

| Feature                        | Description                               | Docs                                                                |
|--------------------------------|-------------------------------------------|---------------------------------------------------------------------|
| **Configuration**              | Full options reference                    | [configuration.md](docs/configuration.md)                           |
| **AI Detection**               | Heuristic model, LLM escalation, learning | [ai-detection.md](docs/ai-detection.md)                             |
| **Learning & Reputation**      | Pattern learning, drift detection         | [learning-and-reputation.md](docs/learning-and-reputation.md)       |
| **Action Policies**            | Block, throttle, challenge, redirect      | [action-policies.md](docs/action-policies.md)                       |
| **Detection Policies**         | Path-based detection configuration        | [policies.md](docs/policies.md)                                     |
| **Extensibility**              | Custom detectors and policies             | [extensibility.md](docs/extensibility.md)                           |
| **User-Agent Detection**       | Pattern matching with reputation          | [user-agent-detection.md](docs/user-agent-detection.md)             |
| **Header Detection**           | HTTP header anomaly analysis              | [header-detection.md](docs/header-detection.md)                     |
| **IP Detection**               | Datacenter and cloud IP identification    | [ip-detection.md](docs/ip-detection.md)                             |
| **Version Age Detection**      | Browser/OS version staleness detection    | [version-age-detection.md](docs/version-age-detection.md)           |
| **Security Tools Detection**   | Penetration testing tool detection        | [security-tools-detection.md](docs/security-tools-detection.md)     |
| **Project Honeypot**           | HTTP:BL IP reputation checking            | [project-honeypot.md](docs/project-honeypot.md)                     |
| **Behavioral Analysis**        | Rate limiting and anomaly detection       | [behavioral-analysis.md](docs/behavioral-analysis.md)               |
| **Client-Side Fingerprinting** | Headless browser detection                | [client-side-fingerprinting.md](docs/client-side-fingerprinting.md) |
| **YARP Integration**           | Bot-aware reverse proxy                   | [yarp-integration.md](docs/yarp-integration.md)                     |
| **Telemetry**                  | OpenTelemetry traces and metrics          | [telemetry-and-metrics.md](docs/telemetry-and-metrics.md)           |
| **YARP Gateway**               | Companion Docker gateway                  | [yarp-gateway.md](docs/yarp-gateway.md)                             |

## Companion Project: YARP Gateway

For edge deployments, use **[Mostlylucid.YarpGateway](../Mostlylucid.YarpGateway/)** - a lightweight Docker-first
reverse proxy:

[![Docker Hub](https://img.shields.io/docker/pulls/scottgal/mostlylucid.yarpgateway?label=Docker%20Hub)](https://hub.docker.com/r/scottgal/mostlylucid.yarpgateway)

```bash
# Zero-config reverse proxy in seconds
docker run -p 80:8080 -e DEFAULT_UPSTREAM=http://your-app:3000 scottgal/mostlylucid.yarpgateway
```

**Why use it with BotDetection?**

- Edge routing and load balancing
- Hot-reload YARP configuration
- Admin API for health/metrics
- Multi-arch: amd64, arm64, **Raspberry Pi** (arm/v7)
- ~90MB Alpine image

See [yarp-gateway.md](docs/yarp-gateway.md) for integration patterns.

## Diagnostic Endpoints

```csharp
app.MapBotDetectionEndpoints("/bot-detection");

// GET /bot-detection/check   - Current request analysis
// GET /bot-detection/stats   - Detection statistics
// GET /bot-detection/health  - Health check
```

## Service Registration Options

```csharp
// Default: all detectors + Heuristic AI with learning
builder.Services.AddBotDetection();

// User-agent only (fastest, minimal)
builder.Services.AddSimpleBotDetection();

// All detectors + LLM escalation (requires Ollama)
builder.Services.AddAdvancedBotDetection("http://localhost:11434", "gemma3:4b");
```

## Requirements

- .NET 8.0 or .NET 9.0
- Optional: [Ollama](https://ollama.ai/) for LLM-based detection escalation

## License

[The Unlicense](https://unlicense.org/) - Public Domain

## Links

- [GitHub](https://github.com/scottgal/mostlylucid.nugetpackages/tree/main/Mostlylucid.BotDetection)
- [NuGet](https://www.nuget.org/packages/mostlylucid.botdetection/)
- [Full Documentation](docs/)
