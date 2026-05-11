# Sidecar gRPC Integration & k6 Tuning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add gRPC mode to Node SDK and ASP.NET sidecar client, wire up two k6 test environments (Node+Sidecar, Caddy+Sidecar), and provide tuning scripts.

**Architecture:** The existing `Mostlylucid.BotDetection.Sidecar` gets a `STYLOBOT_GRPC_ONLY=true` flag that strips the REST surface. The Node SDK gets a third middleware mode (`grpc`) using `@grpc/grpc-js` and dynamic proto loading. A new thin `Mostlylucid.BotDetection.Sidecar.Client` project lets ASP.NET apps call the sidecar over gRPC using the same `HttpContext` extension API as in-process detection. k6 measures raw gRPC throughput (baseline), HTTP through Node middleware, and HTTP through Caddy plugin - all against the same sidecar.

**Tech Stack:** `@grpc/grpc-js`, `@grpc/proto-loader`, `Grpc.Net.Client`, `Grpc.Tools`, `google/protobuf`, k6 (gRPC + HTTP), Docker Compose, xcaddy (local build from `sdk/caddy/`).

---

## File Map

### Sidecar
- Modify: `src/Mostlylucid.BotDetection.Sidecar/Program.cs`

### Node SDK (`@stylobot/node`)
- Create: `sdk/node/packages/node/src/proto/detection.proto` (copy from `sdk/proto/detection.proto`)
- Create: `sdk/node/packages/node/src/grpc-client.ts`
- Modify: `sdk/node/packages/node/src/middleware.ts`
- Modify: `sdk/node/packages/node/package.json`
- Create: `sdk/node/packages/node/src/__tests__/grpc-middleware.test.ts`

### ASP.NET Sidecar Client
- Create: `src/Mostlylucid.BotDetection.Sidecar.Client/Mostlylucid.BotDetection.Sidecar.Client.csproj`
- Create: `src/Mostlylucid.BotDetection.Sidecar.Client/SidecarBotDetectionMiddleware.cs`
- Create: `src/Mostlylucid.BotDetection.Sidecar.Client/SidecarDetectionExtensions.cs`
- Modify: `mostlylucid.stylobot.sln`

### Test Environments
- Create: `tests/integration/upstream/index.js`
- Create: `tests/integration/upstream/package.json`
- Create: `tests/integration/upstream/Dockerfile`
- Create: `tests/integration/node-sidecar/app/index.mjs`
- Create: `tests/integration/node-sidecar/app/package.json`
- Create: `tests/integration/node-sidecar/app/Dockerfile`
- Create: `tests/integration/node-sidecar/docker-compose.yml`
- Create: `tests/integration/caddy-sidecar/Caddyfile`
- Create: `tests/integration/caddy-sidecar/Dockerfile` (xcaddy build from local `sdk/caddy/`)
- Create: `tests/integration/caddy-sidecar/docker-compose.yml`

### k6 Scripts
- Create: `tests/k6/lib/traffic-mix.js`
- Create: `tests/k6/baseline-grpc.js`
- Create: `tests/k6/node-sidecar.js`
- Create: `tests/k6/caddy-sidecar.js`
- Create: `tests/k6/README.md`

---

## Task 1: Sidecar GRPC_ONLY mode

**Files:**
- Modify: `src/Mostlylucid.BotDetection.Sidecar/Program.cs`

- [ ] **Step 1: Add the env var check and conditional REST registration**

In `Program.cs`, after the port parse and before `builder.Services.AddGrpc(...)`, read the flag:

```csharp
var grpcOnly = string.Equals(
    Environment.GetEnvironmentVariable("STYLOBOT_GRPC_ONLY"),
    "true", StringComparison.OrdinalIgnoreCase);
```

Then wrap the `AddStyloBotApi` call (currently at the bottom of service registration):

```csharp
// gRPC - primary high-throughput interface
builder.Services.AddGrpc(opts =>
{
    opts.MaxReceiveMessageSize = 1 * 1024 * 1024;
    opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// Bot detection (all 49 detectors, SQLite persistence in FOSS)
builder.Services.AddBotDetection();

// REST API only when not in grpc-only mode
if (!grpcOnly)
    builder.Services.AddStyloBotApi(opts => opts.EnableOpenApi = false);
```

And wrap the `MapStyloBotApi()` call after `var app = builder.Build()`:

```csharp
app.MapGrpcService<DetectionGrpcService>();

if (!grpcOnly)
    app.MapStyloBotApi();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", mode = grpcOnly ? "sidecar-grpc" : "sidecar", port }))
   .AllowAnonymous();
```

- [ ] **Step 2: Smoke-test gRPC-only mode**

```bash
cd /path/to/stylobot
STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar &
sleep 4

# Health check must return grpc mode
curl -s http://localhost:5090/health | grep '"mode":"sidecar-grpc"'

# REST must not be present (404)
curl -s -o /dev/null -w "%{http_code}" http://localhost:5090/api/v1/detect
# Expected: 404

kill %1
```

- [ ] **Step 3: Smoke-test REST mode (no flag)**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Sidecar &
sleep 4

curl -s -X POST http://localhost:5090/api/v1/detect \
  -H "Content-Type: application/json" \
  -d '{"method":"GET","path":"/","headers":{"User-Agent":"curl/7.0"},"remoteIp":"1.2.3.4"}' | jq .verdict.isBot
# Expected: prints true or false, not a 404

kill %1
```

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.Sidecar/Program.cs
git commit -m "feat(sidecar): add STYLOBOT_GRPC_ONLY env var to strip REST surface"
```

---

## Task 2: Copy proto into Node package and add gRPC deps

