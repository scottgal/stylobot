# Go + TypeScript SDK Redesign

## Goal

Reshape both language SDKs so each has a standalone reusable client layer that framework integrations (Caddy plugin, Express middleware) consume as a dependency. Add `RenderWidget` gRPC RPC for hybrid server-side rendering over the sidecar connection.

## Current State

**Go:** everything lives in `sdk/caddy/` and is coupled to the Caddy module system. The proto import path (`github.com/scottgal/caddy-stylobot/proto`) is inside the Caddy plugin module, making the gRPC client unusable without Caddy.

**TypeScript:** `@stylobot/core` is already SDK-shaped (zero-dep types + REST client). The gRPC client (`grpc-client.ts`) is in `@stylobot/node` instead of core, which means it can't be used outside Express/Fastify contexts.

## Proto Changes (`sdk/proto/detection.proto`)

Add `RenderWidget` RPC and its messages. Update `go_package` to point to the new Go SDK module.

```proto
service DetectionService {
  rpc Detect (DetectRequest) returns (DetectResponse);
  rpc DetectBatch (DetectBatchRequest) returns (DetectBatchResponse);
  rpc RenderWidget (RenderWidgetRequest) returns (RenderWidgetResponse);
}

message RenderWidgetRequest {
  string template = 1;                // Liquid template source
  DetectResponse verdict = 2;        // optional; injects detection signals into template vars
  map<string, string> vars = 3;      // additional template variables
}

message RenderWidgetResponse {
  string html    = 1;
  bool   success = 2;
  string error   = 3;
}
```

`go_package` changes from `github.com/scottgal/caddy-stylobot/proto;proto` to `github.com/scottgal/stylobot-go/proto;proto`.

The Node SDK copy at `sdk/node/packages/node/proto/detection.proto` moves to `sdk/node/packages/core/proto/detection.proto` when the gRPC client moves to core.

## Go SDK (`sdk/go/`)

New standalone Go module at `github.com/scottgal/stylobot-go`. The module has no Caddy dependency.

**Module layout:**
```
sdk/go/
├── go.mod          # module github.com/scottgal/stylobot-go
├── go.sum
├── client.go       # Client interface + NewClient constructor
├── types.go        # Go-native request/response/options types
├── options.go      # functional options (WithTimeout, WithAPIKey)
└── proto/          # protoc-generated files (moved from sdk/caddy/proto/)
    ├── detection.pb.go
    └── detection_grpc.pb.go
```

**Key types:**

```go
// DetectRequest is the Go-native request type (not proto-generated).
type DetectRequest struct {
    Method   string
    Path     string
    Headers  map[string]string
    RemoteIP string
    Protocol string  // "http" or "https"
    TLS      *TLSInfo
}

type TLSInfo struct {
    Version string
    Cipher  string
    JA3     string
    JA4     string
}

// Verdict is the Go-native response type.
type Verdict struct {
    IsBot             bool
    BotProbability    float32
    Confidence        float32
    BotType           string
    BotName           string
    RiskBand          string
    RecommendedAction string
    ThreatScore       float32
    ThreatBand        string
    ProcessingTimeMs  float32
    DetectorsRun      int32
    Reasons           []Reason
}

type Reason struct {
    Detector string
    Detail   string
    Impact   float32
}

type RenderRequest struct {
    Template string
    Verdict  *Verdict          // optional; nil = no detection context injected
    Vars     map[string]string // additional template variables
}

type RenderResponse struct {
    HTML    string
    Success bool
    Error   string
}

// Client is the interface callers depend on.
type Client interface {
    Detect(ctx context.Context, req DetectRequest) (*Verdict, error)
    DetectBatch(ctx context.Context, reqs []DetectRequest) ([]*Verdict, error)
    RenderWidget(ctx context.Context, req RenderRequest) (*RenderResponse, error)
    Close() error
}
```

**Constructor:**

```go
type Option func(*clientOptions)

func WithTimeout(d time.Duration) Option { ... }
func WithAPIKey(key string) Option       { ... }

func NewClient(endpoint string, opts ...Option) (Client, error) {
    // grpc.NewClient with insecure creds, apply options
    // returns *grpcClient (unexported concrete type)
}
```

