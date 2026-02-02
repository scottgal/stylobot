# StyloBot.Gateway
by ***mostly*lucid**

A lightweight, Docker-first YARP reverse proxy gateway with built-in bot detection.

[![Docker Hub](https://img.shields.io/docker/pulls/scottgal/mostlylucid.yarpgateway?label=Docker%20Hub)](https://hub.docker.com/r/scottgal/mostlylucid.yarpgateway)

## Quick Start (30 Seconds)

```bash
docker run -p 8080:8080 -e DEFAULT_UPSTREAM=http://your-backend:3000 scottgal/mostlylucid.yarpgateway
```

That's it. Every request is now analyzed for bots. Check the logs:

```
[INF] Bot detection: GET / -> risk=0.74 (High), bot=unknown, reasons=[UserAgent:+0.90, Behavioral:+0.25], time=1.0ms
```

---

## Deployment Tiers

Choose the level that fits your needs. Each tier builds on the previous one.

### Tier 1: Minimal (No Database, No Config Files)

**Best for:** Quick testing, simple single-backend setups, trying it out.

One environment variable, zero config files. Bot detection runs in-memory with no persistence.

```yaml
# docker-compose.yml
services:
  gateway:
    image: scottgal/mostlylucid.yarpgateway:latest
    ports:
      - "80:8080"
    environment:
      - DEFAULT_UPSTREAM=http://backend:3000

  backend:
    image: your-app:latest
```

```bash
docker compose up -d
```

**What you get:**
- Bot detection on every request (10 fast-path detectors, <1ms)
- Heuristic AI classification with weight learning (in-memory)
- `X-Bot-*` response headers
- Structured logging with risk scores

**What you don't get:**
- No persistence (learned weights lost on restart)
- No custom routing rules
- No admin API protection

---

### Tier 2: Standard (File Config, No Database)

**Best for:** Production deployments that don't need persistence, multi-backend routing.

Add config files for custom routing, action policies, and admin protection. Learned weights are stored in a local SQLite file (auto-created).

```yaml
# docker-compose.yml
services:
  gateway:
    image: scottgal/mostlylucid.yarpgateway:latest
    ports:
      - "80:8080"
    environment:
      - ADMIN_SECRET=your-secret-here
    volumes:
      - ./config:/app/config:ro
      - ./data:/app/data
      - ./logs:/app/logs
    restart: unless-stopped

  api:
    image: your-api:latest
    # Not exposed directly - only via gateway

  web:
    image: your-web:latest
    # Not exposed directly - only via gateway
```

Create `config/yarp.json`:

```json
{
  "ReverseProxy": {
    "Routes": {
      "api": {
        "ClusterId": "api",
        "Match": { "Path": "/api/{**catch-all}" }
      },
      "web": {
        "ClusterId": "web",
        "Match": { "Path": "/{**catch-all}" }
      }
    },
    "Clusters": {
      "api": {
        "Destinations": { "d1": { "Address": "http://api:3000" } },
        "HealthCheck": { "Passive": { "Enabled": true } }
      },
      "web": {
        "Destinations": { "d1": { "Address": "http://web:8080" } }
      }
    }
  }
}
```

Optionally override bot detection settings with `config/appsettings.json`:

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth"
  }
}
```

```bash
docker compose up -d
```

**What you get (in addition to Tier 1):**
- Multi-backend routing with load balancing and health checks
- Admin API with secret protection
- Persistent logs
- SQLite-backed weight learning (survives restarts in `/app/data`)
- Custom action policies

---

### Tier 3: Full (PostgreSQL, All Detectors, Learning)

**Best for:** Production deployments that need full persistence, multi-instance scaling, advanced detection.

PostgreSQL stores learned weights, patterns, and reputation data. Enables all 21 detectors including advanced fingerprinting.

```yaml
# docker-compose.yml
services:
  gateway:
    image: scottgal/mostlylucid.yarpgateway:latest
    ports:
      - "80:8080"
    environment:
      - ADMIN_SECRET=your-secret-here
      - DB_PROVIDER=postgres
      - DB_CONNECTION_STRING=Host=postgres;Database=stylobot;Username=stylobot;Password=stylobot
      - DB_MIGRATE_ON_STARTUP=true
    volumes:
      - ./config:/app/config:ro
      - ./logs:/app/logs
    depends_on:
      postgres:
        condition: service_healthy
    restart: unless-stopped

  postgres:
    image: postgres:16-alpine
    environment:
      - POSTGRES_DB=stylobot
      - POSTGRES_USER=stylobot
      - POSTGRES_PASSWORD=stylobot
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U stylobot"]
      interval: 5s
      timeout: 5s
      retries: 5

  api:
    image: your-api:latest

  web:
    image: your-web:latest