**Files:**
- Create: `sdk/node/packages/node/src/proto/detection.proto`
- Modify: `sdk/node/packages/node/package.json`

- [ ] **Step 1: Copy the proto file**

```bash
cp sdk/proto/detection.proto sdk/node/packages/node/src/proto/detection.proto
```

- [ ] **Step 2: Add gRPC dependencies to `package.json`**

Open `sdk/node/packages/node/package.json`. Add to `dependencies`:

```json
"@grpc/grpc-js": "^1.10.0",
"@grpc/proto-loader": "^0.7.0"
```

Also add the proto file to the published files (find the `"files"` array or add one if absent):

```json
"files": [
  "dist",
  "src/proto"
]
```

- [ ] **Step 3: Add dev type declarations to `devDependencies`**

```json
"@types/node": "^20.0.0"
```

(Already present in most Node packages - check first and skip if it is.)

- [ ] **Step 4: Install**

```bash
cd sdk/node
npm install --workspace=packages/node
```

Expected: lockfile updated, no errors.

- [ ] **Step 5: Commit**

```bash
git add sdk/node/packages/node/src/proto/detection.proto sdk/node/packages/node/package.json sdk/node/package-lock.json
git commit -m "feat(node-sdk): add grpc deps and bundle detection.proto"
```

---

## Task 3: Node SDK gRPC client

**Files:**
- Create: `sdk/node/packages/node/src/grpc-client.ts`

- [ ] **Step 1: Write `grpc-client.ts`**

```typescript
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { DetectRequest, Verdict, ThreatBand, RiskBand, RecommendedAction } from '@stylobot/core';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PROTO_PATH = join(__dirname, '../proto/detection.proto');

const pkgDef = protoLoader.loadSync(PROTO_PATH, {
  keepCase: false,
  longs: String,
  enums: String,
  defaults: true,
  oneofs: true,
});

const proto = grpc.loadPackageDefinition(pkgDef) as Record<string, unknown>;
// Navigate the nested namespace
const DetectionService = (proto['stylobot'] as Record<string, unknown>)['detection'] as Record<string, unknown>;
const DetectionServiceClient = (DetectionService['v1'] as Record<string, unknown>)['DetectionService'] as grpc.ServiceClientConstructor;

export interface GrpcRawResponse {
  isBot: boolean;
  botProbability: number;
  confidence: number;
  botType: string;
  botName: string;
  riskBand: string;       // e.g. "RISK_BAND_HIGH"
  recommendedAction: string; // e.g. "RECOMMENDED_ACTION_BLOCK"
  threatScore: number;
  threatBand: string;     // e.g. "THREAT_BAND_NONE"
  reasons: Array<{ detector: string; detail: string; impact: number }>;
  processingTimeMs: number;
  detectorsRun: number;
}

export function createGrpcDetectionClient(endpoint: string): grpc.Client {
  return new DetectionServiceClient(endpoint, grpc.credentials.createInsecure());
}

export function grpcDetect(client: grpc.Client, req: DetectRequest): Promise<GrpcRawResponse> {
  return new Promise((resolve, reject) => {
    (client as Record<string, unknown>)['Detect'](
      {
        method: req.method,
        path: req.path,
        headers: req.headers,
        remoteIp: req.remoteIp,
        protocol: req.protocol ?? 'https',
      },
      (err: grpc.ServiceError | null, response: GrpcRawResponse) => {
        if (err) reject(err);
        else resolve(response);
      },
    );
  });
}

const RISK_MAP: Record<string, RiskBand> = {
  RISK_BAND_VERY_LOW: 'VeryLow', RISK_BAND_LOW: 'Low', RISK_BAND_ELEVATED: 'Elevated',
  RISK_BAND_MEDIUM: 'Medium', RISK_BAND_HIGH: 'High', RISK_BAND_VERY_HIGH: 'VeryHigh',
  RISK_BAND_VERIFIED: 'Verified',
};

const ACTION_MAP: Record<string, RecommendedAction> = {
  RECOMMENDED_ACTION_ALLOW: 'Allow', RECOMMENDED_ACTION_THROTTLE: 'Throttle',
  RECOMMENDED_ACTION_CHALLENGE: 'Challenge', RECOMMENDED_ACTION_BLOCK: 'Block',
};

const THREAT_MAP: Record<string, ThreatBand> = {
  THREAT_BAND_NONE: 'None', THREAT_BAND_LOW: 'Low', THREAT_BAND_ELEVATED: 'Elevated',
  THREAT_BAND_HIGH: 'High', THREAT_BAND_CRITICAL: 'Critical',
};

export function mapGrpcVerdict(r: GrpcRawResponse): Verdict {
  return {
    isBot: r.isBot,
    botProbability: r.botProbability,
    confidence: r.confidence,
    botType: r.botType || null,
    botName: r.botName || null,
    riskBand: RISK_MAP[r.riskBand] ?? 'Unknown',
    recommendedAction: ACTION_MAP[r.recommendedAction] ?? 'Allow',
    threatScore: r.threatScore,
    threatBand: THREAT_MAP[r.threatBand] ?? 'None',
  };
}
```

- [ ] **Step 2: Verify it compiles**

```bash
cd sdk/node/packages/node
npx tsc --noEmit
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add sdk/node/packages/node/src/grpc-client.ts
git commit -m "feat(node-sdk): gRPC client factory and response mapper"
```

---

## Task 4: Node SDK middleware grpc mode

**Files:**
- Modify: `sdk/node/packages/node/src/middleware.ts`

- [ ] **Step 1: Add gRPC branch to `styloBotMiddleware`**

Add the import at the top of `middleware.ts`:

