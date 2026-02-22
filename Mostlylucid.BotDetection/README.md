# StyloBot: Mostlylucid.BotDetection

**DESTROY ALL ROBOTS!** (politely, with HTTP 403s)

Built on **StyloFlow**, the ephemeral workflow engine.

Bot detection middleware for ASP.NET Core with multi-signal detection, **AI-powered classification with continuous
learning**, auto-updated blocklists, YARP integration, and full observability.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.botdetection.svg)](https://www.nuget.org/packages/mostlylucid.botdetection)

## Key Features

- **29 detectors in 4 waves**: User-Agent, headers, IP, behavioral, protocol fingerprinting, AI classification, cluster detection, and more
- **Protocol-deep fingerprinting**: JA3/JA4 TLS, p0f TCP/IP, AKAMAI HTTP/2, QUIC HTTP/3 — catch bots even when they spoof everything
- **AI-powered classification**: Heuristic model (<1ms, ~50 features) with optional LLM escalation for complex cases
- **Continuous learning**: Heuristic weights adapt over time based on detection feedback
- **Bot network discovery**: Leiden clustering finds coordinated campaigns across thousands of signatures
- **Geo intelligence**: Country reputation, geographic drift detection, VPN/proxy/Tor/datacenter identification
- **Composable policies**: Separate detection (WHAT) from action (HOW) for maximum flexibility
- **Stealth responses**: Throttle, challenge, or honeypot bots without revealing detection
- **Real-time dashboard**: World map, country stats, cluster visualization, user agent breakdown, live signature feed
- **Zero PII**: All persistence uses HMAC-SHA256 hashed signatures — no raw IPs or user agents stored
- **Auto-updated threat intel**: Pulls isbot patterns and cloud IP ranges automatically
- **First-class YARP support**: Bot-aware routing and header injection for any-language backends
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

## Detection Methods (29 Detectors)

All detectors execute in a wave-based pipeline. Fast-path detectors run in parallel in <1ms. Advanced detectors fire only when triggered by upstream signals.

### Wave 0 — Fast Path (<1ms)

| Detector | Description |
|----------|-------------|
| **UserAgent** | Pattern matching against 1000+ known bot signatures with category classification |
| **Header** | Suspicious/missing header detection (Accept-Language, encoding, connection patterns) |
| **IP** | Datacenter, cloud provider, and known botnet IP range identification |
| **SecurityTool** | Penetration testing tool detection (Nikto, sqlmap, Burp Suite, Metasploit) |
| **CacheBehavior** | HTTP cache header interaction analysis (ETag, If-Modified-Since, gzip) |
| **VersionAge** | Browser/OS version staleness detection (outdated clients = suspicious) |
| **AiScraper** | AI training bot detection (GPTBot, ClaudeBot, PerplexityBot, Google-Extended) |
| **FastPathReputation** | Ultra-fast cached reputation from previous detections (ConfirmedGood/Bad) |
| **ReputationBias** | Signature-based reputation tracking from historical patterns |
| **Haxxor** | SQL injection, XSS, path traversal, command injection detection |
| **TransportProtocol** | WebSocket, gRPC, GraphQL, SSE protocol violation detection |
| **Inconsistency** | UA/header mismatch and cross-signal inconsistency detection |
| **VerifiedBot** | DNS-verified identification of Googlebot, Bingbot, and 30+ legitimate crawlers |

### Wave 1 — Behavioral (1-5ms)

| Detector | Description |
|----------|-------------|
| **Behavioral** | Rate limiting, request pattern analysis, timing anomalies |
| **AdvancedBehavioral** | Deep statistical analysis — entropy, Markov chains, anomaly detection |
| **BehavioralWaveform** | FFT-based spectral fingerprinting of request timing patterns |
| **ClientSide** | Headless browser detection via JavaScript fingerprinting signals |
| **GeoChange** | Geographic drift detection, country reputation, origin verification |
| **AccountTakeover** | Credential stuffing, brute force, and account takeover detection |
| **ResponseBehavior** | Honeypot path detection, response-side behavioral patterns |

### Wave 2 — Protocol Fingerprinting (<1ms)

| Detector | Description |
|----------|-------------|
| **TLS Fingerprint** | JA3/JA4 TLS fingerprint analysis — identifies client libraries |
| **TCP/IP Fingerprint** | p0f-style passive OS fingerprinting via TCP stack behavior |
| **HTTP/2 Fingerprint** | AKAMAI-style HTTP/2 frame analysis (settings, priorities, pseudo-headers) |
| **HTTP/3 Fingerprint** | QUIC transport parameter fingerprinting and version negotiation analysis |
| **MultiLayerCorrelation** | Cross-layer consistency analysis (does TLS match TCP match HTTP match UA?) |

### Wave 3 — AI + Learning (1-500ms)

| Detector | Description |
|----------|-------------|
| **Heuristic** | Feature-weighted classification extracting ~50 features with online learning |
| **HeuristicLate** | Post-AI refinement with full evidence from all prior waves |
| **Similarity** | Fuzzy signature matching via HNSW/Qdrant vector search |
| **Cluster** | Bot network detection with Leiden community discovery |
| **TimescaleReputation** | Time-series IP/signature reputation aggregation |
| **LLM** | Background classification via LLM plugin (LlamaSharp CPU or Ollama HTTP) |

### Slow Path

| Detector | Description |
|----------|-------------|
| **ProjectHoneypot** | HTTP:BL IP reputation via DNS lookup (~100ms) |

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
| **Quick Start**                | Two-line setup, all 29 detectors          | [quickstart.md](docs/quickstart.md)                                 |
| **Configuration**              | Full options reference                    | [configuration.md](docs/configuration.md)                           |
| **AI Detection**               | Heuristic model, LLM escalation, learning | [ai-detection.md](docs/ai-detection.md)                             |
| **AI Scraper Detection**       | GPTBot, ClaudeBot, PerplexityBot          | [ai-scraper-detection.md](docs/ai-scraper-detection.md)             |
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
| **Advanced Behavioral**        | Entropy, Markov chains, anomalies         | [advanced-behavioral-detection.md](docs/advanced-behavioral-detection.md) |
| **Behavioral Waveform**        | FFT spectral request timing analysis      | [behavioral-waveform.md](docs/behavioral-waveform.md)               |
| **Client-Side Fingerprinting** | Headless browser detection                | [client-side-fingerprinting.md](docs/client-side-fingerprinting.md) |
| **Cache Behavior**             | HTTP cache header analysis                | [cache-behavior-detection.md](docs/cache-behavior-detection.md)     |
| **Response Behavior**          | Honeypot and response-side patterns       | [response-behavior.md](docs/response-behavior.md)                   |
| **TLS/TCP/HTTP2 Fingerprinting** | JA3/JA4, p0f, AKAMAI fingerprints      | [AdvancedFingerprintingDetectors.md](docs/AdvancedFingerprintingDetectors.md) |
| **HTTP/3 Fingerprinting**      | QUIC transport parameter analysis         | [http3-fingerprinting.md](docs/http3-fingerprinting.md)             |
| **Multi-Layer Correlation**    | Cross-layer consistency analysis          | [multi-layer-correlation.md](docs/multi-layer-correlation.md)       |
| **Cluster Detection**          | Leiden clustering for bot networks        | [cluster-detection.md](docs/cluster-detection.md)                   |
| **TCP/IP Fingerprint**         | Passive OS fingerprinting (p0f)           | [tcp-ip-fingerprint.md](docs/tcp-ip-fingerprint.md)                 |
| **Timescale Reputation**       | Time-series reputation aggregation        | [timescale-reputation.md](docs/timescale-reputation.md)             |
| **Haxxor Detection**           | SQL injection, XSS, attack payload detection | [haxxor-detection.md](docs/haxxor-detection.md)                  |
| **Account Takeover**           | Credential stuffing, brute force detection | [account-takeover-detection.md](docs/account-takeover-detection.md) |
| **Transport Protocol**         | WebSocket, gRPC, GraphQL violation detection | [transport-protocol-detection.md](docs/transport-protocol-detection.md) |
| **Geo Change Detection**       | Geographic drift, country reputation      | [geo-change-detection.md](docs/geo-change-detection.md)             |
| **Verified Bot Detection**     | DNS-verified crawler identification       | [verified-bot-detection.md](docs/verified-bot-detection.md)         |
| **Inconsistency Detection**    | UA/header mismatch detection              | [inconsistency-detection.md](docs/inconsistency-detection.md)       |
| **YARP Integration**           | Bot-aware reverse proxy                   | [yarp-integration.md](docs/yarp-integration.md)                     |
| **Telemetry**                  | OpenTelemetry traces and metrics          | [telemetry-and-metrics.md](docs/telemetry-and-metrics.md)           |
| **Stylobot Gateway**           | Companion Docker gateway                  | [yarp-gateway.md](docs/yarp-gateway.md)                             |

## Companion Project: Stylobot Gateway

For edge deployments, use **[Stylobot.Gateway](../Stylobot.Gateway/)** - a lightweight Docker-first
reverse proxy:

[![Docker Hub](https://img.shields.io/docker/pulls/scottgal/stylobot-gateway?label=Docker%20Hub)](https://hub.docker.com/r/scottgal/stylobot-gateway)

```bash
# Zero-config reverse proxy in seconds
docker run -p 80:8080 -e DEFAULT_UPSTREAM=http://your-app:3000 scottgal/stylobot-gateway
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
// Default: all detectors + Heuristic AI with learning (no LLM)
builder.Services.AddBotDetection();

// Add in-process CPU LLM provider (LlamaSharp, zero external deps)
builder.Services.AddStylobotLlamaSharp();

// OR: Add external Ollama HTTP provider (GPU capable)
builder.Services.AddStylobotOllama("http://localhost:11434", "qwen3:0.6b");

// User-agent only (fastest, minimal)
builder.Services.AddSimpleBotDetection();
```

## Requirements

- .NET 10.0
- Optional: LlamaSharp (in-process CPU) or Ollama (external HTTP) for LLM classification

## License

[The Unlicense](https://unlicense.org/) - Public Domain

## Links

- [GitHub](https://github.com/scottgal/mostlylucid.stylobot/tree/main/Mostlylucid.BotDetection)
- [NuGet](https://www.nuget.org/packages/mostlylucid.botdetection/)
- [Full Documentation](docs/)