volumes:
  pgdata:
```

Create `config/appsettings.json` with all detectors enabled:

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
      "LearningRate": 0.1,
      "EnableDriftDetection": true
    },

    "Policies": {
      "default": {
        "FastPath": [
          "FastPathReputation", "UserAgent", "Header", "Ip",
          "SecurityTool", "CacheBehavior", "Behavioral",
          "Inconsistency", "VersionAge", "ReputationBias",
          "Heuristic", "TlsFingerprint", "TcpIpFingerprint",
          "Http2Fingerprint", "MultiLayerCorrelation",
          "BehavioralWaveform"
        ],
        "EarlyExitThreshold": 0.85,
        "ImmediateBlockThreshold": 0.95
      }
    },

    "PathPolicies": {
      "/api/auth/*": "strict",
      "/api/checkout/*": "strict",
      "/robots.txt": "allowAll",
      "/sitemap.xml": "allowVerifiedBots",
      "/*": "default"
    }
  }
}
```

```bash
docker compose up -d
```

**What you get (in addition to Tier 2):**
- PostgreSQL-backed persistence (weights, patterns, reputation, bot data)
- All 21 detectors including TLS/TCP/HTTP2 fingerprinting
- Adaptive learning with drift detection
- Multi-instance safe (shared database)
- Path-specific detection policies

---

### Tier Comparison

| Feature | Minimal | Standard | Full |
|---------|---------|----------|------|
| Bot detection | 10 fast detectors | 10 fast detectors | All 21 detectors |
| Latency | <1ms | <1ms | <2ms |
| Config files | None | `yarp.json` | `yarp.json` + `appsettings.json` |
| Database | None | SQLite (auto) | PostgreSQL |
| Weight learning | In-memory | SQLite file | PostgreSQL |
| Multi-instance | No | No | Yes |
| Persistence | None | Local file | Full DB |
| Admin API | Unprotected | Secret-protected | Secret-protected |
| Setup | 1 env var | 2 files | 2 files + Postgres |

---

## Reading Bot Detection Headers in Your Backend

The gateway adds `X-Bot-*` headers to every proxied request. Your backend can read these to make decisions.

<details>
<summary><strong>Headers Reference (Production Mode)</strong></summary>

| Header | Value | When Added |
|--------|-------|-----------|
| `X-Bot-Detected` | `true` / `false` | Always |
| `X-Bot-Confidence` | `0.00` - `1.00` | Always |
| `X-Bot-Type` | `SearchEngine`, `Scraper`, `SecurityTool`, etc. | When bot detected |
| `X-Bot-Name` | `Googlebot`, `Bingbot`, `Scrapy`, etc. | When identified |
| `X-Bot-Category` | Detection category | When bot detected |
| `X-Is-Search-Engine` | `true` / `false` | When bot detected |
| `X-Is-Malicious-Bot` | `true` / `false` | When bot detected |
| `X-Is-Social-Bot` | `true` / `false` | When bot detected |

</details>

<details>
<summary><strong>Headers Reference (Demo Mode - GATEWAY_DEMO_MODE=true)</strong></summary>

All production headers plus:

| Header | Value | Description |
|--------|-------|-------------|
| `X-Bot-Detection-Result` | `true` / `false` | Detection result |
| `X-Bot-Detection-Probability` | `0.0000` - `1.0000` | Raw probability |
| `X-Bot-Detection-Confidence` | `0.0000` - `1.0000` | Confidence score |
| `X-Bot-Detection-RiskBand` | `Low`/`Medium`/`High`/`VeryHigh`/`Verified` | Risk band |
| `X-Bot-Detection-BotType` | Bot type enum | Detected type |
| `X-Bot-Detection-BotName` | String | Identified bot name |
| `X-Bot-Detection-Policy` | String | Detection policy used |
| `X-Bot-Detection-Action` | String | Action taken |
| `X-Bot-Detection-ProcessingMs` | `0.00` - `999.99` | Processing time |
| `X-Bot-Detection-Reasons` | JSON array | Top 5 detection reasons |
| `X-Bot-Detection-Contributions` | JSON array | All detector contributions |
| `X-Bot-Detection-RequestId` | String | Request trace ID |
| `X-Signature-ID` | String | Request signature hash |

