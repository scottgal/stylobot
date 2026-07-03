# StyloBot Public API & Node SDK Design

**Date:** 2026-04-21
**Status:** Approved
**Scope:** Canonical REST API for all SDK clients, Node.js as first SDK

---

## Problem

StyloBot is detection-in-the-proxy today. There is no transactional "send request metadata, get detection result" API. This means:

- Non-.NET apps (Node, Python, Go) cannot use StyloBot programmatically
- Apps not behind the Gateway have no detection path
- Dashboard data is only accessible via internal UI endpoints with no stable contract
- No foundation for a multi-language SDK ecosystem

## Solution

1. **Public REST API** (`Mostlylucid.BotDetection.Api`) - versioned, OpenAPI-documented, auth-gated endpoints
2. **Proxy header injection** - Gateway injects `X-StyloBot-*` headers for zero-latency detection
3. **Node SDK** - `@stylobot/core` (types + client) and `@stylobot/node` (Express/Fastify middleware)
4. **OpenAPI spec** - generated from .NET, source of truth for all future SDKs

---

## API Auth Tiers

### Tier 1 - Unauthenticated (proxy headers)

For apps behind StyloBot Gateway. SDK reads `X-StyloBot-*` headers from the proxied request or response. No API call, zero latency.

### Tier 2 - API Key (`X-SB-Api-Key` header)

For detection-as-a-service and dashboard read access. Uses the existing rich API key system (`ApiKeyConfig`). Rate limits, path restrictions, time windows, and detector policy overlays all enforced per key.

**Endpoints:** `POST /api/v1/detect`, all `GET /api/v1/*` read endpoints, `GET /api/v1/me`.

### Tier 3 - OIDC Bearer Token (commercial)

For management operations. Uses the customer's OIDC provider (self-hosted) or Keycloak (stylo.bot). Follows the two-domain auth model: vendor domain issues licenses, customer domain manages their own deployment.

**Endpoints:** `PUT /api/v1/config/policies/*`, `POST/DELETE /api/v1/keys`, `GET /api/v1/export`.

---

## API Contract

### Detection

#### `POST /api/v1/detect`

**Auth:** Tier 2 (API Key)

**Request:**

```json
{
  "method": "GET",
  "path": "/products/123",
  "headers": {
    "user-agent": "Mozilla/5.0 ...",
    "accept": "text/html",
    "accept-language": "en-US,en;q=0.9",
    "accept-encoding": "gzip, deflate, br",
    "referer": "https://example.com/",
    "cookie": "_ga=GA1.2.123"
  },
  "remoteIp": "203.0.113.42",
  "protocol": "https",
  "tls": {
    "version": "TLSv1.3",
    "cipher": "TLS_AES_256_GCM_SHA384",
    "ja3": "771,4865-4866-4867..."
  }
}
```

- `headers` - flat string dict. SDK extracts from framework request object.
- `tls` - optional. Node apps rarely terminate TLS; included for proxy/gateway callers.
- `remoteIp` - SDK handles `X-Forwarded-For` resolution before sending.

**Response:**

```json
{
  "verdict": {
    "isBot": true,
    "botProbability": 0.92,
    "confidence": 0.87,
    "botType": "Scraper",
    "botName": "GPTBot",
    "riskBand": "High",
    "recommendedAction": "Block",
    "threatScore": 0.15,
    "threatBand": "Low"
  },
  "reasons": [
    { "detector": "UserAgent", "detail": "Known AI scraper UA", "impact": 0.6 },
    { "detector": "Header", "detail": "Missing Accept-Language", "impact": 0.2 }
  ],
  "signals": {
    "ua.isBot": true,
    "ua.botName": "GPTBot",
    "ip.isDatacenter": true,
    "geo.countryCode": "US"
  },
  "meta": {
    "processingTimeMs": 4,
    "detectorsRun": 18,
    "policyName": "default",
    "aiRan": false,
    "requestId": "abc-123"
  }
}
```

#### `POST /api/v1/detect/batch`

Same request shape as array, returns array of results in same order. For log replay, offline analysis, high-throughput scenarios. Max batch size configurable (default 100).

