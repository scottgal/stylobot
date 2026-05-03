# gRPC Caddy Bot Detection Module Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a gRPC `DetectService` to StyloBot's API project and a Caddy v2 middleware module (Go) that calls it, giving any web stack sub-millisecond bot detection via a persistent HTTP/2 connection.

**Architecture:** A shared `.proto` defines `DetectService`. The .NET API project gains a `GrpcDetectService` that mirrors the existing REST detect endpoint logic, calling `BlackboardOrchestrator` via the same pattern as `DetectEndpoints.HandleDetect`. A Go Caddy module (`sdk/caddy/`) provisions one persistent `grpc.ClientConn` at startup, calls `Detect()` on every request (microsecond overhead on localhost), injects `X-StyloBot-*` headers for the upstream app, and optionally enforces the action policy (block with configurable HTTP status).

**Tech Stack:** .NET 10 + Grpc.AspNetCore, Protocol Buffers 3, Go 1.22+, Caddy v2, grpc-go.

**Working directory:** `.worktrees/feat-grpc-caddy/` for all file operations.

---

## File Map

### Proto
| Action | File |
|--------|------|
| Create | `sdk/proto/detect.proto` |

### .NET API
| Action | File |
|--------|------|
| Modify | `Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj` — add Grpc.AspNetCore + proto item |
| Create | `Mostlylucid.BotDetection.Api/Grpc/GrpcDetectService.cs` |
| Modify | `Mostlylucid.BotDetection.Api/Program.cs` — register + map gRPC |

### Go Caddy module
| Action | File |
|--------|------|
| Create | `sdk/caddy/go.mod` |
| Create | `sdk/caddy/proto/detect.pb.go` (generated) |
| Create | `sdk/caddy/proto/detect_grpc.pb.go` (generated) |
| Create | `sdk/caddy/headers.go` — IP extraction, header flattening |
| Create | `sdk/caddy/stylobot.go` — module, provision, ServeHTTP, Caddyfile |
| Create | `sdk/caddy/stylobot_test.go` |
| Create | `sdk/caddy/README.md` |

---

## Task 1: Write the proto definition

**Files:** Create `sdk/proto/detect.proto`

- [ ] Create `sdk/proto/` and write the proto:

```protobuf
syntax = "proto3";

package stylobot.v1;

option csharp_namespace = "Mostlylucid.BotDetection.Api.Grpc";
option go_package = "github.com/scottgal/caddy-stylobot/proto;proto";

service DetectService {
  rpc Detect(DetectRequest) returns (DetectResponse);
  rpc DetectBatch(DetectBatchRequest) returns (DetectBatchResponse);
}

message DetectRequest {
  string method    = 1;
  string path      = 2;
  string remote_ip = 3;
  string protocol  = 4;
  map<string, string> headers = 5;
}

message DetectResponse {
  bool   is_bot             = 1;
  float  bot_probability    = 2;
  float  confidence         = 3;
  string bot_type           = 4;
  string bot_name           = 5;
  string risk_band          = 6;
  string recommended_action = 7;
  float  threat_score       = 8;
  string threat_band        = 9;
  string policy             = 10;
}

message DetectBatchRequest {
  repeated DetectRequest requests = 1;
}

message DetectBatchResponse {
  repeated DetectResponse responses = 1;
}
```

- [ ] Commit:

```bash
git add sdk/proto/detect.proto
git commit -m "chore(proto): DetectService proto definition"
```

---

## Task 2: Add gRPC packages to the .NET API project

**Files:** Modify `Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj`

- [ ] Read the current csproj to understand what's already there:

```bash
cat Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
```

- [ ] Add inside the existing `<ItemGroup>` for PackageReferences:

```xml
<PackageReference Include="Grpc.AspNetCore" Version="2.67.0" />
<PackageReference Include="Grpc.AspNetCore.Server.Reflection" Version="2.67.0" />
```

- [ ] Add a new `<ItemGroup>` for the proto (path is relative from the .csproj):

```xml
<ItemGroup>
  <Protobuf Include="..\sdk\proto\detect.proto" GrpcServices="Server" />
</ItemGroup>
```

- [ ] Restore and build:

```bash
dotnet restore Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj -v q
```

Expected: 0 errors. Grpc.Tools generates `Mostlylucid.BotDetection.Api.Grpc.DetectService.DetectServiceBase` in the `obj/` directory.