</details>

<details>
<summary><strong>Network Fingerprinting Headers (always added)</strong></summary>

| Header | Value | Description |
|--------|-------|-------------|
| `X-TLS-Protocol` | `TLS 1.3`, etc. | TLS version |
| `X-TLS-Cipher` | Cipher suite | Negotiated cipher |
| `X-HTTP-Protocol` | `HTTP/1.1`, `HTTP/2` | HTTP version |
| `X-Is-HTTP2` | `1` | Present if HTTP/2 |
| `X-Client-IP` | IP address | Client IP |
| `X-Client-Port` | Port number | Client port |
| `X-Connection-ID` | String | Connection identifier |
| `X-Request-ID` | String | Request trace ID |
| `X-Request-Timestamp` | Unix ms | Request timestamp |

</details>

### Node.js / Express Backend

```js
const express = require('express');
const app = express();

app.use((req, res, next) => {
  // Read bot detection headers from gateway
  const isBot = req.headers['x-bot-detected'] === 'true';
  const confidence = parseFloat(req.headers['x-bot-confidence'] || '0');
  const botName = req.headers['x-bot-name'] || 'unknown';
  const botType = req.headers['x-bot-type'];

  // Attach to request for downstream use
  req.bot = { isBot, confidence, botName, botType };

  if (isBot && confidence > 0.8) {
    console.log(`High-confidence bot: ${botName} (${confidence})`);
    // Serve simplified content, skip analytics, etc.
  }

  next();
});

// Example: serve different content to bots
app.get('/products', (req, res) => {
  if (req.bot.isBot) {
    // Return SEO-friendly HTML for search engines
    return res.send(renderStaticProductPage());
  }
  // Return SPA for real users
  res.sendFile('index.html');
});

// Example: protect API endpoints
app.post('/api/checkout', (req, res) => {
  if (req.bot.isBot) {
    return res.status(403).json({ error: 'Automated requests not allowed' });
  }
  // Process checkout...
});

app.listen(3000);
```

### PHP / Laravel Backend

```php
<?php
// Middleware: app/Http/Middleware/BotDetection.php
namespace App\Http\Middleware;

use Closure;
use Illuminate\Http\Request;

class BotDetection
{
    public function handle(Request $request, Closure $next)
    {
        // Read bot detection headers from gateway
        $isBot = $request->header('X-Bot-Detected') === 'true';
        $confidence = (float) $request->header('X-Bot-Confidence', '0');
        $botName = $request->header('X-Bot-Name', 'unknown');
        $botType = $request->header('X-Bot-Type');

        // Store on request for downstream use
        $request->merge([
            'bot_detected' => $isBot,
            'bot_confidence' => $confidence,
            'bot_name' => $botName,
            'bot_type' => $botType,
        ]);

        // Block high-confidence bots from sensitive endpoints
        if ($isBot && $confidence > 0.8) {
            logger()->warning("Bot detected: {$botName} ({$confidence})", [
                'ip' => $request->header('X-Client-IP'),
                'path' => $request->path(),
            ]);
        }

        return $next($request);
    }
}

// Register in app/Http/Kernel.php:
// protected $middleware = [
//     \App\Http\Middleware\BotDetection::class,
// ];
```

```php
<?php
// In a controller:
class ProductController extends Controller
{
    public function index(Request $request)
    {
        if ($request->input('bot_detected')) {
            // Return lightweight response for bots
            return view('products.seo', ['products' => Product::all()]);
        }
        return view('products.spa');
    }
}
```

### Python / Flask Backend

```python
from flask import Flask, request, g, jsonify

app = Flask(__name__)

@app.before_request
def read_bot_headers():
    """Read bot detection headers from gateway."""
    g.is_bot = request.headers.get('X-Bot-Detected', 'false') == 'true'
    g.bot_confidence = float(request.headers.get('X-Bot-Confidence', '0'))
    g.bot_name = request.headers.get('X-Bot-Name', 'unknown')
    g.bot_type = request.headers.get('X-Bot-Type')

@app.route('/api/data')
def get_data():
    if g.is_bot and g.bot_confidence > 0.8:
        return jsonify({'error': 'Automated access not permitted'}), 403
    return jsonify({'data': 'sensitive stuff'})
```

