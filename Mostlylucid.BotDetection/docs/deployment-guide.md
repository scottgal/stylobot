# Deployment Guide

Three deployment tiers from zero-setup to production scale. Pick the one that matches your traffic.

| Tier | Storage | Vector Search | Requests/day | Setup |
|------|---------|---------------|--------------|-------|
| **Minimal** | In-memory | None | <10K | 3 lines of code |
| **Standard** | SQLite | None | 10K-100K | Default config |
| **Production** | PostgreSQL + TimescaleDB | pgvector | 100K-10M+ | Docker Compose |

---

## Tier 1: Minimal (Zero Setup)

Fast-path heuristic detection only. No LLM, no database, no external services. Detection runs in <1ms per request using pattern matching, header analysis, and behavioral scoring.

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

// One line: registers all 21 detectors with sensible defaults
builder.Services.AddBotDetection();

var app = builder.Build();

// Activate the middleware
app.UseBotDetection();

// Diagnostic endpoints: /bot-detection/check, /bot-detection/stats, /bot-detection/health
app.MapBotDetectionEndpoints();

// Your endpoints - use extension methods to check detection results
app.MapGet("/", (HttpContext ctx) => Results.Ok(new
{
    isBot = ctx.IsBot(),
    confidence = ctx.GetBotConfidence(),
    botType = ctx.GetBotType()?.ToString(),
    botName = ctx.GetBotName()
}));

// Block bots from sensitive endpoints
app.MapGet("/api/data", () => Results.Ok("sensitive data"))
    .BlockBots();

// Humans only (blocks ALL bots including Googlebot)
app.MapPost("/api/submit", () => Results.Ok("submitted"))
    .RequireHuman();

// Allow search engines, block scrapers
app.MapGet("/api/content", () => Results.Ok("public content"))
    .BlockBots(allowSearchEngines: true);

// High-confidence blocking only (>90%)
app.MapGet("/api/lenient", () => Results.Ok("lenient"))
    .BlockBots(minConfidence: 0.9);

app.Run();
```

### .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Mostlylucid.BotDetection" Version="*" />
  </ItemGroup>
</Project>
```

### What runs at this tier

All 21 contributing detectors execute in a wave-based pipeline:

**Pre-Wave 0** (instant abort):
- `FastPathReputationContributor` - Short-circuits known-bad patterns from reputation cache

**Wave 0** (parallel, no dependencies):
- `UserAgentContributor` - Pattern matching against 50K+ known bot signatures
- `HeaderContributor` - HTTP header consistency (Accept, Accept-Language, Connection)
- `IpContributor` - Datacenter IP range detection (AWS, GCP, Azure, Cloudflare)
- `BehavioralContributor` - Request rate and timing analysis
- `ClientSideContributor` - Browser fingerprint token validation
- `SecurityToolContributor` - Pen-testing tool signatures (sqlmap, Burp, Nikto)
- `CacheBehaviorContributor` - Cache header consistency
- `AdvancedBehavioralContributor` - Behavioral pattern analysis
- `TlsFingerprintContributor` - JA3/JA4 TLS fingerprinting
- `TcpIpFingerprintContributor` - p0f TCP/IP fingerprinting
- `Http2FingerprintContributor` - AKAMAI HTTP/2 fingerprinting
- `ResponseBehaviorContributor` - Historical response pattern feedback

**Wave 1+** (triggered by Wave 0 signals):
- `VersionAgeContributor` - Browser/OS version staleness
- `InconsistencyContributor` - Cross-signal inconsistency (e.g., UA says Chrome but TLS says curl)
- `ProjectHoneypotContributor` - DNS-based IP reputation lookup
- `ReputationBiasContributor` - Learned pattern reputation bias
- `HeuristicContributor` - Feature-based heuristic scoring (early)
- `MultiLayerCorrelationContributor` - Cross-layer fingerprint correlation
- `BehavioralWaveformContributor` - Multi-request behavioral waveform analysis

**Final**:
- `HeuristicLateContributor` - Final heuristic scoring consuming all evidence

### Zero configuration required

With no `appsettings.json` section, the defaults are:

| Setting | Default | Effect |
|---------|---------|--------|
| `BotThreshold` | 0.7 | Requests above 70% bot probability are classified as bots |
| `BlockDetectedBots` | false | Detection only (no blocking) - add `.BlockBots()` to block |
| `EnableUserAgentDetection` | true | UA pattern matching active |
| `EnableHeaderAnalysis` | true | Header consistency checks active |
| `EnableIpDetection` | true | Datacenter IP detection active |
| `EnableBehavioralAnalysis` | true | Rate/timing analysis active |
| `EnableLlmDetection` | false | No LLM required |
| `EnableTestMode` | true | `ml-bot-test-mode` header works for testing |
| Storage | SQLite (auto) | Patterns/weights stored in `botdetection.db` |

### Optional: appsettings.json

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth"
  }
}
```

### HttpContext extension methods

These are available on every request after `UseBotDetection()` runs:

```csharp
// Boolean checks
context.IsBot()                 // true if bot probability >= threshold
context.IsHuman()               // inverse of IsBot()
context.IsSearchEngineBot()     // true for Googlebot, Bingbot, etc.
context.IsVerifiedBot()         // true for bots that pass DNS verification

// Scores and details
context.GetBotConfidence()      // 0.0 - 1.0 probability
context.GetBotType()            // BotType enum (SearchEngine, Scraper, etc.)
context.GetBotName()            // "Googlebot", "Scrapy", etc.
context.GetRiskBand()           // VeryLow, Low, Elevated, Medium, High, VeryHigh
context.GetRecommendedAction()  // Allow, Challenge, Throttle, Block
context.ShouldChallengeRequest() // true for Medium risk band

// Full result object
var result = context.GetBotDetectionResult();
foreach (var reason in result.Reasons)
{
    Console.WriteLine($"{reason.Category}: {reason.Detail} ({reason.ConfidenceImpact:+0.00;-0.00})");
}
```

### Testing

```bash
# Human browser (should get low bot score)
curl -H "Accept: text/html" -H "Accept-Language: en-US" \
  -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0" \
  http://localhost:5080/bot-detection/check

# Known bot (should get high bot score)
curl -A "Googlebot/2.1" http://localhost:5080/bot-detection/check

# Scraper (should get blocked on .BlockBots() endpoints)
curl -A "Scrapy/2.5.0" http://localhost:5080/api/data

# Test mode simulation
curl -H "ml-bot-test-mode: malicious" http://localhost:5080/bot-detection/check

# Statistics
curl http://localhost:5080/bot-detection/stats
```

---

## Tier 2: Standard (SQLite + Learning)

Everything from Tier 1 plus persistent learning and heuristic AI. SQLite stores learned patterns and detector weights across restarts. No external services needed.

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBotDetection();
var app = builder.Build();
app.UseBotDetection();
app.MapBotDetectionEndpoints();
// ... your endpoints
app.Run();
```

### appsettings.json

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth",

    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "TimeoutMs": 1000,
      "Heuristic": {
        "Enabled": true,
        "EnableWeightLearning": true,
        "LoadLearnedWeights": true
      }
    },

    "Learning": {
      "Enabled": true,
      "LearningRate": 0.1,
      "EnableDriftDetection": true
    },

    "PathPolicies": {
      "/api/login": "strict",
      "/api/checkout/*": "strict",
      "/api/admin/**": "strict",
      "/sitemap.xml": "allowVerifiedBots",
      "/robots.txt": "allowVerifiedBots"
    },

    "ActionPolicies": {
      "api-throttle": {
        "Type": "Throttle",
        "BaseDelayMs": 500,
        "MaxDelayMs": 10000,
        "ScaleByRisk": true,
        "JitterPercent": 0.5,
        "IncludeHeaders": false
      }
    },

    "SignatureHashKey": "YOUR-BASE64-KEY-HERE"
  }
}
```

### What the learning system does

The heuristic AI and learning system form a closed loop:

1. **Heuristic scoring** (`HeuristicContributor` / `HeuristicLateContributor`): Extracts ~50 features from each request (UA length, header count, timing variance, etc.) and runs a lightweight scoring model. No LLM needed - pure math.

2. **Weight learning** (`SignatureFeedbackHandler`): When a request is classified, the system adjusts per-detector weights using exponential moving average (EMA). Detectors that contribute accurate signals get higher weights.

3. **Pattern reputation** (`PatternReputationUpdater`): Bot patterns accumulate reputation over time: Neutral -> Suspect -> ConfirmedBad. Known-bad patterns get fast-pathed on subsequent requests.

4. **Drift detection** (`DriftDetectionHandler`): Monitors for distribution shifts in detection patterns. If bots change behavior en masse, the system detects the drift and adjusts.

All of this persists to SQLite by default:

| Store | File | Contents |
|-------|------|----------|
| `SqliteLearnedPatternStore` | `botdetection.db` | Learned bot patterns with confidence |
| `SqliteWeightStore` | `botdetection.db` | Per-detector weight adjustments |

### Docker deployment

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY publish/ .

# Persist the learning database across restarts
VOLUME /app/data
ENV BotDetection__DatabasePath=/app/data/botdetection.db

ENTRYPOINT ["dotnet", "YourApp.dll"]
```