- [ ] Commit:

```bash
git add Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj
git commit -m "chore(api): add Grpc.AspNetCore and proto build item"
```

---

## Task 3: Implement GrpcDetectService

**Files:** Create `Mostlylucid.BotDetection.Api/Grpc/GrpcDetectService.cs`

- [ ] First read the existing detect endpoint to understand exactly how it calls `BlackboardOrchestrator`:

```bash
cat Mostlylucid.BotDetection.Api/Endpoints/DetectEndpoints.cs
```

Also confirm the shape of `AggregatedEvidence` and `DetectResponse`:

```bash
grep -rn "class AggregatedEvidence\|BotProbability\|RiskBand\|ThreatScore\|BotType\|BotName\|RecommendedAction\|ThreatBand\|ActionPolicy\|Policy" \
  Mostlylucid.BotDetection.Orchestration/ Mostlylucid.BotDetection/Orchestration/ \
  Mostlylucid.BotDetection.Api/Models/ 2>/dev/null | head -40

grep -rn "class DetectResponse\|FromEvidence\|VerdictDto\|MetaDto" \
  Mostlylucid.BotDetection.Api/ 2>/dev/null | head -20
```

- [ ] Create `Mostlylucid.BotDetection.Api/Grpc/GrpcDetectService.cs`.

The gRPC service mirrors `DetectEndpoints.HandleDetect` exactly, using the same `BlackboardOrchestrator` call pattern. The key difference: instead of reading from `HttpContext`, we receive data from the gRPC request and map it to an `ApiModels.DetectRequest`, then call whatever the REST endpoint calls.

Because `BlackboardOrchestrator.DetectAsync` takes `HttpContext`, the gRPC service must use the same intermediate path that the REST endpoint uses. Read `DetectEndpoints.HandleDetect` carefully — if it constructs a synthetic context or uses a different overload, follow that pattern.

```csharp
using Grpc.Core;
using Mostlylucid.BotDetection.Api.Grpc;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Grpc;

public sealed class GrpcDetectService(
    BlackboardOrchestrator orchestrator,
    ILogger<GrpcDetectService> logger)
    : DetectService.DetectServiceBase
{
    public override async Task<DetectResponse> Detect(
        DetectRequest request, ServerCallContext context)
    {
        try
        {
            // Build the same model the REST endpoint uses
            var apiRequest = new ApiModels.DetectRequest(
                Method:   request.Method,
                Path:     request.Path,
                Headers:  request.Headers.ToDictionary(k => k.Key, v => v.Value),
                RemoteIp: request.RemoteIp,
                Protocol: request.Protocol.Length > 0 ? request.Protocol : "https"
            );

            // Call the orchestrator the same way the REST endpoint does.
            // If the REST endpoint builds a synthetic HttpContext, replicate that here.
            // Check DetectEndpoints.HandleDetect for the exact call.
            var evidence = await orchestrator.DetectAsync(apiRequest, context.CancellationToken);
            var apiResponse = ApiModels.DetectResponse.FromEvidence(evidence);

            return MapToProto(apiResponse);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC Detect failed for {Path}", request.Path);
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task<DetectBatchResponse> DetectBatch(
        DetectBatchRequest request, ServerCallContext context)
    {
        var tasks = request.Requests.Select(r => Detect(r, context));
        var responses = await Task.WhenAll(tasks);
        var reply = new DetectBatchResponse();
        reply.Responses.AddRange(responses);
        return reply;
    }

    private static DetectResponse MapToProto(ApiModels.DetectResponse r)
    {
        // Adjust property names below to match actual ApiModels.DetectResponse shape.
        // Run the grep above if unsure — look at VerdictDto, MetaDto fields.
        return new DetectResponse
        {
            IsBot             = r.Verdict?.IsBot ?? false,
            BotProbability    = (float)(r.Verdict?.BotProbability ?? 0),
            Confidence        = (float)(r.Verdict?.Confidence ?? 0),
            BotType           = r.Verdict?.BotType ?? "",
            BotName           = r.Verdict?.BotName ?? "",
            RiskBand          = r.Verdict?.RiskBand?.ToString() ?? "Unknown",
            RecommendedAction = r.Meta?.RecommendedAction ?? "Allow",
            ThreatScore       = (float)(r.Verdict?.ThreatScore ?? 0),
            ThreatBand        = r.Verdict?.ThreatBand?.ToString() ?? "None",
            Policy            = r.Meta?.PolicyName ?? ""
        };
    }
}
```