```typescript
import { createGrpcDetectionClient, grpcDetect, mapGrpcVerdict } from './grpc-client.js';
```

Add `'grpc'` to the `mode` union in `StyloBotMiddlewareOptions`:

```typescript
export interface StyloBotMiddlewareOptions {
  mode: 'headers' | 'api' | 'grpc';
  endpoint?: string;   // api mode: http://host:port  |  grpc mode: host:port (no scheme)
  apiKey?: string;
  timeout?: number;
}
```

Add the `grpc` branch inside `styloBotMiddleware`, before the final `headers` return:

```typescript
if (options.mode === 'grpc') {
  if (!options.endpoint) throw new Error('endpoint is required for grpc mode (host:port, no scheme)');

  const client = createGrpcDetectionClient(options.endpoint);

  return async (req: Request, res: Response, next: NextFunction) => {
    try {
      const detectReq = extractDetectRequest(req);
      const raw = await grpcDetect(client, detectReq);
      const verdict = mapGrpcVerdict(raw);
      req.stylobot = {
        isBot: verdict.isBot,
        verdict,
        signals: {},
        reasons: raw.reasons,
        meta: { processingTimeMs: raw.processingTimeMs, detectorsRun: raw.detectorsRun, policyName: null, aiRan: false },
      };
    } catch {
      req.stylobot = { isBot: false, verdict: EMPTY_VERDICT, signals: {}, reasons: [], meta: null };
    }
    next();
  };
}
```

The `grpc` branch must appear BEFORE the `headers` fallback return at the end of the function.

- [ ] **Step 2: Verify compilation**

```bash
cd sdk/node/packages/node
npx tsc --noEmit
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add sdk/node/packages/node/src/middleware.ts
git commit -m "feat(node-sdk): add grpc middleware mode via @grpc/grpc-js"
```

---

## Task 5: Node SDK gRPC middleware tests

**Files:**
- Create: `sdk/node/packages/node/src/__tests__/grpc-middleware.test.ts`

- [ ] **Step 1: Write the test file**

```typescript
import assert from 'node:assert/strict';
import { describe, it, before, after } from 'node:test';
import * as grpc from '@grpc/grpc-js';
import * as protoLoader from '@grpc/proto-loader';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { styloBotMiddleware } from '../middleware.js';

const __dirname = dirname(fileURLToPath(import.meta.url));
const PROTO_PATH = join(__dirname, '../proto/detection.proto');

const pkgDef = protoLoader.loadSync(PROTO_PATH, {
  keepCase: false, longs: String, enums: String, defaults: true, oneofs: true,
});
const proto = grpc.loadPackageDefinition(pkgDef) as Record<string, unknown>;
const svcDef = ((proto['stylobot'] as Record<string, unknown>)['detection'] as Record<string, unknown>)['v1'] as Record<string, unknown>;

function startMockServer(isBot: boolean, riskBand: string, action: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const server = new grpc.Server();
    server.addService((svcDef['DetectionService'] as grpc.ServiceClientConstructor).service, {
      Detect: (_: unknown, cb: (err: null, res: object) => void) => cb(null, {
        isBot, botProbability: isBot ? 0.95 : 0.05, confidence: 0.9,
        riskBand, recommendedAction: action, threatBand: 'THREAT_BAND_NONE',
        botType: '', botName: '', reasons: [], processingTimeMs: 1.2, detectorsRun: 12,
      }),
    });
    server.bindAsync('127.0.0.1:0', grpc.ServerCredentials.createInsecure(), (err, port) => {
      if (err) return reject(err);
      resolve(`127.0.0.1:${port}`);
    });
  });
}

function mockReq(ua: string): IncomingMessage {
  return {
    method: 'GET', url: '/test', headers: { 'user-agent': ua },
    socket: { remoteAddress: '127.0.0.1' },
  } as unknown as IncomingMessage;
}

describe('styloBotMiddleware grpc mode', () => {
  let endpoint: string;

  before(async () => {
    endpoint = await startMockServer(true, 'RISK_BAND_HIGH', 'RECOMMENDED_ACTION_BLOCK');
  });

  it('populates req.stylobot from grpc response', async () => {
    const mw = styloBotMiddleware({ mode: 'grpc', endpoint });
    const req = mockReq('Googlebot') as unknown as Parameters<typeof mw>[0];
    let nextCalled = false;
    await new Promise<void>(resolve => {
      mw(req, {} as ServerResponse, () => { nextCalled = true; resolve(); });
    });
    assert.equal(nextCalled, true);
    assert.equal(req.stylobot.isBot, true);
    assert.equal(req.stylobot.verdict.riskBand, 'High');
    assert.equal(req.stylobot.verdict.recommendedAction, 'Block');
    assert.ok(req.stylobot.verdict.botProbability > 0.9);
  });

  it('fails open when server is unreachable', async () => {
    const mw = styloBotMiddleware({ mode: 'grpc', endpoint: '127.0.0.1:1' });
    const req = mockReq('Chrome') as unknown as Parameters<typeof mw>[0];
    await new Promise<void>(resolve => {
      mw(req, {} as ServerResponse, () => resolve());
    });
    assert.equal(req.stylobot.isBot, false);
    assert.equal(req.stylobot.verdict.botProbability, 0);
  });

  it('throws when endpoint missing', () => {
    assert.throws(
      () => styloBotMiddleware({ mode: 'grpc' }),
      /endpoint is required/,
    );
  });
});
```

- [ ] **Step 2: Run tests**

```bash
cd sdk/node/packages/node
node --experimental-strip-types --test src/__tests__/grpc-middleware.test.ts
```

Expected: 3 passing tests.