---

## Tier 3: Production (PostgreSQL + TimescaleDB + pgvector)

For high-traffic production deployments. Replaces SQLite with PostgreSQL for concurrent multi-server access, adds TimescaleDB for time-series analytics, and optionally pgvector for ML-based signature similarity search.

### Program.cs

```csharp
var builder = WebApplication.CreateBuilder(args);

// Core detection
builder.Services.AddBotDetection();

// Dashboard with real-time SignalR
builder.Services.AddStyloBotDashboard();

// PostgreSQL storage (replaces SQLite for all stores)
builder.Services.AddStyloBotPostgreSQL(
    builder.Configuration.GetConnectionString("BotDetection")!,
    options =>
    {
        options.RetentionDays = 90;
        options.EnableAutomaticCleanup = true;
        options.CleanupIntervalHours = 24;
        options.UseGinIndexOptimizations = true;

        // Enable TimescaleDB for high-volume analytics
        options.EnableTimescaleDB = true;
        options.TimescaleChunkInterval = TimeSpan.FromDays(1);
        options.CompressionAfter = TimeSpan.FromDays(7);
        options.AggregateRefreshInterval = TimeSpan.FromSeconds(30);

        // Enable pgvector for ML similarity search (optional)
        options.EnablePgVector = true;
        options.VectorDimension = 384;           // all-MiniLM-L6-v2
        options.VectorMinSimilarity = 0.8;
    });

var app = builder.Build();

app.UseBotDetection();
app.UseStyloBotDashboard();
app.MapBotDetectionEndpoints();
// ... your endpoints
app.Run();
```

### Required NuGet packages

```xml
<ItemGroup>
  <PackageReference Include="Mostlylucid.BotDetection" Version="*" />
  <PackageReference Include="Mostlylucid.BotDetection.UI" Version="*" />
  <PackageReference Include="Mostlylucid.BotDetection.UI.PostgreSQL" Version="*" />
</ItemGroup>
```

### appsettings.json

```json
{
  "ConnectionStrings": {
    "BotDetection": "Host=localhost;Port=5432;Database=stylobot;Username=stylobot;Password=YOUR-PASSWORD"
  },
  "BotDetection": {
    "BotThreshold": 0.7,
    "BlockDetectedBots": true,
    "DefaultActionPolicyName": "throttle-stealth",
    "SignatureHashKey": "YOUR-BASE64-KEY-FROM-KEY-VAULT",

    "EnableAiDetection": true,
    "AiDetection": {
      "Provider": "Heuristic",
      "TimeoutMs": 1000,
      "Heuristic": {
        "Enabled": true,
        "EnableWeightLearning": true,
        "LoadLearnedWeights": true
      }
    },

    "Learning": {
      "Enabled": true,
      "LearningRate": 0.1,
      "EnableDriftDetection": true
    },

    "ResponseHeaders": {
      "Enabled": true,
      "IncludeConfidence": true,
      "IncludeDetectors": true,
      "IncludeProcessingTime": true
    },

    "PathPolicies": {
      "/api/login": "strict",
      "/api/checkout/*": "strict",
      "/api/admin/**": "strict",
      "/api/public/**": "relaxed",
      "/sitemap.xml": "allowVerifiedBots",
      "/robots.txt": "allowVerifiedBots"
    }
  }
}
```

### docker-compose.yml