---

## Action Policies Cookbook

Action policies control **how** the gateway responds when a bot is detected. Configure them in `appsettings.json` under `BotDetection.ActionPolicies`, or use the built-in presets.

### Hard Block (403 Forbidden)

Immediately reject with HTTP 403. Use for APIs and sensitive endpoints.

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "block"
  }
}
```

Or via environment variable:
```bash
docker run -e BOTDETECTION__BLOCKDETECTEDBOTS=true \
           -e BOTDETECTION__DEFAULTACTIONPOLICYNAME=block \
           ...
```

### Stealth Throttle (Tarpit)

Slow bots down without revealing detection. Delay scales with risk score. Bots see normal responses, just slower.

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth"
  }
}
```

The default `throttle-stealth` policy: 500ms-10s delay, no headers, high jitter. For custom delay ranges:

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "my-tarpit",
    "ActionPolicies": {
      "my-tarpit": {
        "Type": "Throttle",
        "BaseDelayMs": 2000,
        "MaxDelayMs": 30000,
        "JitterPercent": 0.5,
        "ScaleByRisk": true,
        "IncludeHeaders": false
      }
    }
  }
}
```

**How delay scales:**
- Risk 0.5 = ~2s delay
- Risk 0.7 = ~5s delay
- Risk 0.9 = ~15s delay
- Risk 1.0 = ~30s delay (max)

### Honeypot Redirect

Silently redirect bots to a trap URL for analysis. No error codes -- bots think they reached the real page.

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "redirect-honeypot"
  }
}
```

Custom honeypot with metadata:

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "my-honeypot",
    "ActionPolicies": {
      "my-honeypot": {
        "Type": "Redirect",
        "TargetUrl": "/honeypot?risk={risk}&path={originalPath}",
        "Permanent": false,
        "AddMetadata": false
      }
    }
  }
}
```

### Challenge (CAPTCHA / Proof-of-Work)

Present a challenge before allowing access. Proof-of-work scales difficulty with risk score.

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "challenge-pow",
    "ActionPolicies": {
      "challenge-pow": {
        "Type": "Challenge",
        "ChallengeType": "ProofOfWork",
        "TokenValidityMinutes": 30
      }
    }
  }
}
```

PoW difficulty by risk: 0.5 = ~10ms, 0.7 = ~100ms, 1.0 = ~1-2s of client CPU time.

### Shadow Mode (Log Only)

Monitor without blocking. Use this first to measure false positives before enabling enforcement.

```json
{
  "BotDetection": {
    "BotThreshold": 0.5,
    "BlockDetectedBots": false,
    "DefaultActionPolicyName": "shadow"
  }
}
```

### Layered Response (Risk-Based Escalation)