### Read Endpoints (Tier 2)

All return paginated envelope:

```json
{
  "data": [ ... ],
  "pagination": { "offset": 0, "limit": 50, "total": 1234 },
  "meta": { "generatedAt": "2026-04-21T12:00:00Z" }
}
```

Single-resource endpoints return `{ "data": { ... }, "meta": { ... } }`.

| Endpoint | Purpose | Key params |
|----------|---------|------------|
| `GET /api/v1/detections` | Recent detection events | `?limit=50&offset=0&isBot=true&since=` |
| `GET /api/v1/sessions` | Behavioral sessions | `?signature={id}&since=&limit=` |
| `GET /api/v1/sessions/{id}` | Session detail (Markov chain, vector, paths) | |
| `GET /api/v1/signatures` | Bot signature reputation table | `?isBot=true&limit=50` |
| `GET /api/v1/summary` | Aggregate stats | `?period=1h\|24h\|7d` |
| `GET /api/v1/timeseries` | Time-bucketed counts | `?interval=1m\|5m\|1h&since=&until=` |
| `GET /api/v1/countries` | Country-level aggregations | `?limit=20` |
| `GET /api/v1/endpoints` | Per-path stats | `?limit=20` |
| `GET /api/v1/topbots` | Top detected bot signatures | `?limit=10` |
| `GET /api/v1/threats` | CVE probe / threat intel | `?severity=high` |
| `GET /api/v1/me` | Current API key info, permissions, rate limit status | |

### Management Endpoints (Tier 3)

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/config/policies` | List detection policies |
| `PUT /api/v1/config/policies/{name}` | Update policy (commercial) |
| `GET /api/v1/config/manifests` | Detector manifests |
| `POST /api/v1/keys` | Create API key |
| `DELETE /api/v1/keys/{id}` | Revoke API key |
| `GET /api/v1/keys` | List keys (names + metadata, never secrets) |
| `GET /api/v1/export` | Data export (CSV/JSON) |

### Error Responses

All errors use RFC 9457 Problem Details:

```json
{
  "type": "https://stylo.bot/errors/rate-limit-exceeded",
  "title": "Rate limit exceeded",
  "status": 429,
  "detail": "API key 'CI Pipeline' exceeded 200 req/min limit",
  "instance": "/api/v1/detect"
}
```

---

## Proxy Header Injection

### Response headers (default)

Injected by Gateway into responses. For when the Node app is a client calling through the Gateway.

### Forward request headers

Injected by Gateway into the proxied request to upstream. For when the Node app is the upstream server behind the Gateway.

| Header | Value | Example |
|--------|-------|---------|
| `X-StyloBot-IsBot` | `true\|false` | `true` |
| `X-StyloBot-Probability` | `0.00-1.00` | `0.92` |
| `X-StyloBot-Confidence` | `0.00-1.00` | `0.87` |
| `X-StyloBot-BotType` | enum string | `Scraper` |
| `X-StyloBot-BotName` | string or empty | `GPTBot` |
| `X-StyloBot-RiskBand` | enum string | `High` |
| `X-StyloBot-Action` | enum string | `Block` |
| `X-StyloBot-ThreatScore` | `0.00-1.00` | `0.15` |
| `X-StyloBot-ThreatBand` | enum string | `Low` |
| `X-StyloBot-Policy` | policy name | `default` |
| `X-StyloBot-RequestId` | correlation ID | `abc-123` |

Configuration:

```json
{
  "BotDetection": {
    "InjectResponseHeaders": true,
    "ForwardRequestHeaders": true
  }
}
```

---

## .NET Project: `Mostlylucid.BotDetection.Api`

### Structure

```
Mostlylucid.BotDetection.Api/
├── Endpoints/
│   ├── DetectEndpoints.cs
│   ├── DetectionReadEndpoints.cs
│   ├── SummaryEndpoints.cs
│   ├── ManagementEndpoints.cs
│   └── MeEndpoints.cs
├── Models/
│   ├── DetectRequest.cs
│   ├── DetectResponse.cs
│   ├── Verdict.cs
│   ├── DetectionReason.cs
│   ├── PaginatedResponse.cs
│   └── ErrorResponse.cs
├── Auth/
│   ├── ApiKeyAuthHandler.cs
│   └── OidcAuthHandler.cs
├── Bridge/
│   └── SyntheticHttpContext.cs
├── Middleware/
│   └── ResponseHeaderInjector.cs
└── StyloBotApiExtensions.cs
```

### Detection bridge

The orchestrator takes `HttpContext` today. Rather than refactoring every detector (33+ files), the API endpoint constructs a synthetic `HttpContext` from `DetectRequest`:

```csharp
app.MapPost("/api/v1/detect", async (
    DetectRequest request,
    IBlackboardOrchestrator orchestrator) =>
{
    var httpContext = SyntheticHttpContext.FromDetectRequest(request);
    var evidence = await orchestrator.DetectAsync(httpContext);
    return DetectResponse.FromEvidence(evidence);
});
```

`SyntheticHttpContext.FromDetectRequest()` maps:
- `request.headers` → `HttpContext.Request.Headers`
- `request.remoteIp` → `HttpContext.Connection.RemoteIpAddress`
- `request.path` → `HttpContext.Request.Path`
- `request.method` → `HttpContext.Request.Method`
- `request.protocol` → `HttpContext.Request.Scheme`
- `request.tls` → `HttpContext.Items["BotDetection.TlsInfo"]` (consumed by TLS detector)

Future: `IDetectionRequest` abstraction replaces `HttpContext` dependency across all detectors. Separate workstream.

### Registration

```csharp
// In Gateway or Console Program.cs
builder.Services.AddStyloBotApi(options => {
    options.EnableManagementEndpoints = true;  // Tier 3, requires OIDC
    options.EnableOpenApi = true;
    options.MaxBatchSize = 100;
});

