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

## Quick start

```bash
docker run --rm -p 5090:5090 \
  -e BotDetection__ApiKeys__0__Key=changeme \
  -e BotDetection__ApiKeys__0__Name=app \
  scottgal/stylobot-sidecar:latest
```

Health check: `GET http://localhost:5090/health`

For persistent state (session learning, reputation) mount a volume:

```bash
docker run --rm -p 5090:5090 \
  -v stylobot-data:/data \
  -e BotDetection__ApiKeys__0__Key=changeme \
  -e BotDetection__ApiKeys__0__Name=app \
  scottgal/stylobot-sidecar:latest
```

---

## Docker Compose

See [`scripts/docker-compose.sidecar.yml`](../../scripts/docker-compose.sidecar.yml) for a complete example with health checks and volume mounts.

```bash
STYLOBOT_API_KEY=changeme docker compose -f scripts/docker-compose.sidecar.yml up
```

---

## gRPC interface

The proto file is at [`sdk/proto/detection.proto`](../../sdk/proto/detection.proto). Generate stubs for your language with `protoc` or `buf`.

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
      value: "http://localhost:5090"

  - name: stylobot-sidecar
    image: scottgal/stylobot-sidecar:latest
    ports:
    - containerPort: 5090
    env:
    - name: STYLOBOT_PORT
      value: "5090"
    - name: BotDetection__ApiKeys__0__Key
      valueFrom:
        secretKeyRef:
          name: stylobot-secrets
          key: api-key
    - name: BotDetection__ApiKeys__0__Name
      value: "app"
    volumeMounts:
    - name: stylobot-data
      mountPath: /data
    livenessProbe:
      httpGet:
        path: /health
        port: 5090
      initialDelaySeconds: 5
      periodSeconds: 10
  volumes:
  - name: stylobot-data
    emptyDir: {}  # or a PVC for durable session learning
```

Because the sidecar runs in the same pod, your app connects to `localhost:5090`. No network hop beyond the loopback interface.

---

## Configuration

All standard `BotDetection` configuration applies via environment variables (double underscore for nesting):

| Variable | Default | Description |
|----------|---------|-------------|
| `STYLOBOT_PORT` | `5090` | Listen port |
| `BotDetection__BotThreshold` | `0.7` | Probability at which `isBot = true` |
| `BotDetection__DefaultActionPolicyName` | `logonly` | Default action policy |
| `BotDetection__ApiKeys__0__Key` | (required) | API key for REST endpoints |
| `BotDetection__ApiKeys__0__Name` | `app` | Friendly name for the key |

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