```yaml
services:
  timescaledb:
    image: timescale/timescaledb:latest-pg16
    environment:
      POSTGRES_USER: stylobot
      POSTGRES_PASSWORD: ${DB_PASSWORD}
      POSTGRES_DB: stylobot
    ports:
      - "5432:5432"
    volumes:
      - timescale-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U stylobot"]
      interval: 10s
      timeout: 5s
      retries: 5

  app:
    build: .
    environment:
      ConnectionStrings__BotDetection: "Host=timescaledb;Port=5432;Database=stylobot;Username=stylobot;Password=${DB_PASSWORD}"
      BotDetection__SignatureHashKey: ${SIGNATURE_HASH_KEY}
    ports:
      - "5080:5080"
    depends_on:
      timescaledb:
        condition: service_healthy

volumes:
  timescale-data:
```

### PostgreSQL schema (auto-created)

The `AddStyloBotPostgreSQL` registration auto-initializes the schema on startup when `AutoInitializeSchema = true` (default). The schema includes:

| Table | Purpose | Key indexes |
|-------|---------|-------------|
| `dashboard_detections` | Real-time detection events | GIN on reasons (JSONB), B-tree on timestamp, risk_band |
| `dashboard_signatures` | Unique bot signatures with hit counts | GIN on primary, IP, UA signatures (trigram) |
| `learned_patterns` | ML-learned bot patterns | B-tree on signature_type, pattern |
| `learned_weights` | Per-detector weight adjustments | B-tree on signature_type, signature |
| `bot_patterns` | Known bot pattern catalog | GIN on pattern value |
| `bot_signatures` | Multifactor signatures with reputation | GIN on primary, IP, UA, client-side |
| `signature_audit_log` | Audit trail for signature changes | B-tree on timestamp |

**Required PostgreSQL extensions** (auto-created if not present):
- `pg_trgm` - Trigram-based fuzzy text matching for GIN indexes
- `btree_gin` - B-tree support within GIN indexes
- `uuid-ossp` - UUID generation

### TimescaleDB features

When `EnableTimescaleDB = true`, the schema adds:

- **Hypertables**: `dashboard_detections` and `signature_audit_log` become time-partitioned
- **Compression**: Data older than `CompressionAfter` (default 7 days) is automatically compressed (90-95% storage reduction)
- **Continuous aggregates**: Pre-computed summaries at 1-minute, 1-hour, and 1-day intervals for dashboard queries
- **Retention policies**: Auto-delete data older than `RetentionDays`
- **Helper functions**: `get_dashboard_summary_fast()` and `get_time_series_fast()` for <1ms dashboard queries

**Performance impact:**

| Query | Without TimescaleDB | With TimescaleDB |
|-------|--------------------|-----------------:|
| Dashboard summary | ~50ms | <1ms |
| Time series (1h window) | ~200ms | <5ms |
| Storage per 100M events | ~50GB | ~5GB |

### pgvector features

When `EnablePgVector = true`, the schema adds vector columns and HNSW indexes to `bot_signatures`:

- `signature_embedding vector(384)` - Signature feature vector
- `behavior_embedding vector(384)` - Behavioral pattern vector
- HNSW indexes for <1ms cosine similarity search

This enables ML-based detection:
- Find signatures similar to a known bot (even with rotated UAs)
- Cluster bot networks by behavioral similarity
- Anomaly detection for requests far from known patterns

**Embedding model**: The system uses ONNX-based `all-MiniLM-L6-v2` (~22M params, ~80MB) for local embedding generation. No external API calls needed.

**Required PostgreSQL extension:**
- `vector` - pgvector extension (included in TimescaleDB Docker image)

### Configuration reference: PostgreSQLStorageOptions

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `ConnectionString` | string | (required) | PostgreSQL connection string |
| `AutoInitializeSchema` | bool | true | Auto-create tables on startup |
| `MaxDetectionsPerQuery` | int | 10,000 | Query result limit |
| `RetentionDays` | int | 30 | Auto-purge age (0 = disabled) |
| `EnableAutomaticCleanup` | bool | true | Background cleanup service |
| `CleanupIntervalHours` | int | 24 | Cleanup frequency |
| `CommandTimeoutSeconds` | int | 30 | DB operation timeout |
| `UseGinIndexOptimizations` | bool | true | Trigram search (requires pg_trgm) |
| `EnableTimescaleDB` | bool | false | Hypertables + compression + aggregates |
| `TimescaleChunkInterval` | TimeSpan | 1 day | Hypertable partition size |
| `CompressionAfter` | TimeSpan | 7 days | Compress data older than this |
| `AggregateRefreshInterval` | TimeSpan | 30 sec | Continuous aggregate refresh rate |
| `EnablePgVector` | bool | false | Vector similarity search |
| `VectorDimension` | int | 384 | Embedding dimensions (match your model) |
| `VectorIndexM` | int | 16 | HNSW links (higher = more accurate, more RAM) |
| `VectorIndexEfConstruction` | int | 64 | HNSW build quality (higher = better, slower build) |
| `VectorMinSimilarity` | double | 0.8 | Minimum cosine similarity threshold |