- [ ] **Step 3: Commit**

```bash
git add sdk/node/packages/node/src/__tests__/grpc-middleware.test.ts
git commit -m "test(node-sdk): grpc middleware mode - populates verdict, fails open, validates endpoint"
```

---

## Task 6: ASP.NET sidecar client project

**Files:**
- Create: `src/Mostlylucid.BotDetection.Sidecar.Client/Mostlylucid.BotDetection.Sidecar.Client.csproj`
- Create: `src/Mostlylucid.BotDetection.Sidecar.Client/SidecarBotDetectionMiddleware.cs`
- Create: `src/Mostlylucid.BotDetection.Sidecar.Client/SidecarDetectionExtensions.cs`
- Modify: `mostlylucid.stylobot.sln`

- [ ] **Step 1: Create the `.csproj`**

`src/Mostlylucid.BotDetection.Sidecar.Client/Mostlylucid.BotDetection.Sidecar.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Mostlylucid.BotDetection.Sidecar.Client</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Grpc.Net.Client" Version="2.67.0" />
    <PackageReference Include="Google.Protobuf" Version="3.30.2" />
    <PackageReference Include="Grpc.Tools" Version="2.67.0" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <Protobuf Include="..\..\sdk\proto\detection.proto" GrpcServices="Client" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Mostlylucid.BotDetection\Mostlylucid.BotDetection.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write `SidecarBotDetectionMiddleware.cs`**

```csharp
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Proto = Stylobot.Detection.V1;

namespace Mostlylucid.BotDetection.Sidecar.Client;

public sealed class SidecarBotDetectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Proto.DetectionService.DetectionServiceClient _client;
    private readonly int _timeoutMs;

    public SidecarBotDetectionMiddleware(
        RequestDelegate next,
        Proto.DetectionService.DetectionServiceClient client,
        SidecarClientOptions options)
    {
        _next = next;
        _client = client;
        _timeoutMs = options.TimeoutMs;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            var req = BuildRequest(context);
            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            var resp = await _client.DetectAsync(req, deadline: deadline,
                cancellationToken: context.RequestAborted);
            WriteToContext(context, resp);
        }
        catch (RpcException)
        {
            // fail open — do not block the request
        }
        await _next(context);
    }

    private static Proto.DetectRequest BuildRequest(HttpContext ctx)
    {
        var req = new Proto.DetectRequest
        {
            Method = ctx.Request.Method,
            Path = ctx.Request.Path + ctx.Request.QueryString,
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Protocol = ctx.Request.IsHttps ? "https" : "http",
        };
        foreach (var (key, value) in ctx.Request.Headers)
            req.Headers[key.ToLowerInvariant()] = value.ToString();
        return req;
    }

    private static void WriteToContext(HttpContext ctx, Proto.DetectResponse resp)
    {
        var evidence = new AggregatedEvidence
        {
            BotProbability = resp.BotProbability,
            Confidence = resp.Confidence,
            RiskBand = MapRiskBand(resp.RiskBand),
            PrimaryBotType = string.IsNullOrEmpty(resp.BotType) ? null : Enum.TryParse<BotType>(resp.BotType, out var bt) ? bt : null,
            PrimaryBotName = string.IsNullOrEmpty(resp.BotName) ? null : resp.BotName,
            ThreatScore = resp.ThreatScore,
            ThreatBand = MapThreatBand(resp.ThreatBand),
            TotalProcessingTimeMs = resp.ProcessingTimeMs,
        };

        ctx.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;

        var legacyResult = new BotDetectionResult
        {
            IsBot = resp.IsBot,
            ConfidenceScore = resp.BotProbability,
            BotType = evidence.PrimaryBotType,
            BotName = evidence.PrimaryBotName,
        };
        ctx.Items[BotDetectionMiddleware.BotDetectionResultKey] = legacyResult;
    }

    private static RiskBand MapRiskBand(Proto.RiskBand b) => b switch
    {
        Proto.RiskBand.VeryLow  => RiskBand.VeryLow,
        Proto.RiskBand.Low      => RiskBand.Low,
        Proto.RiskBand.Elevated => RiskBand.Elevated,
        Proto.RiskBand.Medium   => RiskBand.Medium,
        Proto.RiskBand.High     => RiskBand.High,
        Proto.RiskBand.VeryHigh => RiskBand.VeryHigh,
        Proto.RiskBand.Verified => RiskBand.Verified,
        _                       => RiskBand.Unknown,
    };

    private static ThreatBand MapThreatBand(Proto.ThreatBand b) => b switch
    {
        Proto.ThreatBand.Low      => ThreatBand.Low,
        Proto.ThreatBand.Elevated => ThreatBand.Elevated,
        Proto.ThreatBand.High     => ThreatBand.High,
        Proto.ThreatBand.Critical => ThreatBand.Critical,
        _                         => ThreatBand.None,
    };
}
```

- [ ] **Step 3: Write `SidecarDetectionExtensions.cs`**

```csharp
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Proto = Stylobot.Detection.V1;

namespace Mostlylucid.BotDetection.Sidecar.Client;

public sealed class SidecarClientOptions
{
    /// <summary>gRPC endpoint, e.g. "http://localhost:5090"</summary>
    public required string Endpoint { get; set; }
    /// <summary>Per-request gRPC deadline in milliseconds. Fails open on timeout. Default: 50ms.</summary>
    public int TimeoutMs { get; set; } = 50;
}