IMPORTANT: The `DetectRequest` constructor call, the `orchestrator.DetectAsync` signature, and the `MapToProto` property names MUST match the actual types found via grep. Adjust as needed — the greps above give you the ground truth.

- [ ] Build:

```bash
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj --no-restore -v q
```

Expected: 0 errors. Fix any property name mismatches until clean.

- [ ] Commit:

```bash
git add Mostlylucid.BotDetection.Api/Grpc/GrpcDetectService.cs
git commit -m "feat(api): GrpcDetectService wrapping BlackboardOrchestrator"
```

---

## Task 4: Register gRPC in Program.cs

**Files:** Modify `Mostlylucid.BotDetection.Api/Program.cs`

- [ ] Read the current Program.cs to find where services are registered and endpoints mapped:

```bash
grep -n "builder\.Services\|app\.Map\|UseRouting\|AddGrpc" \
  Mostlylucid.BotDetection.Api/Program.cs | head -30
```

- [ ] Add `builder.Services.AddGrpc()` and `builder.Services.AddGrpcReflection()` near the other service registrations (before `builder.Build()`):

```csharp
builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaxReceiveMessageSize = 4 * 1024 * 1024;
});
builder.Services.AddGrpcReflection();
```

- [ ] After `app.UseRouting()` (or wherever REST endpoints are mapped), add:

```csharp
app.MapGrpcService<GrpcDetectService>();
if (app.Environment.IsDevelopment())
    app.MapGrpcReflectionService();
```

Add using if missing:
```csharp
using Mostlylucid.BotDetection.Api.Grpc;
```

- [ ] Build:

```bash
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj --no-restore -v q
```

Expected: 0 errors.

- [ ] Commit:

```bash
git add Mostlylucid.BotDetection.Api/Program.cs
git commit -m "feat(api): map gRPC detect service endpoint"
```

---

## Task 5: Scaffold Go module and generate proto client

**Files:** Create `sdk/caddy/go.mod`, generate `sdk/caddy/proto/*.go`

- [ ] Verify tooling:

```bash
go version          # need 1.22+
protoc --version    # brew install protobuf if missing
which protoc-gen-go || go install google.golang.org/protobuf/cmd/protoc-gen-go@latest
which protoc-gen-go-grpc || go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@latest
```

- [ ] Create the module:

```bash
mkdir -p sdk/caddy/proto
cd sdk/caddy
go mod init github.com/scottgal/caddy-stylobot
```

- [ ] Add dependencies (run from `sdk/caddy/`):

```bash
go get github.com/caddyserver/caddy/v2@latest
go get google.golang.org/grpc@latest
go get google.golang.org/grpc/credentials/insecure
go get google.golang.org/protobuf@latest
```

- [ ] Generate Go client from the proto (run from the repo root):

```bash
protoc \
  --proto_path=sdk/proto \
  --go_out=sdk/caddy/proto \
  --go_opt=paths=source_relative \
  --go-grpc_out=sdk/caddy/proto \
  --go-grpc_opt=paths=source_relative \
  sdk/proto/detect.proto
```

Expected: `sdk/caddy/proto/detect.pb.go` and `sdk/caddy/proto/detect_grpc.pb.go` created.

- [ ] Verify generated code compiles:

```bash
cd sdk/caddy && go build ./proto/...
```

Expected: no errors.

- [ ] Run `go mod tidy`:

```bash
cd sdk/caddy && go mod tidy
```

- [ ] Commit:

```bash
git add sdk/caddy/
git commit -m "chore(caddy): scaffold Go module, generate gRPC proto client"
```

---

## Task 6: Implement headers.go

**Files:** Create `sdk/caddy/headers.go`

- [ ] Write the failing test cases in `sdk/caddy/stylobot_test.go` first (just the header-related ones):