Different actions for different risk levels. Low risk = throttle, medium = challenge, high = block.

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "Policies": {
      "default": {
        "FastPath": ["FastPathReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "Inconsistency", "VersionAge", "ReputationBias", "Heuristic"],
        "Transitions": [
          { "WhenRiskExceeds": 0.95, "ActionPolicyName": "block" },
          { "WhenRiskExceeds": 0.8, "ActionPolicyName": "challenge-pow" },
          { "WhenRiskExceeds": 0.6, "ActionPolicyName": "throttle-stealth" }
        ]
      }
    }
  }
}
```

### Path-Specific Policies

Protect sensitive endpoints more aggressively:

```json
{
  "BotDetection": {
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth",
    "PathPolicies": {
      "/api/auth/*": "strict",
      "/api/checkout/*": "strict",
      "/api/*": "default",
      "/robots.txt": "allowAll",
      "/sitemap.xml": "allowVerifiedBots",
      "/*": "default"
    }
  }
}
```

<details>
<summary><strong>All Built-In Policy Presets</strong></summary>

**Block:**
| Name | Status Code | Description |
|------|-------------|-------------|
| `block` | 403 | Default block |
| `block-hard` | 403 | Minimal response |
| `block-soft` | 429 | Too Many Requests |
| `block-debug` | 403 | Includes risk score |

**Throttle:**
| Name | Delay Range | Description |
|------|-------------|-------------|
| `throttle` | 500ms-5s | Moderate |
| `throttle-gentle` | 200ms-1s | Light |
| `throttle-moderate` | 500ms-5s | Risk-scaled |
| `throttle-aggressive` | 1s-30s | Exponential backoff |
| `throttle-stealth` | 500ms-10s | No headers, high jitter |

**Challenge:**
| Name | Type | Description |
|------|------|-------------|
| `challenge` | Redirect | Redirect to challenge page |
| `challenge-captcha` | CAPTCHA | reCAPTCHA/hCaptcha |
| `challenge-js` | JavaScript | Browser verification |
| `challenge-pow` | Proof of Work | Computational puzzle |

**Redirect:**
| Name | Target | Description |
|------|--------|-------------|
| `redirect` | `/blocked` | Simple redirect |
| `redirect-honeypot` | `/honeypot` | Silent trap |
| `redirect-tarpit` | `/tarpit` | Slow response |
| `redirect-error` | `/error` | Error page with risk info |

**Log Only:**
| Name | Level | Description |
|------|-------|-------------|
| `logonly` | Info | Log and allow |
| `shadow` | Info | Shadow mode with headers |
| `debug` | Debug | Full evidence logging |
| `full-log` | Debug | Maximum visibility (demos) |

</details>

---

## YARP Routing

### Zero-Config (Single Backend)

Just set `DEFAULT_UPSTREAM`:

```bash
docker run -p 8080:8080 -e DEFAULT_UPSTREAM=http://backend:3000 scottgal/mostlylucid.yarpgateway
```

### File-Based (Multiple Backends)

Create `config/yarp.json`:

```json
{
  "ReverseProxy": {
    "Routes": {
      "api": {
        "ClusterId": "api",
        "Match": { "Path": "/api/{**catch-all}" }
      },
      "static": {
        "ClusterId": "cdn",
        "Match": { "Path": "/static/{**catch-all}" }
      },
      "default": {
        "ClusterId": "web",
        "Match": { "Path": "/{**catch-all}" }
      }
    },
    "Clusters": {
      "api": {
        "LoadBalancingPolicy": "RoundRobin",
        "HealthCheck": { "Passive": { "Enabled": true } },
        "Destinations": {
          "api1": { "Address": "http://api1:3000" },
          "api2": { "Address": "http://api2:3000" }
        }
      },
      "cdn": {
        "Destinations": {
          "d1": { "Address": "http://cdn:80" }
        }
      },
      "web": {
        "Destinations": {
          "d1": { "Address": "http://web:8080" }
        }
      }
    }
  }
}
```

### Host-Based Routing

```json
{
  "ReverseProxy": {
    "Routes": {
      "api": {
        "ClusterId": "api",
        "Match": { "Hosts": ["api.example.com"] }
      },
      "app": {
        "ClusterId": "app",
        "Match": { "Hosts": ["app.example.com"] }
      }
    },
    "Clusters": {
      "api": { "Destinations": { "d1": { "Address": "http://api:3000" } } },
      "app": { "Destinations": { "d1": { "Address": "http://app:8080" } } }
    }
  }
}
```

---

## Admin API

All endpoints under `/admin` (configurable via `ADMIN_BASE_PATH`). Protected by `ADMIN_SECRET` if set.

```bash
# Health check
curl http://localhost:8080/admin/health

# With secret
curl -H "X-Admin-Secret: your-secret" http://localhost:8080/admin/health
```

| Endpoint | Description |
|----------|-------------|
| `GET /admin/health` | Health, uptime, route/cluster counts, DB status |
| `GET /admin/config/effective` | Current merged configuration |
| `GET /admin/config/sources` | Configuration source precedence |
| `GET /admin/routes` | Current YARP routes |
| `GET /admin/clusters` | Current YARP clusters |
| `GET /admin/metrics` | Requests/sec, errors, connections, bytes |
| `GET /admin/fs` | List logical directories |
| `GET /admin/fs/{name}` | Browse directory contents |

---

## Configuration Reference

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DEFAULT_UPSTREAM` | - | Catch-all upstream URL (zero-config mode) |
| `GATEWAY_HTTP_PORT` | `8080` | HTTP port |
| `ADMIN_BASE_PATH` | `/admin` | Admin API path prefix |
| `ADMIN_SECRET` | - | Required `X-Admin-Secret` header value |
| `DB_PROVIDER` | `none` | `none`, `postgres`, `sqlserver` |
| `DB_CONNECTION_STRING` | - | Database connection string |
| `DB_MIGRATE_ON_STARTUP` | `true` | Auto-run migrations |
| `GATEWAY_DEMO_MODE` | `false` | Pass all detection headers downstream |
| `LOG_LEVEL` | `Information` | Serilog minimum level |