public static class SidecarDetectionExtensions
{
    /// <summary>
    /// Registers the StyloBot sidecar gRPC client.
    /// Call <see cref="UseSidecarBotDetection"/> in the middleware pipeline.
    /// </summary>
    public static IServiceCollection AddSidecarBotDetection(
        this IServiceCollection services,
        Action<SidecarClientOptions> configure)
    {
        var options = new SidecarClientOptions { Endpoint = "http://localhost:5090" };
        configure(options);
        services.AddSingleton(options);

        var channel = GrpcChannel.ForAddress(options.Endpoint);
        services.AddSingleton(new Proto.DetectionService.DetectionServiceClient(channel));

        return services;
    }

    /// <summary>
    /// Adds the StyloBot sidecar middleware. Place before any middleware that reads IsBot() etc.
    /// </summary>
    public static IApplicationBuilder UseSidecarBotDetection(this IApplicationBuilder app)
        => app.UseMiddleware<SidecarBotDetectionMiddleware>();
}
```

- [ ] **Step 4: Add the project to the solution**

```bash
dotnet sln mostlylucid.stylobot.sln add src/Mostlylucid.BotDetection.Sidecar.Client/Mostlylucid.BotDetection.Sidecar.Client.csproj
```

- [ ] **Step 5: Build to verify codegen and compilation**

```bash
dotnet build src/Mostlylucid.BotDetection.Sidecar.Client
```

Expected: 0 errors. Grpc.Tools generates the proto client stubs during build.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.Sidecar.Client/ mostlylucid.stylobot.sln
git commit -m "feat(sidecar-client): ASP.NET gRPC sidecar client with HttpContext extension compatibility"
```

---

## Task 7: Shared upstream echo server

**Files:**
- Create: `tests/integration/upstream/index.js`
- Create: `tests/integration/upstream/package.json`
- Create: `tests/integration/upstream/Dockerfile`

- [ ] **Step 1: Write the echo server**

`tests/integration/upstream/index.js`:

```js
import { createServer } from 'node:http';

const PORT = parseInt(process.env.PORT ?? '3000', 10);

createServer((req, res) => {
  const chunks = [];
  req.on('data', c => chunks.push(c));
  req.on('end', () => {
    const body = {
      method: req.method,
      url: req.url,
      // forward detection headers so k6 can assert on them
      stylobotIsBot: req.headers['x-stylobot-isbot'],
      stylobotProbability: req.headers['x-stylobot-probability'],
      stylobotAction: req.headers['x-stylobot-action'],
    };
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify(body));
  });
}).listen(PORT, () => console.log(`upstream echo on :${PORT}`));
```

`tests/integration/upstream/package.json`:

```json
{
  "name": "stylobot-test-upstream",
  "type": "module",
  "version": "0.0.1",
  "scripts": { "start": "node index.js" }
}
```

`tests/integration/upstream/Dockerfile`:

```dockerfile
FROM node:22-alpine
WORKDIR /app
COPY package.json .
COPY index.js .
EXPOSE 3000
CMD ["node", "index.js"]
```

- [ ] **Step 2: Verify locally**

```bash
node tests/integration/upstream/index.js &
curl -s http://localhost:3000/test | jq .
kill %1
```

Expected: JSON with `method`, `url`, `stylobotIsBot: null` (no detection in direct mode).

- [ ] **Step 3: Commit**

```bash
git add tests/integration/upstream/
git commit -m "test(integration): upstream echo server for k6 test environments"
```

---

## Task 8: Node + Sidecar docker-compose

**Files:**
- Create: `tests/integration/node-sidecar/app/index.mjs`
- Create: `tests/integration/node-sidecar/app/package.json`
- Create: `tests/integration/node-sidecar/app/Dockerfile`
- Create: `tests/integration/node-sidecar/docker-compose.yml`

- [ ] **Step 1: Write the Node test app**

`tests/integration/node-sidecar/app/index.mjs`:

```js
import express from 'express';
import { styloBotMiddleware } from '@stylobot/node';

const app = express();
const mode = process.env.STYLOBOT_MODE ?? 'grpc';
const endpoint = process.env.SIDECAR_ENDPOINT ?? 'sidecar:5090';

app.use(styloBotMiddleware({ mode, endpoint }));

app.use((req, res) => {
  const sb = req.stylobot;
  // Propagate detection as response headers so k6 can inspect them without parsing JSON
  res.set('X-StyloBot-IsBot', String(sb.isBot));
  res.set('X-StyloBot-Probability', String(sb.verdict.botProbability.toFixed(4)));
  res.set('X-StyloBot-Action', sb.verdict.recommendedAction);
  res.json({
    path: req.path,
    isBot: sb.isBot,
    probability: sb.verdict.botProbability,
    riskBand: sb.verdict.riskBand,
    action: sb.verdict.recommendedAction,
  });
});

app.listen(3000, () => console.log(`Node test app listening :3000 (stylobot mode=${mode})`));
```

`tests/integration/node-sidecar/app/package.json`:

```json
{
  "name": "stylobot-node-test-app",
  "type": "module",
  "version": "0.0.1",
  "scripts": { "start": "node index.mjs" },
  "dependencies": {
    "@stylobot/node": "file:../../../../sdk/node/packages/node",
    "@stylobot/core": "file:../../../../sdk/node/packages/core",
    "express": "^4.21.0"
  }
}
```

`tests/integration/node-sidecar/app/Dockerfile`:

```dockerfile
FROM node:22-alpine
WORKDIR /app
# Copy SDK packages so the file: references resolve inside Docker
COPY sdk/node/packages/core /sdk/core
COPY sdk/node/packages/node /sdk/node
COPY tests/integration/node-sidecar/app /app
RUN npm install
EXPOSE 3000
CMD ["node", "index.mjs"]
```

Note: this Dockerfile's build context is the repo root (so the sdk/ copy works).