app.MapStyloBotApi();  // registers /api/v1/* endpoints
```

### OpenAPI

Endpoints use `.WithOpenApi()` for spec generation. Served at `/api/v1/openapi.json`. This is the source of truth for all SDK code generation.

---

## Node SDK

### `@stylobot/core`

Zero dependencies, platform-agnostic. Works in Node, Deno, Bun, Cloudflare Workers.

**Contents:**
- TypeScript types: `Verdict`, `DetectRequest`, `DetectResponse`, `DetectionReason`, `Signal`, `Summary`, `Session`, `Signature`, `PaginatedResponse`
- Header constants: `STYLOBOT_HEADERS` object with all `X-StyloBot-*` names
- Header parser: `parseStyloBotHeaders(headers: Record<string, string>): Verdict`
- API client: `StyloBotClient` class

**`StyloBotClient`:**

```typescript
class StyloBotClient {
  constructor(options: {
    endpoint: string;
    apiKey?: string;        // Tier 2
    bearerToken?: string;   // Tier 3
    timeout?: number;       // default 5000ms
    retries?: number;       // default 1
  });

  // Detection (Tier 2)
  detect(request: DetectRequest): Promise<DetectResponse>;
  detectBatch(requests: DetectRequest[]): Promise<DetectResponse[]>;

  // Read (Tier 2)
  detections(params?: DetectionsQuery): Promise<PaginatedResponse<Detection>>;
  sessions(params?: SessionsQuery): Promise<PaginatedResponse<Session>>;
  session(id: string): Promise<Session>;
  signatures(params?: SignaturesQuery): Promise<PaginatedResponse<Signature>>;
  summary(params?: SummaryQuery): Promise<Summary>;
  timeseries(params?: TimeseriesQuery): Promise<TimeseriesPoint[]>;
  countries(params?: PaginationQuery): Promise<PaginatedResponse<CountryStat>>;
  endpoints(params?: PaginationQuery): Promise<PaginatedResponse<EndpointStat>>;
  topBots(params?: PaginationQuery): Promise<PaginatedResponse<BotStat>>;
  threats(params?: ThreatsQuery): Promise<PaginatedResponse<Threat>>;
  me(): Promise<ApiKeyInfo>;

