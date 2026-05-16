# Sidecar Deployment

The sidecar is a lightweight detection API you run alongside your application. Your app calls it per-request. No reverse proxy, no header injection - your code decides what to do with the verdict.

**Use sidecar when:**
- Your backend is Python, Go, Java, Ruby, PHP, or any non-.NET runtime
- You can't put a proxy in front of your app (serverless, Lambda, managed PaaS)
- You want programmatic control over which requests get checked
- Your app already has complex routing that a transparent proxy would complicate

**Use the [YARP gateway](yarp-gateway.md) instead when:**
- You have multiple backends to protect from one place
- You want zero code changes in your app
- Protecting static file servers, legacy apps, or third-party services

---

## How it works

```
Request → Your App → calls POST /detect (REST) or gRPC Detect()
                              ↓
                     stylobot-sidecar
                     (all 49 detectors, SQLite state)
                              ↓
                     Returns verdict
                              ↓
              Your App: block / throttle / allow
```

The sidecar exposes two interfaces on the same port:
- **gRPC** (`application/grpc`, HTTP/2) - primary interface; binary protobuf, lowest overhead
- **REST** (`POST /api/v1/detect`, HTTP/1.1 or HTTP/2) - for Node.js SDK and curl

---

## Network exposure and authentication

The sidecar binds **loopback only by default**. That matches the trust model: a
sidecar shares the loopback interface with the app it serves (same Kubernetes
Pod, or the same container). Nothing off-host can reach it.

To reach the sidecar across a network - a separate Docker Compose service, a
gateway on another host - you must opt in **and** authenticate:

- `STYLOBOT_BIND=any` makes the listener bind all interfaces.
- Configure at least one API key. The gRPC surface (`Detect`, `DetectBatch`,
  `RenderWidget`) drives the full detection pipeline and mutates shared
  reputation state, so it is **not reachable unauthenticated** once keys exist.

The sidecar **refuses to start** if it is bound to all interfaces with no API
keys configured. Set `STYLOBOT_ALLOW_INSECURE=true` to override that (it then
runs exposed and unauthenticated - not recommended).

### Configuring API keys

Each entry in the `BotDetection:ApiKeys` map has a stable id (the map key) and a
secret value. Give the secret explicitly with `Key` so it can be sourced from a
secret store while the id stays non-sensitive. As environment variables (double
underscore nests):

```
BotDetection__ApiKeys__app__Key=<your-secret-key>
BotDetection__ApiKeys__app__Name=app
```

Here `app` is the id and `<your-secret-key>` is what callers present. If you omit
`Key`, the id itself is used as the secret. gRPC callers send the secret as the
`x-sb-api-key` metadata entry; REST callers send the `X-SB-Api-Key` HTTP header.

## Quick start

Same-host only (loopback - the secure default):

```bash
docker run --rm --network host \
  -v stylobot-data:/data \
  scottgal/stylobot-sidecar:latest
```

Reachable from other containers/hosts (published port needs `STYLOBOT_BIND=any`
plus a key - a loopback-bound listener is not reachable via `-p`):

```bash
docker run --rm -p 5090:5090 -p 5091:5091 \
  -v stylobot-data:/data \
  -e STYLOBOT_BIND=any \
  -e BotDetection__ApiKeys__app__Key=changeme \
  -e BotDetection__ApiKeys__app__Name=app \
  scottgal/stylobot-sidecar:latest
```

Health check: `GET http://localhost:5091/health` (REST port, HTTP/1.1) or
`http://localhost:5090/health` over HTTP/2.

---

## Docker Compose

See [`scripts/docker-compose.sidecar.yml`](../../scripts/docker-compose.sidecar.yml) for a complete example with health checks and volume mounts.

```bash
STYLOBOT_API_KEY=changeme docker compose -f scripts/docker-compose.sidecar.yml up
```

Compose runs the sidecar as its own service on the Compose network, so that
example sets `STYLOBOT_BIND=any` and an API key - the secure cross-network combination.

---

## Single container (sidecar + app together)

The sidecar publishes as a self-contained single-file binary, so you can run it
inside your application's own container instead of as a separate one. The two
processes then share the container's loopback interface, so the sidecar keeps
its secure loopback-only default and needs no API key.

Lift the binary straight out of the published image:

```dockerfile
FROM your-app-base:latest

# Drop in the StyloBot sidecar binary - no .NET runtime needed, it is self-contained.
COPY --from=scottgal/stylobot-sidecar:latest /usr/local/bin/stylobot-sidecar /usr/local/bin/

# Start both: the sidecar on loopback, then your app. Use a process supervisor
# (s6-overlay, supervisord, a shell wrapper) so both run and the container exits
# if either dies. Your app calls the sidecar at 127.0.0.1:5090.
```