- [ ] **Step 2: Write `docker-compose.yml`**

`tests/integration/node-sidecar/docker-compose.yml`:

```yaml
services:
  upstream:
    build:
      context: ../../upstream
    ports:
      - "13000:3000"

  app:
    build:
      context: ../../../..   # repo root — needed for sdk/ paths
      dockerfile: tests/integration/node-sidecar/app/Dockerfile
    ports:
      - "13001:3000"
    environment:
      STYLOBOT_MODE: grpc
      SIDECAR_ENDPOINT: host.docker.internal:5090
    extra_hosts:
      - "host.docker.internal:host-gateway"   # Linux compat

  # The sidecar is expected to run on the host:
  #   STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar
  # Default port 5090 is reachable via host.docker.internal:5090
```

- [ ] **Step 3: Start the sidecar and bring up compose**

```bash
# Terminal 1 — sidecar on host
STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar

# Terminal 2 — compose
docker compose -f tests/integration/node-sidecar/docker-compose.yml up --build

# Terminal 3 — verify
curl -s http://localhost:13001/ -H "User-Agent: curl/8.0" | jq .
# Expected: { "isBot": true, "probability": 0.xx, ... }

curl -s http://localhost:13001/ \
  -H "User-Agent: Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36" | jq .
# Expected: { "isBot": false, ... }
```

- [ ] **Step 4: Commit**

```bash
git add tests/integration/node-sidecar/
git commit -m "test(integration): Node + Sidecar gRPC docker-compose test environment"
```

---

## Task 9: Caddy + Sidecar docker-compose

**Files:**
- Create: `tests/integration/caddy-sidecar/Caddyfile`
- Create: `tests/integration/caddy-sidecar/Dockerfile`
- Create: `tests/integration/caddy-sidecar/docker-compose.yml`

- [ ] **Step 1: Write `Caddyfile`**

`tests/integration/caddy-sidecar/Caddyfile`:

```
{
    admin off
    auto_https off
}

:80 {
    stylobot {
        endpoint sidecar:5090
        timeout  50ms
        on_block 403
    }

    reverse_proxy upstream:3000
}
```

- [ ] **Step 2: Write `Dockerfile` for custom Caddy (xcaddy from local source)**

`tests/integration/caddy-sidecar/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
FROM caddy:builder AS builder

# Copy local caddy plugin source
COPY sdk/caddy /caddy-stylobot

# Build caddy with the local plugin (--with path/to/module=local/path)
RUN xcaddy build \
    --with github.com/scottgal/caddy-stylobot=/caddy-stylobot

FROM caddy:latest
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
EXPOSE 80
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
```

Build context must be repo root so `sdk/caddy` is available.

- [ ] **Step 3: Write `docker-compose.yml`**

`tests/integration/caddy-sidecar/docker-compose.yml`:

```yaml
services:
  upstream:
    build:
      context: ../../upstream

  sidecar:
    # Build the sidecar from published packages OR run it on host.
    # Simplest for local dev: run on host and expose via host.docker.internal.
    # If containerised, set STYLOBOT_GRPC_ONLY=true and ASPNETCORE_URLS=http://+:5090.
    image: ghcr.io/scottgal/stylobot-sidecar:latest
    ports:
      - "5090:5090"
    environment:
      STYLOBOT_GRPC_ONLY: "true"
      ASPNETCORE_URLS: "http://+:5090"
    # For local dev without the published image, comment out image/ports above and
    # run the sidecar on host: STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar
    # Then replace `sidecar:5090` references with `host.docker.internal:5090` in Caddyfile.

  caddy:
    build:
      context: ../../../..   # repo root
      dockerfile: tests/integration/caddy-sidecar/Dockerfile
    ports:
      - "14080:80"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
    depends_on:
      - upstream
      - sidecar
```

- [ ] **Step 4: Build and verify**

```bash
# Run sidecar on host (if not using container image)
STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar &

# Bring up compose (sidecar service will fail/be skipped if using host mode; OK)
docker compose -f tests/integration/caddy-sidecar/docker-compose.yml up --build caddy upstream

# Verify human request passes through
curl -s -o /dev/null -w "%{http_code}" http://localhost:14080/ \
  -H "User-Agent: Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)"
# Expected: 200

# Verify known bot gets blocked
curl -s -o /dev/null -w "%{http_code}" http://localhost:14080/ \
  -H "User-Agent: curl/8.0"
# Expected: 403
```

- [ ] **Step 5: Commit**

```bash
git add tests/integration/caddy-sidecar/
git commit -m "test(integration): Caddy + Sidecar gRPC docker-compose test environment"
```

---

## Task 10: k6 traffic mix library

**Files:**
- Create: `tests/k6/lib/traffic-mix.js`

- [ ] **Step 1: Write the shared traffic mix module**

`tests/k6/lib/traffic-mix.js`:

```js
// Weighted UA pool: 70% human-ish, 20% tool, 10% known scanners
const UAS = [
  // Human (weight 70)
  { ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36', weight: 30 },
  { ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_4) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15', weight: 20 },
  { ua: 'Mozilla/5.0 (X11; Linux x86_64; rv:125.0) Gecko/20100101 Firefox/125.0', weight: 20 },
  // Tool (weight 20)
  { ua: 'curl/8.7.1', weight: 10 },
  { ua: 'python-requests/2.31.0', weight: 10 },
  // Scanner (weight 10)
  { ua: 'Googlebot/2.1 (+http://www.google.com/bot.html)', weight: 5 },
  { ua: 'AhrefsBot/7.0; +http://ahrefs.com/robot/', weight: 3 },
  { ua: 'SemrushBot/7~bl; +http://www.semrush.com/bot.html', weight: 2 },
];

const PATHS = [
  '/', '/about', '/products', '/api/data',
  '/wp-admin/', '/admin', '/.env', '/api/v1/users',
];

// Weighted random selection
const total = UAS.reduce((s, u) => s + u.weight, 0);
function pickUa() {
  let r = Math.random() * total;
  for (const entry of UAS) { r -= entry.weight; if (r <= 0) return entry.ua; }
  return UAS[0].ua;
}

export function randomPath() {
  return PATHS[Math.floor(Math.random() * PATHS.length)];
}

export function requestHeaders() {
  return {
    'User-Agent': pickUa(),
    'Accept': 'text/html,application/xhtml+xml,*/*;q=0.8',
    'Accept-Language': 'en-US,en;q=0.9',
  };
}

export function assertStylobotHeaders(res) {
  return {
    'has X-StyloBot-IsBot': (r) => r.headers['x-stylobot-isbot'] !== undefined,
    'has X-StyloBot-Action': (r) => r.headers['x-stylobot-action'] !== undefined,
    'status not 5xx': (r) => r.status < 500,
  };
}
```

- [ ] **Step 2: Commit**

```bash
git add tests/k6/lib/traffic-mix.js
git commit -m "test(k6): shared traffic mix module - weighted UA pool and assertion helpers"
```

---

## Task 11: k6 baseline gRPC script

**Files:**
- Create: `tests/k6/baseline-grpc.js`

- [ ] **Step 1: Write the baseline gRPC k6 script**

`tests/k6/baseline-grpc.js`:

```js
import grpc from 'k6/net/grpc';
import { check, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { requestHeaders, randomPath } from './lib/traffic-mix.js';

const client = new grpc.Client();
client.load(['../../sdk/proto'], 'detection.proto');

const detectionLatency = new Trend('detection_latency_ms', true);
const botCount = new Counter('bot_detections');
const humanCount = new Counter('human_detections');

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '15s', target: 20 },
        { duration: '30s', target: 50 },
        { duration: '15s', target: 100 },
        { duration: '30s', target: 100 },
        { duration: '10s', target: 0 },
      ],
    },
  },
  thresholds: {
    grpc_req_duration: ['p(95)<15', 'p(99)<30'],
    detection_latency_ms: ['p(99)<10'],
  },
};

const SIDECAR = __ENV.SIDECAR ?? 'localhost:5090';

export default function () {
  client.connect(SIDECAR, { plaintext: true });

  const headers = requestHeaders();
  const response = client.invoke('stylobot.detection.v1.DetectionService/Detect', {
    method: 'GET',
    path: randomPath(),
    headers: { 'user-agent': headers['User-Agent'] },
    remote_ip: `${Math.floor(Math.random() * 255)}.${Math.floor(Math.random() * 255)}.${Math.floor(Math.random() * 255)}.1`,
    protocol: 'https',
  });

  check(response, {
    'status OK': (r) => r.status === grpc.StatusOK,
    'has botProbability': (r) => typeof r.message?.botProbability === 'number',
  });

  if (response.status === grpc.StatusOK && response.message) {
    detectionLatency.add(response.message.processingTimeMs ?? 0);
    if (response.message.isBot) botCount.add(1);
    else humanCount.add(1);
  }

  client.close();
}
```

- [ ] **Step 2: Run the baseline**

```bash
# Sidecar must be running first:
STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar &
sleep 4

k6 run tests/k6/baseline-grpc.js
```

Expected output:
- `grpc_req_duration p(99) < 30ms`
- `detection_latency_ms p(99) < 10ms` (pure detection cost from the sidecar response field)
- Bot + human counts reflecting the traffic mix

- [ ] **Step 3: Commit**

```bash
git add tests/k6/baseline-grpc.js
git commit -m "test(k6): baseline gRPC detection throughput - ramp to 100 VUs, p99 thresholds"
```

---

## Task 12: k6 Node + Sidecar script

**Files:**
- Create: `tests/k6/node-sidecar.js`

- [ ] **Step 1: Write the Node k6 script**

`tests/k6/node-sidecar.js`:

```js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';
import { requestHeaders, randomPath, assertStylobotHeaders } from './lib/traffic-mix.js';

const e2eLatency = new Trend('e2e_latency_ms', true);

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '15s', target: 20 },
        { duration: '30s', target: 50 },
        { duration: '30s', target: 50 },
        { duration: '10s', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<200', 'p(99)<400'],
    http_req_failed: ['rate<0.01'],
  },
};

const APP = __ENV.APP ?? 'http://localhost:13001';

export default function () {
  const headers = requestHeaders();
  const res = http.get(`${APP}${randomPath()}`, { headers });

  e2eLatency.add(res.timings.duration);

  check(res, {
    ...assertStylobotHeaders(res),
    'not server error': (r) => r.status < 500,
  });

  // Bot traffic: no sleep (bot behaviour)
  // Human traffic: small pause
  const isBot = res.headers['X-Stylobot-Isbot'] === 'true';
  if (!isBot) sleep(Math.random() * 0.3);
}
```

- [ ] **Step 2: Run against the compose environment**

```bash
# Sidecar + compose must be running (Task 8)
k6 run tests/k6/node-sidecar.js

# Compare p99 against baseline:
# baseline gRPC p99 is the detection cost
# node-sidecar p99 minus baseline = Express + gRPC round-trip overhead
```

- [ ] **Step 3: Commit**

```bash
git add tests/k6/node-sidecar.js
git commit -m "test(k6): Node+Sidecar HTTP load test - ramp to 50 VUs, header assertions"
```

---

## Task 13: k6 Caddy + Sidecar script and README