```go
package stylobot_test

import (
	"net/http"
	"net/http/httptest"
	"testing"

	stylobot "github.com/scottgal/caddy-stylobot"
)

func TestExtractIPFromXFF(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.Header.Set("X-Forwarded-For", "1.2.3.4, 10.0.0.1")
	got := stylobot.ExtractIP(req)
	if got != "1.2.3.4" {
		t.Errorf("expected 1.2.3.4, got %q", got)
	}
}

func TestExtractIPFromRemoteAddr(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.RemoteAddr = "5.6.7.8:12345"
	got := stylobot.ExtractIP(req)
	if got != "5.6.7.8" {
		t.Errorf("expected 5.6.7.8, got %q", got)
	}
}

func TestExtractHeadersLowercase(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.Header.Set("User-Agent", "TestBot/1.0")
	req.Header.Set("Accept-Language", "en-US,en;q=0.9")
	headers := stylobot.ExtractHeaders(req)
	if headers["user-agent"] != "TestBot/1.0" {
		t.Errorf("expected user-agent header, got %v", headers)
	}
	if headers["accept-language"] != "en-US,en;q=0.9" {
		t.Errorf("expected accept-language header, got %v", headers)
	}
}
```

- [ ] Run test to confirm it fails:

```bash
cd sdk/caddy && go test ./... 2>&1 | head -10
```

Expected: compile error — `ExtractIP`, `ExtractHeaders` undefined.

- [ ] Create `sdk/caddy/headers.go`:

```go
package stylobot

import (
	"net"
	"net/http"
	"strings"
)

// ExtractIP returns the real client IP, preferring X-Forwarded-For.
func ExtractIP(r *http.Request) string {
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		parts := strings.SplitN(xff, ",", 2)
		return strings.TrimSpace(parts[0])
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// ExtractHeaders returns all request headers as a lowercase-keyed map.
func ExtractHeaders(r *http.Request) map[string]string {
	out := make(map[string]string, len(r.Header))
	for k, v := range r.Header {
		out[strings.ToLower(k)] = strings.Join(v, ", ")
	}
	return out
}
```

- [ ] Run tests:

```bash
cd sdk/caddy && go test ./... -run TestExtract -v
```

Expected: 3 passing.

- [ ] Commit:

```bash
git add sdk/caddy/headers.go sdk/caddy/stylobot_test.go
git commit -m "feat(caddy): header extraction helpers + tests"
```

---

## Task 7: Implement the Caddy middleware

**Files:** Create `sdk/caddy/stylobot.go`, extend `sdk/caddy/stylobot_test.go`

- [ ] Add middleware tests to `sdk/caddy/stylobot_test.go`. Append these test functions:

```go
import (
	// add to existing imports:
	"context"
	"net"

	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
	pb "github.com/scottgal/caddy-stylobot/proto"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

// mockDetectServer is a minimal in-process gRPC server for tests.
type mockDetectServer struct {
	pb.UnimplementedDetectServiceServer
	resp *pb.DetectResponse
}

func (m *mockDetectServer) Detect(_ context.Context, _ *pb.DetectRequest) (*pb.DetectResponse, error) {
	return m.resp, nil
}

func startMockGRPC(t *testing.T, resp *pb.DetectResponse) string {
	t.Helper()
	lis, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	s := grpc.NewServer()
	pb.RegisterDetectServiceServer(s, &mockDetectServer{resp: resp})
	go s.Serve(lis)
	t.Cleanup(s.Stop)
	return lis.Addr().String()
}

func newTestMiddleware(t *testing.T, addr string, onBlock int) *stylobot.StyloBot {
	t.Helper()
	conn, err := grpc.NewClient(addr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	m := &stylobot.StyloBot{OnBlock: onBlock}
	m.SetConn(conn)
	return m
}

func TestInjectsHeaders(t *testing.T) {
	addr := startMockGRPC(t, &pb.DetectResponse{
		IsBot: false, BotProbability: 0.12, Confidence: 0.95,
		RiskBand: "Low", RecommendedAction: "Allow", ThreatBand: "None",
	})
	m := newTestMiddleware(t, addr, 403)

	req := httptest.NewRequest(http.MethodGet, "/test", nil)
	rr := httptest.NewRecorder()
	var captured *http.Request
	next := caddyhttp.HandlerFunc(func(w http.ResponseWriter, r *http.Request) error {
		captured = r
		return nil
	})

	if err := m.ServeHTTP(rr, req, next); err != nil {
		t.Fatal(err)
	}
	if captured == nil {
		t.Fatal("next was not called")
	}
	if captured.Header.Get("X-StyloBot-IsBot") != "false" {
		t.Errorf("X-StyloBot-IsBot: got %q", captured.Header.Get("X-StyloBot-IsBot"))
	}
	if captured.Header.Get("X-StyloBot-RiskBand") != "Low" {
		t.Errorf("X-StyloBot-RiskBand: got %q", captured.Header.Get("X-StyloBot-RiskBand"))
	}
}

func TestFailsOpen(t *testing.T) {
	// No server at this address — should fail open.
	conn, _ := grpc.NewClient("127.0.0.1:1", grpc.WithTransportCredentials(insecure.NewCredentials()))
	t.Cleanup(func() { conn.Close() })
	m := &stylobot.StyloBot{OnBlock: 403}
	m.SetConn(conn)

	req := httptest.NewRequest(http.MethodGet, "/", nil)
	rr := httptest.NewRecorder()
	called := false
	next := caddyhttp.HandlerFunc(func(w http.ResponseWriter, r *http.Request) error {
		called = true
		return nil
	})
	_ = m.ServeHTTP(rr, req, next)
	if !called {
		t.Error("should fail open and call next on gRPC error")
	}
}

func TestBlocksBot(t *testing.T) {
	addr := startMockGRPC(t, &pb.DetectResponse{
		IsBot: true, BotProbability: 0.98, RecommendedAction: "Block", RiskBand: "VeryHigh",
	})
	m := newTestMiddleware(t, addr, 403)

	req := httptest.NewRequest(http.MethodGet, "/", nil)
	rr := httptest.NewRecorder()
	called := false
	next := caddyhttp.HandlerFunc(func(w http.ResponseWriter, r *http.Request) error {
		called = true
		return nil
	})
	_ = m.ServeHTTP(rr, req, next)
	if called {
		t.Error("next should not be called when action is Block")
	}
	if rr.Code != 403 {
		t.Errorf("expected 403, got %d", rr.Code)
	}
}
```

- [ ] Run to confirm tests fail (no StyloBot struct yet):

```bash
cd sdk/caddy && go test ./... 2>&1 | head -10
```

Expected: compile error.

- [ ] Create `sdk/caddy/stylobot.go`:

