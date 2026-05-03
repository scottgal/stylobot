# Stylobot.Gateway

**Not just another reverse proxy.** This is a **behavioral router** with production-ready bot detection built-in.

## What Makes This Different

| Traditional Proxy | Behavioral Router |
|-------------------|-------------------|
| Routes by URL path | Routes by **who's asking** |
| Same backend for everyone | Different backends for bots vs humans |
| Block or allow | Block, throttle, honeypot, challenge, redirect |
| Static rules | **Learns and adapts** in real-time |

```
Scraper → Honeypot (fake data)
Suspicious → Throttled backend
Malicious → Blocked
Googlebot → Real backend (verified)
Human → Real backend
```

## Core Feature: Bot Detection

This isn't a wrapper around YARP with bot detection bolted on. **Bot detection IS the core feature.** YARP provides the routing infrastructure.

Integrates [Mostlylucid.BotDetection](https://www.nuget.org/packages/Mostlylucid.BotDetection) v1.2.0:

### Detection Pipeline

| Method | Description | Latency |
|--------|-------------|---------|
| **User-Agent** | Pattern matching against 1000+ known bots | <1ms |
| **Headers** | Suspicious/missing header detection | <1ms |
| **IP Reputation** | Datacenter IP ranges (AWS, GCP, Azure, Cloudflare) | <1ms |
| **Security Tools** | Nikto, sqlmap, Burp Suite, Nmap, Acunetix detection | <1ms |
| **Version Age** | Browser/OS version staleness detection | <1ms |
| **Project Honeypot** | HTTP:BL IP reputation via DNS lookup | ~100ms |
| **Behavioral** | Rate limiting + anomaly detection | 1-5ms |
| **Inconsistency** | Cross-signal mismatch detection | 1-5ms |
| **Client-Side** | JavaScript fingerprinting, headless browser detection | <1ms |
| **Heuristic AI** | Feature-weighted classification with continuous learning | <1ms |
| **LLM Escalation** | Full reasoning for uncertain cases (Ollama) | 50-500ms |

### Key Capabilities

- **Sub-Millisecond Fast Path** - Most requests classified in <1ms
- **AI Escalation** - Uncertain cases escalate to heuristic model or LLM
- **Continuous Learning** - Weights adapt from detection feedback
- **Pattern Reputation** - Learned signatures feed back to fast detectors
- **Drift Detection** - Monitors when fast-path diverges from full analysis
- **Stealth Responses** - Bots don't know they're detected
- **Full Observability** - OpenTelemetry metrics and traces

### Detection → Action → Route

```
Request
   ↓
┌─────────────────────────────────────┐
│  Fast Path (<1ms)                   │
│  UA + Headers + IP + SecurityTools  │
│  + Fingerprint + Heuristic AI       │
└─────────────────────────────────────┘
   ↓ (if uncertain)
┌─────────────────────────────────────┐
│  Slow Path                          │
│  Behavioral + Inconsistency + LLM   │
└─────────────────────────────────────┘
   ↓
   Risk Score (0.0 - 1.0)
   ↓
┌─────────────────────────────────────┐
│  Action Policy                      │
│  → Allow (real backend)             │
│  → Redirect (honeypot/cache)        │
│  → Throttle (slow response)         │
│  → Challenge (CAPTCHA)              │
│  → Block (403/custom)               │
└─────────────────────────────────────┘
   ↓
┌─────────────────────────────────────┐
│  Learning Bus                       │
│  → Pattern reputation updates       │
│  → Weight adjustments               │
│  → Drift monitoring                 │
└─────────────────────────────────────┘
```

---

## Quick Start

### Zero-Config Mode

```bash
docker run -d -p 8080:8080 \
  -e DEFAULT_UPSTREAM=http://your-backend:3000 \
  scottgal/stylobot-gateway
```

### With Configuration

```bash
docker run -d -p 8080:8080 \
  -v ./config:/app/config:ro \
  scottgal/stylobot-gateway
```

### Configuration Precedence

1. `DEFAULT_UPSTREAM` env var (highest - zero-config mode)
2. YARP config file (`/app/config/yarp.json`)
3. Empty config (no routes - shows warning)

## Bot Detection Configuration

### Recommended Production Setup

**config/appsettings.json**:

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
      "Enabled": true
    },

    "PathPolicies": {
      "/api/auth/*": "strict",
      "/api/checkout/*": "strict",
      "/robots.txt": "allowAll",
      "/sitemap.xml": "allowVerifiedBots"
    }
  }
}
```

### Detection Policies

Configure which detectors run and how they're weighted:

```json
{
  "BotDetection": {
    "Policies": {
      "default": {
        "Description": "All detectors + Heuristic, LLM for uncertain",
        "FastPath": ["UserAgent", "Header", "Ip", "SecurityTool", "ProjectHoneypot",
                     "Behavioral", "ClientSide", "Inconsistency", "VersionAge", "Heuristic"],
        "AiPath": ["Llm", "HeuristicLate"],
        "EscalateToAi": true,
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95
      },
      "strict": {
        "Description": "Lower thresholds for sensitive endpoints",
        "FastPath": ["UserAgent", "Header", "Ip", "SecurityTool", "Heuristic"],
        "EarlyExitThreshold": 0.70,
        "ImmediateBlockThreshold": 0.80
      },
      "fastpath": {
        "Description": "No LLM, heuristic only",
        "FastPath": ["UserAgent", "Header", "Ip", "SecurityTool", "Heuristic"],
        "EscalateToAi": false
      }
    },
    "PathPolicies": {
      "/api/checkout/*": "strict",
      "/api/public/*": "fastpath"
    }
  }
}
```

### Action Policies

Control HOW to respond to detected bots:

```json
{
  "BotDetection": {
    "ActionPolicies": {
      "block": { "Type": "Block", "StatusCode": 403 },
      "throttle-stealth": {
        "Type": "Throttle",
        "DelayMs": 3000,
        "JitterMs": 1000
      },
      "honeypot": {
        "Type": "Redirect",
        "RedirectCluster": "honeypot"
      },
      "challenge": { "Type": "Challenge" },
      "logonly": { "Type": "Allow" }
    }
  }
}
```

## Bot-Aware Routing Example

**config/yarp.json** - Define your clusters:

```json
{
  "ReverseProxy": {
    "Routes": {
      "catch-all": {
        "ClusterId": "backend",
        "Match": { "Path": "/{**catch-all}" }
      }
    },
    "Clusters": {
      "backend": {
        "Destinations": { "main": { "Address": "http://api:3000" } }
      },
      "honeypot": {
        "Destinations": { "trap": { "Address": "http://honeypot:8080" } }
      },
      "cached": {
        "Destinations": { "static": { "Address": "http://cache:80" } }
      }
    }
  }
}
```

**config/appsettings.json** - Route by behavior:

```json
{
  "BotDetection": {
    "Enabled": true,
    "DefaultActionPolicyName": "allow",

    "ActionPolicies": {
      "allow": { "Type": "Allow" },
      "block": { "Type": "Block", "StatusCode": 403 },
      "honeypot": { "Type": "Redirect", "RedirectCluster": "honeypot" },
      "throttle": { "Type": "Throttle", "BaseDelayMs": 3000 }
    },

    "DetectionPolicies": {
      "default": {
        "ActionPolicyName": "allow",
        "Transitions": [
          { "WhenRiskExceeds": 0.9, "ActionPolicyName": "block" },
          { "WhenRiskExceeds": 0.7, "ActionPolicyName": "honeypot" },
          { "WhenRiskExceeds": 0.5, "ActionPolicyName": "throttle" },
          { "WhenSignal": "VerifiedGoodBot", "ActionPolicyName": "allow" }
        ]
      }
    }
  }
}
```

### Result

| Who | Risk | Action |
|-----|------|--------|
| Human | < 0.3 | Real backend |
| Googlebot (verified) | Any | Real backend |
| Suspicious | 0.5-0.7 | Throttled (3s delay) |
| Scraper | 0.7-0.9 | Honeypot (fake data) |
| Malicious | > 0.9 | Blocked |

## LLM Integration (Ollama)

For complex cases, escalate to an LLM for full reasoning:

```json
{
  "BotDetection": {
    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "HeuristicWithEscalation",
      "LlmEscalation": {
        "OllamaUrl": "http://ollama:11434",
        "Model": "gemma3:4b",
        "EscalationThreshold": 0.4
      }
    }
  }
}
```

```yaml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    environment:
      - BOTDETECTION__AIDETECTION__LLMESCALATION__OLLAMAURL=http://ollama:11434
    depends_on:
      - ollama

  ollama:
    image: ollama/ollama:latest
    volumes:
      - ollama_data:/root/.ollama