**Files:**
- Create: `tests/k6/caddy-sidecar.js`
- Create: `tests/k6/README.md`

- [ ] **Step 1: Write the Caddy k6 script**

`tests/k6/caddy-sidecar.js`:

```js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { requestHeaders, randomPath, assertStylobotHeaders } from './lib/traffic-mix.js';

const blockedReqs = new Counter('blocked_requests');
const e2eLatency = new Trend('e2e_latency_ms', true);

export const options = {
  scenarios: {
    ramp: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '15s', target: 20 },
        { duration: '30s', target: 100 },
        { duration: '30s', target: 100 },
        { duration: '10s', target: 0 },
      ],
    },
  },
  thresholds: {
    // Caddy gRPC path is lower latency than Node HTTP path
    http_req_duration: ['p(95)<100', 'p(99)<200'],
    http_req_failed: ['rate<0.005'],  // only server errors; 403s are expected
  },
};

const CADDY = __ENV.CADDY ?? 'http://localhost:14080';

export default function () {
  const headers = requestHeaders();
  const res = http.get(`${CADDY}${randomPath()}`, { headers });

  e2eLatency.add(res.timings.duration);

  if (res.status === 403) {
    blockedReqs.add(1);
  }

  check(res, {
    'not server error': (r) => r.status < 500,
    // 200 (human) or 403 (bot blocked) are both correct outcomes
    'expected status': (r) => r.status === 200 || r.status === 403,
    ...assertStylobotHeaders(res),
  });

  const isBot = res.headers['X-Stylobot-Isbot'] === 'true';
  if (!isBot && res.status === 200) sleep(Math.random() * 0.2);
}
```

- [ ] **Step 2: Write `README.md`**

`tests/k6/README.md`:

```markdown
# StyloBot k6 Load Tests

Three scripts measuring detection cost at different integration depths.

## Prerequisites

- k6 installed: https://k6.io/docs/get-started/installation/
- Sidecar running: `STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar`
- Docker Compose for Node and Caddy environments (see `tests/integration/`)

## Scripts

| Script | Measures | Environment |
|---|---|---|
| `baseline-grpc.js` | Raw detection throughput (gRPC direct) | Sidecar on host |
| `node-sidecar.js` | Node HTTP + gRPC sidecar round-trip | `tests/integration/node-sidecar/` compose |
| `caddy-sidecar.js` | Caddy reverse proxy + gRPC sidecar | `tests/integration/caddy-sidecar/` compose |

## Quick start

```bash
# 1. Start sidecar
STYLOBOT_GRPC_ONLY=true dotnet run --project src/Mostlylucid.BotDetection.Sidecar &

# 2. Baseline - direct gRPC
k6 run tests/k6/baseline-grpc.js

# 3. Node+Sidecar (docker compose must be running)
docker compose -f tests/integration/node-sidecar/docker-compose.yml up -d
k6 run tests/k6/node-sidecar.js

# 4. Caddy+Sidecar (docker compose must be running)
docker compose -f tests/integration/caddy-sidecar/docker-compose.yml up -d
k6 run tests/k6/caddy-sidecar.js
```

## Env vars

| Var | Default | Meaning |
|---|---|---|
| `SIDECAR` | `localhost:5090` | gRPC endpoint for baseline script |
| `APP` | `http://localhost:13001` | Node app URL |
| `CADDY` | `http://localhost:14080` | Caddy URL |

Override: `k6 run -e SIDECAR=other-host:5090 tests/k6/baseline-grpc.js`

## Tuning reference

**Goal:** minimise the detection overhead added to each request.

- `detection_latency_ms` (baseline) — pure sidecar processing cost. Target: p99 < 10ms.
- `e2e_latency_ms` in node script minus baseline — HTTP round-trip + Express overhead.
- `e2e_latency_ms` in caddy script minus baseline — Caddy gRPC overhead (should be ~0.5ms).
- SQLite write contention appears as a step-change in p99 as VUs increase past ~200.
```

- [ ] **Step 3: Run full Caddy scenario**

```bash
docker compose -f tests/integration/caddy-sidecar/docker-compose.yml up -d
k6 run tests/k6/caddy-sidecar.js
```

Expected:
- `http_req_duration p(99) < 200ms`
- `blocked_requests` counter > 0 (bot UAs getting 403 from Caddy)
- `expected status` check passes at >99%

- [ ] **Step 4: Commit**

```bash
git add tests/k6/caddy-sidecar.js tests/k6/README.md
git commit -m "test(k6): Caddy+Sidecar HTTP load test + tuning README"
```

---

## Self-Review Checklist

- [x] **Task 1** covers `STYLOBOT_GRPC_ONLY` sidecar mode
- [x] **Tasks 2-5** cover Node SDK gRPC mode end-to-end (deps, client, middleware, tests)
- [x] **Task 6** covers ASP.NET sidecar client project with HttpContext compatibility
- [x] **Tasks 7-9** cover both docker-compose environments with an echo upstream
- [x] **Tasks 10-13** cover k6 scripts: baseline gRPC, Node, Caddy, README
- [x] No placeholder code — all steps contain real implementation
- [x] Type consistency: `GrpcRawResponse` defined in Task 3 used in Task 4; `SidecarClientOptions` defined in Task 6 extension file
- [x] The `AggregatedEvidence` constructor uses only the 3 `required` properties (`BotProbability`, `Confidence`, `RiskBand`) plus optional inits
- [x] The `RiskBand` / `ThreatBand` / `RecommendedAction` enum prefixes in proto enums (`RISK_BAND_HIGH`) are stripped by the mapping dictionaries in `grpc-client.ts`