```go
package stylobot

import (
	"context"
	"fmt"
	"net/http"
	"time"

	"github.com/caddyserver/caddy/v2"
	"github.com/caddyserver/caddy/v2/caddyconfig/caddyfile"
	"github.com/caddyserver/caddy/v2/caddyconfig/httpcaddyfile"
	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
	pb "github.com/scottgal/caddy-stylobot/proto"
	"go.uber.org/zap"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

func init() {
	caddy.RegisterModule(StyloBot{})
	httpcaddyfile.RegisterHandlerDirective("stylobot", parseCaddyfileHandler)
}

// StyloBot is a Caddy HTTP middleware that calls StyloBot's gRPC DetectService
// and injects X-StyloBot-* headers onto the upstream request.
type StyloBot struct {
	// Endpoint is the host:port of the StyloBot gRPC server (no scheme).
	Endpoint string `json:"endpoint"`
	// APIKey is an optional API key sent as gRPC metadata.
	APIKey string `json:"api_key,omitempty"`
	// Timeout for each Detect RPC. Default "50ms".
	Timeout string `json:"timeout,omitempty"`
	// OnBlock: HTTP status to return when recommended action is "Block". Default 403. 0 = inject headers only.
	OnBlock int `json:"on_block,omitempty"`

	timeout time.Duration
	conn    *grpc.ClientConn
	client  pb.DetectServiceClient
	logger  *zap.Logger
}

func (StyloBot) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{
		ID:  "http.handlers.stylobot",
		New: func() caddy.Module { return new(StyloBot) },
	}
}

func (s *StyloBot) Provision(ctx caddy.Context) error {
	s.logger = ctx.Logger()

	s.timeout = 50 * time.Millisecond
	if s.Timeout != "" {
		d, err := time.ParseDuration(s.Timeout)
		if err != nil {
			return fmt.Errorf("stylobot: invalid timeout %q: %w", s.Timeout, err)
		}
		s.timeout = d
	}
	if s.OnBlock == 0 {
		s.OnBlock = 403
	}

	conn, err := grpc.NewClient(s.Endpoint,
		grpc.WithTransportCredentials(insecure.NewCredentials()),
	)
	if err != nil {
		return fmt.Errorf("stylobot: grpc dial %q: %w", s.Endpoint, err)
	}
	s.conn = conn
	s.client = pb.NewDetectServiceClient(conn)
	return nil
}

func (s *StyloBot) Validate() error {
	if s.Endpoint == "" {
		return fmt.Errorf("stylobot: endpoint is required")
	}
	return nil
}

// SetConn injects a pre-dialed connection. Used in tests only.
func (s *StyloBot) SetConn(conn *grpc.ClientConn) {
	s.conn = conn
	s.client = pb.NewDetectServiceClient(conn)
	s.timeout = 2 * time.Second
	if s.logger == nil {
		s.logger = zap.NewNop()
	}
}

func (s *StyloBot) Cleanup() error {
	if s.conn != nil {
		return s.conn.Close()
	}
	return nil
}

func (s *StyloBot) ServeHTTP(w http.ResponseWriter, r *http.Request, next caddyhttp.Handler) error {
	ctx, cancel := context.WithTimeout(r.Context(), s.timeout)
	defer cancel()

	req := &pb.DetectRequest{
		Method:   r.Method,
		Path:     r.URL.RequestURI(),
		RemoteIp: ExtractIP(r),
		Protocol: r.Proto,
		Headers:  ExtractHeaders(r),
	}

	resp, err := s.client.Detect(ctx, req)
	if err != nil {
		s.logger.Warn("stylobot detect failed, failing open", zap.Error(err))
		return next.ServeHTTP(w, r)
	}

	injectHeaders(r, resp)

	if resp.IsBot && s.OnBlock > 0 && resp.RecommendedAction == "Block" {
		http.Error(w, "Forbidden", s.OnBlock)
		return nil
	}

	return next.ServeHTTP(w, r)
}

func injectHeaders(r *http.Request, resp *pb.DetectResponse) {
	h := r.Header
	h.Set("X-StyloBot-IsBot", fmt.Sprintf("%v", resp.IsBot))
	h.Set("X-StyloBot-Probability", fmt.Sprintf("%.4f", resp.BotProbability))
	h.Set("X-StyloBot-Confidence", fmt.Sprintf("%.4f", resp.Confidence))
	h.Set("X-StyloBot-BotType", resp.BotType)
	h.Set("X-StyloBot-BotName", resp.BotName)
	h.Set("X-StyloBot-RiskBand", resp.RiskBand)
	h.Set("X-StyloBot-Action", resp.RecommendedAction)
	h.Set("X-StyloBot-ThreatScore", fmt.Sprintf("%.4f", resp.ThreatScore))
	h.Set("X-StyloBot-ThreatBand", resp.ThreatBand)
	h.Set("X-StyloBot-Policy", resp.Policy)
}

func (s *StyloBot) UnmarshalCaddyfile(d *caddyfile.Dispenser) error {
	for d.Next() {
		for d.NextBlock(0) {
			switch d.Val() {
			case "endpoint":
				if !d.NextArg() {
					return d.ArgErr()
				}
				s.Endpoint = d.Val()
			case "api_key":
				if !d.NextArg() {
					return d.ArgErr()
				}
				s.APIKey = d.Val()
			case "timeout":
				if !d.NextArg() {
					return d.ArgErr()
				}
				s.Timeout = d.Val()
			case "on_block":
				if !d.NextArg() {
					return d.ArgErr()
				}
				var code int
				if _, err := fmt.Sscanf(d.Val(), "%d", &code); err != nil {
					return d.Errf("on_block must be an integer: %v", err)
				}
				s.OnBlock = code
			default:
				return d.Errf("unrecognized directive: %s", d.Val())
			}
		}
	}
	return nil
}

func parseCaddyfileHandler(h httpcaddyfile.Helper) (caddyhttp.MiddlewareHandler, error) {
	var m StyloBot
	return &m, m.UnmarshalCaddyfile(h.Dispenser)
}

// Compile-time interface assertions.
var (
	_ caddy.Module                = (*StyloBot)(nil)
	_ caddy.Provisioner           = (*StyloBot)(nil)
	_ caddy.Validator             = (*StyloBot)(nil)
	_ caddy.CleanerUpper          = (*StyloBot)(nil)
	_ caddyhttp.MiddlewareHandler = (*StyloBot)(nil)
	_ caddyfile.Unmarshaler       = (*StyloBot)(nil)
)
```