```

## Project Honeypot Integration

Add IP reputation checking (free API key from [projecthoneypot.org](https://www.projecthoneypot.org/)):

```json
{
  "BotDetection": {
    "ProjectHoneypot": {
      "Enabled": true,
      "AccessKey": "your-access-key",
      "HighThreatThreshold": 25,
      "TreatHarvestersAsMalicious": true,
      "TreatCommentSpammersAsMalicious": true
    }
  }
}
```

## Full Docker Compose

```yaml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    volumes:
      - ./config:/app/config:ro
      - ./data:/app/data
    environment:
      - ADMIN_SECRET=your-secret
    depends_on:
      - api
      - honeypot

  api:
    image: your-api:latest
    expose:
      - "3000"

  honeypot:
    image: nginx:alpine
    volumes:
      - ./fake-data:/usr/share/nginx/html:ro
    expose:
      - "80"
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DEFAULT_UPSTREAM` | - | Catch-all upstream (zero-config mode) |
| `ADMIN_SECRET` | - | Protect admin API with X-Admin-Secret header |
| `ADMIN_BASE_PATH` | `/admin` | Admin API base path |
| `GATEWAY_HTTP_PORT` | `8080` | HTTP port |
| `YARP_CONFIG_FILE` | `/app/config/yarp.json` | YARP config path |
| `LOG_LEVEL` | `Information` | Logging level |
| `DB_PROVIDER` | `none` | Optional: `postgres`, `sqlserver` |
| `DB_CONNECTION_STRING` | - | Database connection string |
| `DB_MIGRATE_ON_STARTUP` | `true` | Auto-run migrations |

### Bot Detection Environment Variables

| Variable | Description |
|----------|-------------|
| `BOTDETECTION__ENABLED` | Enable/disable bot detection |
| `BOTDETECTION__BOTTHRESHOLD` | Detection threshold (0.0-1.0) |
| `BOTDETECTION__BLOCKDETECTEDBOTS` | Block or just log detected bots |
| `BOTDETECTION__PROJECTHONEYPOT__ACCESSKEY` | Project Honeypot API key |
| `BOTDETECTION__AIDETECTION__LLMESCALATION__OLLAMAURL` | Ollama server URL |

## Directory Mounts

| Path | Purpose |
|------|---------|
| `/app/config` | Configuration (appsettings.json, yarp.json) |
| `/app/data` | Persistent data (learned patterns, weights, reputation cache) |
| `/app/logs` | Log files |
| `/app/plugins` | Plugin assemblies |

## Admin API

```bash
# Health check
curl http://localhost:8080/admin/health
# {"status":"ok","routesConfigured":2,"clustersConfigured":3,"mode":"configured"}