---

## Choosing a Tier

```
Do you need persistent learning across restarts?
├── No  → Tier 1 (Minimal)
└── Yes
    ├── Single server, <100K req/day? → Tier 2 (Standard/SQLite)
    └── Multi-server or >100K req/day? → Tier 3 (Production/PostgreSQL)
        ├── Need dashboard analytics? → Enable TimescaleDB
        └── Need ML similarity search? → Enable pgvector
```

### Migrating between tiers

Storage layers implement the same interfaces (`IDashboardEventStore`, `ILearnedPatternStore`, `IWeightStore`), so migrating is a DI registration change:

```csharp
// Tier 1 → Tier 2: Already happens automatically (SQLite is default)

// Tier 2 → Tier 3: Add PostgreSQL package and registration
builder.Services.AddStyloBotPostgreSQL(connectionString);
// PostgreSQL replaces SQLite stores via RemoveAll<T> + re-register
```

Learned patterns and weights start fresh when switching storage backends. To preserve them, export from SQLite and import to PostgreSQL using the `ILearnedPatternStore` and `IWeightStore` interfaces.

---

## Stylobot Gateway: Smart Router & Endpoint Protection

The [Stylobot Gateway](https://hub.docker.com/r/scottgal/stylobot-gateway) (`scottgal/stylobot-gateway`) is a standalone Docker container that adds bot detection intelligence to any web stack. Deploy it in front of your existing infrastructure -- Caddy, Nginx, Traefik, or directly to your backends -- as a smart routing layer that enriches every request with bot detection headers.

```
Internet → Stylobot Gateway (bot detection) → Your reverse proxy / backend
                 │
                 ├── X-Bot-Detected: true/false
                 ├── X-Bot-Risk-Score: 0.82
                 ├── X-Bot-Risk-Band: High
                 ├── X-Bot-Action: Block
                 └── X-Bot-Confidence: 0.91
```

Your backend or downstream proxy reads these headers and decides what to do. The gateway does detection; you decide the response.

### In Front of Caddy

Caddy handles TLS termination and static files. The Stylobot Gateway sits between the internet and Caddy, enriching requests with bot intelligence. Caddy can then use header matchers to route or block.

```yaml
# docker-compose.yml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      DEFAULT_UPSTREAM: "http://caddy:443"
      ADMIN_SECRET: "${ADMIN_SECRET}"
      GATEWAY_DEMO_MODE: "false"
    volumes:
      - ./config:/app/config:ro
    depends_on:
      - caddy

  caddy:
    image: caddy:2-alpine
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy_data:/data
    expose:
      - "443"
      - "80"

  app:
    build: .
    expose:
      - "8080"

volumes:
  caddy_data:
```

**Caddyfile** -- use the bot detection headers for routing decisions:

```
your-domain.com {
    # Block high-risk bots based on gateway headers
    @blocked {
        header X-Bot-Action Block
    }
    respond @blocked "Access Denied" 403

    # Challenge medium-risk requests
    @challenge {
        header X-Bot-Risk-Band High
    }
    redir @challenge /captcha

    # Everything else goes to your app
    reverse_proxy app:8080
}
```

**`config/yarp.json`** -- route to Caddy:

```json
{
  "ReverseProxy": {
    "Routes": {
      "all": {
        "ClusterId": "caddy",
        "Match": { "Path": "/{**catch-all}" }
      }
    },
    "Clusters": {
      "caddy": {
        "Destinations": {
          "caddy1": { "Address": "http://caddy:80" }
        }
      }
    }
  }
}
```

### In Front of Nginx

Same pattern, with Nginx reading the `X-Bot-*` headers:

```yaml
# docker-compose.yml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      DEFAULT_UPSTREAM: "http://nginx:80"
      ADMIN_SECRET: "${ADMIN_SECRET}"

  nginx:
    image: nginx:alpine
    volumes:
      - ./nginx.conf:/etc/nginx/conf.d/default.conf:ro
    expose:
      - "80"

  app:
    build: .
    expose:
      - "8080"
```

**nginx.conf** -- block or throttle based on bot headers:

```nginx
map $http_x_bot_action $bot_blocked {
    "Block"     1;
    default     0;
}

map $http_x_bot_risk_band $bot_throttle {
    "High"      1;
    "VeryHigh"  1;
    default     0;
}

server {
    listen 80;

    # Block bots the gateway flagged
    if ($bot_blocked) {
        return 403;
    }

    # Rate-limit high-risk requests
    location / {
        if ($bot_throttle) {
            # send to a rate-limited upstream or return challenge
            return 429;
        }
        proxy_pass http://app:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;

        # Forward all bot headers to your app
        proxy_set_header X-Bot-Detected $http_x_bot_detected;
        proxy_set_header X-Bot-Risk-Score $http_x_bot_risk_score;
        proxy_set_header X-Bot-Risk-Band $http_x_bot_risk_band;
        proxy_set_header X-Bot-Action $http_x_bot_action;
        proxy_set_header X-Bot-Confidence $http_x_bot_confidence;
    }
}
```

### In Front of Traefik

Traefik is common in Docker Swarm and K8s. Use the gateway as an external middleware layer:

```yaml
# docker-compose.yml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "8080:8080"
    environment:
      DEFAULT_UPSTREAM: "http://traefik:80"
      ADMIN_SECRET: "${ADMIN_SECRET}"

  traefik:
    image: traefik:v3.0
    command:
      - "--providers.docker=true"
      - "--entrypoints.web.address=:80"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    expose:
      - "80"

  app:
    build: .
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.app.rule=PathPrefix(`/`)"
    expose:
      - "8080"
```

### Kubernetes Deployment

Deploy the Stylobot Gateway as a Deployment + Service in Kubernetes. It sits between your Ingress controller and your backend services, acting as a bot-detection sidecar or gateway.

#### Architecture

```
                                ┌──────────────────────────┐
Internet → Ingress Controller → │  Stylobot Gateway Service    │ → Backend Services
           (nginx/traefik/      │  (bot detection + headers)│   (your pods)
            cloud LB)           └──────────────────────────┘
```

#### Gateway Deployment

```yaml
# k8s/gateway-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: stylobot-gateway
  labels:
    app: stylobot-gateway
spec:
  replicas: 2  # Scale horizontally - detection state is per-request
  selector:
    matchLabels:
      app: stylobot-gateway
  template:
    metadata:
      labels:
        app: stylobot-gateway
    spec:
      containers:
        - name: gateway
          image: scottgal/stylobot-gateway:latest
          ports:
            - containerPort: 8080
          env:
            - name: ADMIN_SECRET
              valueFrom:
                secretKeyRef:
                  name: stylobot-secrets
                  key: admin-secret
          volumeMounts:
            - name: yarp-config
              mountPath: /app/config
              readOnly: true
          resources:
            requests:
              cpu: 100m
              memory: 128Mi
            limits:
              cpu: 500m
              memory: 256Mi
          livenessProbe:
            httpGet:
              path: /admin/health
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /admin/health
              port: 8080
            initialDelaySeconds: 3
            periodSeconds: 5
      volumes:
        - name: yarp-config
          configMap:
            name: stylobot-yarp-config
---
apiVersion: v1
kind: Service
metadata:
  name: stylobot-gateway
spec:
  selector:
    app: stylobot-gateway
  ports:
    - port: 80
      targetPort: 8080
  type: ClusterIP
```

#### YARP Config (ConfigMap)

Route to your backend services by Kubernetes service name:

```yaml
# k8s/yarp-configmap.yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: stylobot-yarp-config
data:
  yarp.json: |
    {
      "ReverseProxy": {
        "Routes": {
          "api": {
            "ClusterId": "api-backend",
            "Match": { "Path": "/api/{**catch-all}" }
          },
          "web": {
            "ClusterId": "web-backend",
            "Match": { "Path": "/{**catch-all}" }
          }
        },
        "Clusters": {
          "api-backend": {
            "LoadBalancingPolicy": "RoundRobin",
            "Destinations": {
              "api-svc": { "Address": "http://api-service.default.svc.cluster.local:80" }
            },
            "HealthCheck": {
              "Passive": { "Enabled": true }
            }
          },
          "web-backend": {
            "Destinations": {
              "web-svc": { "Address": "http://web-service.default.svc.cluster.local:80" }
            }
          }
        }
      }
    }
```

#### Secrets

```yaml
# k8s/secrets.yaml
apiVersion: v1
kind: Secret
metadata:
  name: stylobot-secrets
type: Opaque
stringData:
  admin-secret: "your-admin-secret-here"
```

#### Ingress (route to gateway instead of backends)

Point your Ingress at the gateway service instead of directly at your app:

```yaml
# k8s/ingress.yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: app-ingress
  annotations:
    # Works with any ingress controller
    nginx.ingress.kubernetes.io/proxy-read-timeout: "30"
spec:
  rules:
    - host: your-domain.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: stylobot-gateway  # Gateway, not your app directly
                port:
                  number: 80
  tls:
    - hosts:
        - your-domain.com
      secretName: tls-secret
```

#### With PostgreSQL for Shared Learning

For multi-replica deployments where you want shared learning state across gateway pods:

```yaml
# Add to gateway deployment env:
env:
  - name: DB_PROVIDER
    value: "postgres"
  - name: DB_CONNECTION_STRING
    valueFrom:
      secretKeyRef:
        name: stylobot-secrets
        key: db-connection-string
```

All gateway replicas share the same PostgreSQL for learned patterns, weights, and detection history. Each pod is stateless -- scaling is horizontal.

#### Horizontal Pod Autoscaler

```yaml
# k8s/hpa.yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: stylobot-gateway-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: stylobot-gateway
  minReplicas: 2
  maxReplicas: 10
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 70
```

### Why Use the Gateway Instead of Middleware?

| Scenario | Use Middleware | Use Stylobot Gateway |
|----------|:---:|:---:|
| Single ASP.NET app | Yes | -- |
| Non-.NET backend (Node, Python, Go) | -- | Yes |
| Multiple backends / microservices | -- | Yes |
| Already using Caddy/Nginx/Traefik | -- | Yes |
| Kubernetes with multiple services | -- | Yes |
| Need bot detection without code changes | -- | Yes |
| Want detection as HTTP headers | -- | Yes |

The gateway adds bot intelligence as HTTP headers to every proxied request. Your backend reads the headers and decides what to do -- no SDK, no code changes, any language.

### Environment Variables Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `DEFAULT_UPSTREAM` | -- | Catch-all upstream URL (simplest config) |
| `GATEWAY_HTTP_PORT` | `8080` | Listen port |
| `ADMIN_SECRET` | -- | Protects `/admin` endpoints |
| `ADMIN_BASE_PATH` | `/admin` | Admin API path prefix |
| `GATEWAY_DEMO_MODE` | `false` | Enable all 21 detectors |
| `DB_PROVIDER` | `none` | `none`, `postgres`, `sqlserver` |
| `DB_CONNECTION_STRING` | -- | Database connection string |
| `YARP_CONFIG_FILE` | `/app/config/yarp.json` | YARP routing config path |
| `LOG_LEVEL` | `Information` | Serilog log level |

---

## Security Checklist

| Item | Tier 1 | Tier 2 | Tier 3 |
|------|--------|--------|--------|
| Set `SignatureHashKey` from Key Vault | Recommended | Required | Required |
| Disable `EnableTestMode` in production | Yes | Yes | Yes |
| Set `ExcludedPaths` for health checks | Yes | Yes | Yes |
| Use HTTPS | Yes | Yes | Yes |
| Set `RetentionDays` for GDPR | N/A | N/A | Required |
| Restrict dashboard access | N/A | N/A | Required |

Generate a `SignatureHashKey`:

```csharp
var key = Mostlylucid.BotDetection.PiiHasher.GenerateKey();
Console.WriteLine(Convert.ToBase64String(key));
// Store this in Azure Key Vault / AWS Secrets Manager / env var
```