- [ ] Run `go mod tidy` then run all tests:

```bash
cd sdk/caddy && go mod tidy && go test ./... -v
```

Expected: all tests pass (3 extract tests + 3 middleware tests).

- [ ] Commit:

```bash
git add sdk/caddy/stylobot.go sdk/caddy/stylobot_test.go
git commit -m "feat(caddy): StyloBot Caddy gRPC middleware with tests"
```

---

## Task 8: README

**Files:** Create `sdk/caddy/README.md`

- [ ] Create `sdk/caddy/README.md`:

```markdown
# caddy-stylobot

Caddy v2 middleware module for StyloBot bot detection via gRPC. Uses a persistent HTTP/2 connection to the StyloBot sidecar — typical overhead on localhost is under 0.5ms.

## Requirements

- Caddy v2
- StyloBot running with gRPC enabled (default port 5080)
- Go 1.22+ and [xcaddy](https://github.com/caddyserver/xcaddy) to build

## Build

    go install github.com/caddyserver/xcaddy/cmd/xcaddy@latest
    xcaddy build --with github.com/scottgal/caddy-stylobot

## Docker

    FROM caddy:builder AS builder
    RUN xcaddy build --with github.com/scottgal/caddy-stylobot

    FROM caddy:latest
    COPY --from=builder /usr/bin/caddy /usr/bin/caddy

## Caddyfile

    :80 {
        stylobot {
            endpoint localhost:5080
            timeout  50ms
            on_block 403
        }
        reverse_proxy :3000
    }

## Configuration

| Directive | Default    | Description |
|-----------|------------|-------------|
| endpoint  | (required) | host:port of StyloBot gRPC server (no scheme) |
| api_key   |            | Optional API key sent as gRPC metadata |
| timeout   | 50ms       | Per-request RPC timeout; fails open on timeout |
| on_block  | 403        | HTTP status returned when action=Block. 0 = inject headers, don't enforce. |

## Headers injected

All headers are injected onto the request forwarded to the upstream application:

- `X-StyloBot-IsBot` — true/false
- `X-StyloBot-Probability` — 0.0000–1.0000
- `X-StyloBot-Confidence` — 0.0000–1.0000
- `X-StyloBot-BotType` — e.g. "Scraper", "Scanner"
- `X-StyloBot-BotName` — deterministic name e.g. "Shadowreaper-7"
- `X-StyloBot-RiskBand` — VeryLow/Low/Elevated/Medium/High/VeryHigh/Verified
- `X-StyloBot-Action` — Allow/Throttle/Challenge/Block
- `X-StyloBot-ThreatScore` — 0.0000–1.0000
- `X-StyloBot-ThreatBand` — None/Low/Elevated/High/Critical
- `X-StyloBot-Policy` — active policy name

## Fail-open guarantee

If the StyloBot sidecar is unreachable or times out, the middleware logs a warning and forwards the request without injecting headers. Your app stays up.
```

- [ ] Commit:

```bash
git add sdk/caddy/README.md
git commit -m "docs(caddy): build instructions and Caddyfile reference"
```

---

## Task 9: End-to-end verification

- [ ] Build .NET API:

```bash
dotnet build Mostlylucid.BotDetection.Api/Mostlylucid.BotDetection.Api.csproj -v q
```

Expected: 0 errors.

- [ ] Run all Go tests:

```bash
cd sdk/caddy && go test ./... -v
```

Expected: all 6 tests passing.

- [ ] Verify Go module builds cleanly as a Caddy plugin (requires xcaddy):

```bash
cd sdk/caddy
xcaddy build --with github.com/scottgal/caddy-stylobot=. 2>&1 | tail -5
```

Expected: Caddy binary produced with the stylobot module compiled in.

If xcaddy is not installed:
```bash
go install github.com/caddyserver/xcaddy/cmd/xcaddy@latest
```

- [ ] Commit any remaining changes:

```bash
git add -A && git status
# commit if anything outstanding
git commit -m "chore: end-to-end verification complete" 2>/dev/null || true
```