# Current routes and clusters
curl http://localhost:8080/admin/routes
curl http://localhost:8080/admin/clusters

# Gateway metrics
curl http://localhost:8080/admin/metrics

# Effective configuration
curl http://localhost:8080/admin/config/effective

# Active configuration sources
curl http://localhost:8080/admin/config/sources

# Browse mounted directories
curl http://localhost:8080/admin/fs
curl http://localhost:8080/admin/fs/config
```

If `ADMIN_SECRET` is set, include `X-Admin-Secret: your-secret` header.

## Raspberry Pi Deployment

Multi-arch images support ARM:

```bash
# Install Docker on Pi
curl -fsSL https://get.docker.com | sh

# Pull and run (auto-selects arm64 or armv7)
docker run -d --name gateway \
  -p 80:8080 \
  --restart unless-stopped \
  -e DEFAULT_UPSTREAM=http://192.168.1.100:3000 \
  scottgal/stylobot-gateway
```

### Pi-Optimized Settings

```yaml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      - DEFAULT_UPSTREAM=http://your-backend:3000
      - LOG_LEVEL=Warning
    deploy:
      resources:
        limits:
          memory: 256M
    restart: unless-stopped
```

## Architectures

- `linux/amd64` - x86-64 servers, cloud VMs
- `linux/arm64` - Raspberry Pi 4/5, Apple Silicon, AWS Graviton
- `linux/arm/v7` - Raspberry Pi 3/Zero 2 W

## Image Size

Optimized Alpine-based image:
- Base: ~80MB (ASP.NET 9.0 Alpine runtime)
- App: ~10MB
- **Total: ~90MB**

## Tags

- `latest` - Current stable
- `X.Y.Z[-previewN]` - Specific version (e.g., `1.2.0-preview1`)
- `YYYYMMDD` - Date builds

## Links

- **GitHub**: [github.com/scottgal/mostlylucid.stylobot](https://github.com/scottgal/mostlylucid.stylobot)
- **NuGet (BotDetection)**: [nuget.org/packages/Mostlylucid.BotDetection](https://www.nuget.org/packages/Mostlylucid.BotDetection)
- **Full Documentation**: [BotDetection README](https://github.com/scottgal/mostlylucid.stylobot/tree/main/Mostlylucid.BotDetection)

## License

[Unlicense](https://unlicense.org/) - Public Domain