### Bot Detection via Environment

All `BotDetection` settings can be overridden via environment variables using `__` as separator:

```bash
BOTDETECTION__BOTTHRESHOLD=0.6
BOTDETECTION__BLOCKDETECTEDBOTS=true
BOTDETECTION__DEFAULTACTIONPOLICYNAME=throttle-stealth
BOTDETECTION__LOGALLREQUESTS=false
BOTDETECTION__PROJECTHONEYPOT__ACCESSKEY=your-key
BOTDETECTION__AIDETECTION__LLMESCALATION__OLLAMAURL=http://ollama:11434
```

### Volume Mounts

| Path | Purpose |
|------|---------|
| `/app/config` | Configuration files (`appsettings.json`, `yarp.json`) |
| `/app/data` | Persistent data (SQLite weight store) |
| `/app/logs` | Log files |
| `/app/plugins` | Plugin assemblies |

---

## Detection Methods

| Detector | Latency | Description |
|----------|---------|-------------|
| User-Agent | <1ms | Pattern matching against 1000+ known bots |
| Headers | <1ms | Missing/suspicious header detection |
| IP Ranges | <1ms | Datacenter IP identification (AWS, GCP, Azure) |
| Security Tools | <1ms | Detects Nikto, sqlmap, Burp Suite, etc. |
| Behavioral | <1ms | Request pattern anomaly detection |
| Inconsistency | <1ms | Header/behavior mismatch detection |
| Version Age | <1ms | Outdated browser/OS detection |
| Reputation Bias | <1ms | Learned reputation patterns |
| Cache Behavior | <1ms | Cache interaction analysis |
| Heuristic AI | <1ms | Weighted feature classification with learning |
| TLS Fingerprint | <1ms | JA3/JA4 TLS client hello analysis |
| TCP/IP Fingerprint | <1ms | p0f network stack fingerprinting |
| HTTP/2 Fingerprint | <1ms | AKAMAI HTTP/2 settings frame analysis |
| Multi-Layer Correlation | <1ms | Cross-detector signal correlation |
| Behavioral Waveform | <1ms | Temporal request pattern analysis |
| Project Honeypot | ~100ms | DNS-based IP reputation lookup |
| LLM Escalation | 50-500ms | Full AI reasoning for edge cases |

### Risk Bands

| Score | Band | Interpretation |
|-------|------|----------------|
| 0.0 - 0.3 | Low | Likely human |
| 0.3 - 0.5 | Medium | Uncertain |
| 0.5 - 0.7 | High | Suspicious |
| 0.7 - 1.0 | VeryHigh | Almost certainly bot |
| 1.0 | Verified | Known bot (Googlebot, etc.) |

---

## Advanced Configuration

<details>
<summary><strong>Project Honeypot IP Reputation</strong></summary>