Your app talks to `127.0.0.1:5090` (gRPC) or `127.0.0.1:5091` (REST). Because
both processes are in one container, the call never leaves the loopback
interface - the same trust boundary as a Kubernetes sidecar, in a single image.

The release also ships the same binary as `stylobot-sidecar-<platform>` archives
on the [GitHub Releases](https://github.com/scottgal/stylobot/releases) page if
you would rather not pull it from the image.

---

## gRPC interface

The proto file is at [`sdk/proto/detection.proto`](../../sdk/proto/detection.proto). Generate stubs for your language with `protoc` or `buf`.

When API keys are configured, every gRPC call must carry the key as the
`x-sb-api-key` metadata entry, or it is rejected with `UNAUTHENTICATED`:

- **Python:** `stub.Detect(req, metadata=[("x-sb-api-key", "your-key")])`
- **Go:** `metadata.AppendToOutgoingContext(ctx, "x-sb-api-key", "your-key")`, or use the Go SDK's `WithAPIKey(...)` option.

`insecure_channel` / `insecure.NewCredentials()` below refers to *transport TLS*
only - it is fine on loopback or a trusted internal network. It does not affect
API-key authentication, which is independent.

### Python

```bash
pip install grpcio grpcio-tools
python -m grpc_tools.protoc -I sdk/proto \
  --python_out=. --grpc_python_out=. sdk/proto/detection.proto
```

```python
import grpc
import detection_pb2 as pb
import detection_pb2_grpc as pb_grpc

channel = grpc.insecure_channel("stylobot-sidecar:5090")
stub = pb_grpc.DetectionServiceStub(channel)

def check_request(request):
    resp = stub.Detect(pb.DetectRequest(
        method=request.method,
        path=request.path,
        headers=dict(request.headers),
        remote_ip=request.remote_addr,
        protocol="https",
    ))
    return resp

# Django middleware example
class BotDetectionMiddleware:
    def __init__(self, get_response):
        self.get_response = get_response

    def __call__(self, request):
        verdict = check_request(request)
        if verdict.recommended_action == pb.RecommendedAction.Value("RECOMMENDED_ACTION_BLOCK"):
            from django.http import HttpResponseForbidden
            return HttpResponseForbidden()
        request.stylobot = verdict
        return self.get_response(request)
```

### Go

```bash
go get google.golang.org/grpc google.golang.org/protobuf
# Generate stubs (requires protoc + protoc-gen-go + protoc-gen-go-grpc)
protoc --go_out=. --go-grpc_out=. sdk/proto/detection.proto
```

```go
import (
    "context"
    "net/http"
    pb "your-module/stylobot/detection/v1"
    "google.golang.org/grpc"
    "google.golang.org/grpc/credentials/insecure"
)

var (
    conn   *grpc.ClientConn
    client pb.DetectionServiceClient
)

func init() {
    conn, _ = grpc.Dial("stylobot-sidecar:5090",
        grpc.WithTransportCredentials(insecure.NewCredentials()))
    client = pb.NewDetectionServiceClient(conn)
}

func BotDetectionMiddleware(next http.Handler) http.Handler {
    return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        headers := make(map[string]string, len(r.Header))
        for k, v := range r.Header {
            if len(v) > 0 { headers[k] = v[0] }
        }
        resp, err := client.Detect(context.Background(), &pb.DetectRequest{
            Method:   r.Method,
            Path:     r.URL.Path,
            Headers:  headers,
            RemoteIp: r.RemoteAddr,
            Protocol: "https",
        })
        if err == nil && resp.RecommendedAction == pb.RecommendedAction_RECOMMENDED_ACTION_BLOCK {
            http.Error(w, "Forbidden", http.StatusForbidden)
            return
        }
        next.ServeHTTP(w, r)
    })
}
```

### Node.js (REST - no gRPC client needed)

The `@stylobot/node` package already handles sidecar mode over REST:

```ts
import { styloBotMiddleware } from '@stylobot/node'

app.use(styloBotMiddleware({
    mode: 'api',
    endpoint: 'http://stylobot-sidecar:5090',
    apiKey: process.env.STYLOBOT_API_KEY,
}))
```

---

## REST interface

For languages or environments where gRPC isn't available, use the REST endpoint. Requires the `X-SB-Api-Key` header.

```bash
curl -X POST http://localhost:5090/api/v1/detect \
  -H "Content-Type: application/json" \
  -H "X-SB-Api-Key: changeme" \
  -d '{
    "method": "GET",
    "path": "/api/users",
    "headers": {
      "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ...",
      "Accept": "application/json"
    },
    "remoteIp": "203.0.113.45",
    "protocol": "https"
  }'
```

Response:
```json
{
  "verdict": {
    "isBot": false,
    "botProbability": 0.04,
    "confidence": 0.91,
    "botType": null,
    "botName": null,
    "riskBand": "VeryLow",
    "recommendedAction": "Allow",
    "threatScore": 0.02,
    "threatBand": "None"
  },
  "reasons": [
    { "detector": "HeuristicEarly", "detail": "92% human likelihood", "impact": -0.84 }
  ],
  "meta": {
    "processingTimeMs": 0.38,
    "detectorsRun": 13,
    "aiRan": false
  }
}
```

---

## Kubernetes sidecar

```yaml
spec:
  containers:
  - name: your-app
    image: your-app:latest
    env:
    - name: STYLOBOT_GRPC_URL
      value: "http://localhost:5090"   # same Pod -> shared loopback

  - name: stylobot-sidecar
    image: scottgal/stylobot-sidecar:latest
    # No STYLOBOT_BIND: loopback only. Reachable from other containers in this
    # Pod (shared network namespace), not from other Pods. No API key needed.
    volumeMounts:
    - name: stylobot-data
      mountPath: /data
    livenessProbe:
      # An exec probe is required: a loopback-bound listener does not answer
      # the kubelet's httpGet probe (that targets the Pod IP). curl runs
      # inside the container, where 127.0.0.1 reaches the sidecar.
      exec:
        command: ["curl", "-fsS", "--http2-prior-knowledge", "http://127.0.0.1:5090/health"]
      initialDelaySeconds: 5
      periodSeconds: 10
  volumes:
  - name: stylobot-data
    emptyDir: {}  # or a PVC for durable session learning
```

Because the sidecar runs in the same Pod, your app connects to `localhost:5090`
- no network hop beyond the loopback interface, and nothing outside the Pod can
reach the detection engine.

To reach the sidecar from **other** Pods, set `STYLOBOT_BIND=any`, configure an
API key, and restrict callers with a `NetworkPolicy`:

```yaml
    env:
    - name: STYLOBOT_BIND
      value: "any"
    - name: BotDetection__ApiKeys__app__Key
      valueFrom:
        secretKeyRef:
          name: stylobot-secrets
          key: api-key
    - name: BotDetection__ApiKeys__app__Name
      value: "app"
```

---

## Configuration

All standard `BotDetection` configuration applies via environment variables (double underscore for nesting):

| Variable | Default | Description |
|----------|---------|-------------|
| `STYLOBOT_PORT` | `5090` | gRPC listen port (HTTP/2) |
| `STYLOBOT_REST_PORT` | `STYLOBOT_PORT + 1` | REST listen port (HTTP/1.1) |
| `STYLOBOT_GRPC_ONLY` | `false` | gRPC only - drops the REST surface entirely |
| `STYLOBOT_BIND` | `loopback` | `loopback` (default) or `any` to bind all interfaces |
| `STYLOBOT_ALLOW_INSECURE` | `false` | Permit `STYLOBOT_BIND=any` with no API keys (not recommended) |
| `STYLOBOT_MAX_BATCH` | `100` | Max requests per `DetectBatch` call |
| `STYLOBOT_MAX_TEMPLATE_LENGTH` | `65536` | Max `RenderWidget` template length (characters) |
| `STYLOBOT_MAX_RENDER_STEPS` | `10000` | Max Liquid statements per `RenderWidget` render |
| `BotDetection__BotThreshold` | `0.7` | Probability at which `isBot = true` |
| `BotDetection__DefaultActionPolicyName` | `logonly` | Default action policy |
| `BotDetection__ApiKeys__<id>__Key` | (see note) | Secret value callers present; required when `STYLOBOT_BIND=any` |
| `BotDetection__ApiKeys__<id>__Name` | - | Friendly name for the key |

For full configuration options see [configuration-reference.md](configuration-reference.md).

---

## Performance

The sidecar adds the latency of one loopback network call to your request processing:

| Transport | Overhead | Notes |
|-----------|----------|-------|
| gRPC (same pod) | ~0.1-0.3ms | Loopback + protobuf serialization |
| gRPC (same host) | ~0.2-0.5ms | Docker bridge network |
| REST (same pod) | ~0.3-0.8ms | JSON serialization overhead |
| REST (same host) | ~0.5-1.5ms | Docker bridge + JSON |

Detection itself runs in ~150µs. The bottleneck for sidecar mode is the network call, not the detector pipeline.

For the lowest possible latency on a .NET backend, use [in-process middleware](quickstart.md) instead - detection runs in your process with no network hop at all.

---

## Commercial

In the commercial edition, the sidecar adds:
- PostgreSQL + pgvector persistence (session vectors survive restarts, enable similarity search at scale)
- Redis cross-sidecar reputation sharing (multiple sidecar instances share learned reputation)
- Prometheus `/metrics` endpoint for fleet monitoring