  // Management (Tier 3)
  listPolicies(): Promise<Policy[]>;
  updatePolicy(name: string, policy: PolicyUpdate): Promise<Policy>;
  listManifests(): Promise<Manifest[]>;
  createKey(config: ApiKeyCreate): Promise<ApiKeyCreated>;
  revokeKey(id: string): Promise<void>;
  listKeys(): Promise<ApiKeyInfo[]>;
}
```

### `@stylobot/node`

Depends on `@stylobot/core`. Framework adapters.

**Express middleware:**

```typescript
import { styloBotMiddleware } from '@stylobot/node';

// Header mode (behind Gateway)
app.use(styloBotMiddleware({ mode: 'headers' }));

// API mode (sidecar)
app.use(styloBotMiddleware({
  mode: 'api',
  endpoint: 'http://localhost:5080',
  apiKey: process.env.STYLOBOT_API_KEY,
}));

// Both modes produce identical req.stylobot interface:
app.get('/api/data', (req, res) => {
  req.stylobot.isBot        // boolean
  req.stylobot.verdict      // Verdict object
  req.stylobot.signals      // Record<string, unknown>
  req.stylobot.reasons      // DetectionReason[]
  req.stylobot.meta         // { processingTimeMs, detectorsRun, ... }
});
```

**Fastify plugin:**

```typescript
import { styloBotPlugin } from '@stylobot/node';

fastify.register(styloBotPlugin, {
  mode: 'api',
  endpoint: 'http://localhost:5080',
  apiKey: process.env.STYLOBOT_API_KEY,
});

fastify.get('/api/data', async (request, reply) => {
  if (request.stylobot.isBot) {
    return reply.code(403).send({ error: 'Bot detected' });
  }
});
```

**Request extraction:**

The middleware extracts from the framework's request object:
- `method` from `req.method`
- `path` from `req.originalUrl` (Express) or `request.url` (Fastify)
- `headers` from `req.headers` (both)
- `remoteIp` from `req.ip` (Express, respects trust proxy) or `request.ip` (Fastify)
- `protocol` from `req.protocol`

No manual extraction needed by the consumer.

### TypeScript types

All types are generated to match the OpenAPI spec. The `Verdict` type is the core shared type used in both header parsing and API responses:

```typescript
interface Verdict {
  isBot: boolean;
  botProbability: number;
  confidence: number;
  botType: BotType | null;
  botName: string | null;
  riskBand: RiskBand;
  recommendedAction: RecommendedAction;
  threatScore: number;
  threatBand: ThreatBand;
}

type BotType = 'Unknown' | 'SearchEngine' | 'SocialMediaBot' | 'MonitoringBot'
  | 'Scraper' | 'MaliciousBot' | 'GoodBot' | 'VerifiedBot' | 'AiBot'
  | 'Tool' | 'ExploitScanner';

type RiskBand = 'Unknown' | 'VeryLow' | 'Low' | 'Elevated' | 'Medium'
  | 'High' | 'VeryHigh' | 'Verified';

type RecommendedAction = 'Allow' | 'Throttle' | 'Challenge' | 'Block';

type ThreatBand = 'None' | 'Low' | 'Elevated' | 'High' | 'Critical';
```

---

## What Does Not Change

- **`Mostlylucid.BotDetection`** (core library) - untouched
- **`Mostlylucid.BotDetection.UI`** - keeps internal `/_stylobot/api/*` for dashboard
- **Existing middleware pipeline** - public API is additive
- **Detection logic** - no detector changes
- **Auth model** - API keys and OIDC are existing systems, the API just gates on them

---

## Future SDKs

The OpenAPI spec at `/api/v1/openapi.json` enables:
- **C# SDK** - `Stylobot.Client` NuGet package, generated or hand-written
- **Python SDK** - `stylobot` PyPI package
- **Go SDK** - `github.com/stylobot/stylobot-go`
- **Ruby SDK** - `stylobot` gem

Each follows the same pattern: types from OpenAPI, thin client wrapping HTTP, optional framework middleware.