The concrete `grpcClient` wraps `pb.DetectionServiceClient` and converts proto types to/from Go-native types via private `toProto` / `fromProto` helpers. Callers only see the `Client` interface.

**Enum conversion:** The three enum maps (`riskBandNames`, `actionNames`, `threatBandNames`) move from `sdk/caddy/stylobot.go` to `sdk/go/types.go`.

## Caddy Plugin Refactor (`sdk/caddy/`)

`sdk/caddy/` becomes a thin Caddy module that imports `github.com/scottgal/stylobot-go`.

**What stays:**
- `stylobot.go` - Caddy module registration, `Provision`, `ServeHTTP` (simplified: creates a `stylobot.Client`, calls `Detect`, reads `Verdict`, injects headers, optionally blocks)
- `headers.go` - `ExtractIP`, `ExtractHeaders` (Caddy-specific header extraction, unchanged)
- `stylobot_test.go` - tests (updated to use `stylobot.Client` interface, not proto types directly)

**What moves:**
- `proto/` directory - moved to `sdk/go/proto/`
- Enum maps (`riskBandNames`, `actionNames`, `threatBandNames`) - moved to `sdk/go/types.go`
- `injectHeaders` helper - stays in `sdk/caddy/stylobot.go` but calls `v.RiskBand` / `v.RecommendedAction` string fields directly (no more map lookups at the call site)

**Resulting `go.mod` deps for `sdk/caddy/`:**
- `github.com/caddyserver/caddy/v2`
- `github.com/scottgal/stylobot-go` (local replace directive during development)
- `go.uber.org/zap` (still used for Caddy logger)
- Drop direct: `google.golang.org/grpc`, `google.golang.org/protobuf` (both become transitive via `stylobot-go`)

**`ServeHTTP` after refactor** (~25 lines vs 30 now, cleaner because no proto type juggling):
```go
func (s *StyloBot) ServeHTTP(w http.ResponseWriter, r *http.Request, next caddyhttp.Handler) error {
    ctx, cancel := context.WithTimeout(r.Context(), s.timeout)
    defer cancel()

    verdict, err := s.sbClient.Detect(ctx, stylobot.DetectRequest{
        Method:   r.Method,
        Path:     r.URL.RequestURI(),
        RemoteIP: ExtractIP(r),
        Protocol: r.Proto,
        Headers:  ExtractHeaders(r),
    })
    if err != nil {
        s.logger.Warn("stylobot detect failed, failing open", zap.Error(err))
        return next.ServeHTTP(w, r)
    }

    injectHeaders(r, verdict)

    if verdict.IsBot && s.OnBlock > 0 && verdict.RecommendedAction == "Block" {
        http.Error(w, "Forbidden", s.OnBlock)
        return nil
    }

    return next.ServeHTTP(w, r)
}
```

## TypeScript SDK Refactor

### `@stylobot/core` additions

The gRPC client moves from `@stylobot/node` to `@stylobot/core`:

**New files in `sdk/node/packages/core/src/`:**
- `grpc-client.ts` - moved from `@stylobot/node/src/grpc-client.ts`; add `grpcRenderWidget` function
- `grpc.ts` - exports `StyloBotGrpcClient` class (wraps the raw functions)

**New file in `sdk/node/packages/core/`:**
- `proto/detection.proto` - moved from `@stylobot/node/proto/detection.proto`

**`StyloBotGrpcClient` class:**
```ts
export class StyloBotGrpcClient {
  private client: grpc.Client;
  private timeoutMs: number;

  constructor(endpoint: string, timeoutMs = 5000) {
    this.client = createGrpcDetectionClient(endpoint);
    this.timeoutMs = timeoutMs;
  }

  detect(req: DetectRequest): Promise<Verdict> {
    return grpcDetect(this.client, req, this.timeoutMs).then(mapGrpcVerdict);
  }

  renderWidget(template: string, verdict?: Verdict, vars?: Record<string, string>): Promise<string> {
    return grpcRenderWidget(this.client, template, verdict, vars, this.timeoutMs);
  }

  close(): void {
    this.client.close();
  }
}
```