Add IP reputation checking (free API key from [projecthoneypot.org](https://www.projecthoneypot.org/)):

```bash
docker run -e DEFAULT_UPSTREAM=http://backend:3000 \
           -e BOTDETECTION__PROJECTHONEYPOT__ACCESSKEY=your-key \
           scottgal/mostlylucid.yarpgateway
```

</details>

<details>
<summary><strong>LLM-Powered Detection (Ollama)</strong></summary>

Escalate ambiguous cases to an LLM:

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

Docker compose with Ollama:

```yaml
services:
  gateway:
    image: scottgal/mostlylucid.yarpgateway:latest
    environment:
      - DEFAULT_UPSTREAM=http://backend:3000
      - BOTDETECTION__AIDETECTION__LLMESCALATION__OLLAMAURL=http://ollama:11434

  ollama:
    image: ollama/ollama:latest
    volumes:
      - ollama_data:/root/.ollama

volumes:
  ollama_data:
```

</details>

<details>
<summary><strong>Client-Side Detection</strong></summary>

Add JavaScript probes for enhanced detection. The gateway serves the script automatically.

Add to your HTML:
```html
<script src="/_botdetection/botdetection.js"></script>
<script>BotDetection.init({ endpoint: '/_botdetection/collect', autoSubmit: true });</script>
```

Enable in config:
```json
{
  "BotDetection": {
    "ClientSide": {
      "Enabled": true,
      "CollectBrowserFingerprint": true,
      "RequireJsProbe": false
    }
  }
}
```

Requests with valid client-side probes receive up to -0.3 risk reduction.

</details>

<details>
<summary><strong>Quiet Mode (Log Bots Only)</strong></summary>

```bash
docker run -e DEFAULT_UPSTREAM=http://backend:3000 \
           -e BOTDETECTION__LOGALLREQUESTS=false \
           scottgal/mostlylucid.yarpgateway
```

</details>

<details>
<summary><strong>Disable Response Headers</strong></summary>

```json
{
  "BotDetection": {
    "ResponseHeaders": { "Enabled": false }
  }
}
```

</details>

---

## Docker Image

### Supported Architectures

| Architecture | Platforms |
|--------------|-----------|
| `linux/amd64` | Standard servers, cloud VMs |
| `linux/arm64` | Raspberry Pi 4/5, Apple Silicon, AWS Graviton |
| `linux/arm/v7` | Raspberry Pi 3/Zero 2 W |

### Image Size

~90MB (Alpine-based, production-optimized, non-root user)

### Tags

- `scottgal/mostlylucid.yarpgateway:latest`
- `scottgal/mostlylucid.yarpgateway:X.Y.Z[-previewN]`
- `scottgal/mostlylucid.yarpgateway:YYYYMMDD`

---

## Raspberry Pi Deployment

```bash
# Install Docker
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER

# Run gateway
docker run -d --name gateway \
  -p 80:8080 \
  --restart unless-stopped \
  -e DEFAULT_UPSTREAM=http://192.168.1.100:3000 \
  -e LOG_LEVEL=Warning \
  scottgal/mostlylucid.yarpgateway
```

<details>
<summary><strong>Pi-optimized docker-compose.yml</strong></summary>

```yaml
services:
  gateway:
    image: scottgal/mostlylucid.yarpgateway:latest
    ports:
      - "80:8080"
    environment:
      - DEFAULT_UPSTREAM=http://your-backend:3000
      - ADMIN_SECRET=your-secret
      - LOG_LEVEL=Warning
    volumes:
      - ./config:/app/config:ro
      - ./logs:/app/logs
    deploy:
      resources:
        limits:
          memory: 256M
        reservations:
          memory: 128M
    restart: unless-stopped
```

Tips:
- Use file-based config, skip PostgreSQL (saves RAM)
- Set `LOG_LEVEL=Warning` to reduce disk I/O
- Use USB SSD instead of SD card for logs

</details>

<details>
<summary><strong>Home network gateway (multi-service routing)</strong></summary>

`config/yarp.json`:
```json
{
  "ReverseProxy": {
    "Routes": {
      "ha": { "ClusterId": "ha", "Match": { "Hosts": ["ha.local"] } },
      "plex": { "ClusterId": "plex", "Match": { "Hosts": ["plex.local"] } },
      "pihole": { "ClusterId": "pihole", "Match": { "Hosts": ["pihole.local"] } }
    },
    "Clusters": {
      "ha": { "Destinations": { "d1": { "Address": "http://192.168.1.50:8123" } } },
      "plex": { "Destinations": { "d1": { "Address": "http://192.168.1.51:32400" } } },
      "pihole": { "Destinations": { "d1": { "Address": "http://192.168.1.1:80" } } }
    }
  }
}
```

</details>

---

## Building from Source

```bash
# Docker image
docker build -t scottgal/mostlylucid.yarpgateway .

# .NET SDK
dotnet publish -c Release
```

## Publishing

```bash
# Tag and push
git tag yarpgateway-v1.0.0
git push origin yarpgateway-v1.0.0
# GitHub Actions builds multi-arch images and pushes to Docker Hub
```

Required GitHub secrets: `DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`

## License

[Unlicense](https://unlicense.org/) - Public Domain