**`grpcRenderWidget` function** (added to `grpc-client.ts`):
```ts
export function grpcRenderWidget(
  client: grpc.Client,
  template: string,
  verdict?: Verdict,
  vars?: Record<string, string>,
  timeoutMs = 5000,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const deadline = new Date(Date.now() + timeoutMs);
    (client as any)['renderWidget'](
      { template, verdict, vars: vars ?? {} },
      { deadline },
      (err: grpc.ServiceError | null, response: { html: string; success: boolean; error: string }) => {
        if (err) reject(err);
        else if (!response.success) reject(new Error(response.error));
        else resolve(response.html);
      },
    );
  });
}
```

**`@stylobot/core` `package.json` changes:**
- Add `@grpc/grpc-js` and `@grpc/proto-loader` as optional peer dependencies (so REST-only users pay zero bytes for gRPC)
- `StyloBotGrpcClient` is exported but only instantiable if the peer deps are present (import fails cleanly at runtime if missing)

**`@stylobot/core/src/index.ts` additions:**
```ts
export { StyloBotGrpcClient } from './grpc.js';
export type { GrpcDetectResponse } from './grpc-client.js';
```

### `@stylobot/node` changes

- Delete `src/grpc-client.ts` (moved to core)
- Delete `proto/detection.proto` (moved to core)
- `src/middleware.ts` - replace `import { ... } from './grpc-client.js'` with `import { StyloBotGrpcClient } from '@stylobot/core'`; the `grpc` branch in `styloBotMiddleware` creates a `StyloBotGrpcClient` instance once (module-level), calls `.detect(req)`, writes `req.stylobot`
- `package.json` - remove direct deps on `@grpc/grpc-js` and `@grpc/proto-loader` (they become transitive via core's optional peer deps)

## Data Flow: RenderWidget

1. Client sends a Liquid template string (and optionally the current request's detection verdict) via gRPC `RenderWidget`
2. Sidecar (`Mostlylucid.BotDetection.Sidecar`) receives the call, runs the Liquid engine with the verdict fields as template variables, returns rendered HTML
3. Client inserts the HTML into the response stream

Template variables injected from verdict: `{{ isBot }}`, `{{ botProbability }}`, `{{ riskBand }}`, `{{ recommendedAction }}`, `{{ botType }}`, `{{ botName }}`, `{{ threatBand }}`, plus any caller-supplied `vars`.

## Testing

**Go SDK:** unit tests in `sdk/go/` with a mock gRPC server (using `google.golang.org/grpc/test/bufconn`). Existing Caddy plugin tests update to use the real `stylobot.Client` via `bufconn`.

**TypeScript:** existing `@stylobot/core` tests extend to cover `StyloBotGrpcClient`. The `grpc-client.ts` tests move from `@stylobot/node/__tests__/` to `@stylobot/core/__tests__/`.

**Integration:** existing `tests/integration/caddy-sidecar/` and `tests/integration/node-sidecar/` continue to pass unchanged (interface is identical; only internal wiring changes). Add a render-widget smoke test to the node-sidecar integration app.

## File Changes Summary

| Action | Path |
|--------|------|
| Create | `sdk/go/go.mod`, `sdk/go/client.go`, `sdk/go/types.go`, `sdk/go/options.go` |
| Move | `sdk/caddy/proto/` → `sdk/go/proto/` |
| Modify | `sdk/caddy/stylobot.go` (import Go SDK, remove proto/grpc direct deps) |
| Modify | `sdk/caddy/go.mod` (add `github.com/scottgal/stylobot-go` dep) |
| Move | `sdk/node/packages/node/src/grpc-client.ts` → `sdk/node/packages/core/src/grpc-client.ts` |
| Move | `sdk/node/packages/node/proto/detection.proto` → `sdk/node/packages/core/proto/detection.proto` |
| Create | `sdk/node/packages/core/src/grpc.ts` (`StyloBotGrpcClient` class) |
| Modify | `sdk/node/packages/core/src/index.ts` (export `StyloBotGrpcClient`) |
| Modify | `sdk/node/packages/core/package.json` (add optional gRPC peer deps) |
| Modify | `sdk/node/packages/node/src/middleware.ts` (import from core) |
| Modify | `sdk/proto/detection.proto` (add `RenderWidget`, update `go_package`) |
| Implement | `RenderWidget` handler in `Mostlylucid.BotDetection.Sidecar` |
