# caddy-stylobot

Caddy v2 middleware that calls the StyloBot gRPC `DetectionService` on every request, injects `X-StyloBot-*` headers onto the upstream request, and optionally blocks bots with a configurable HTTP status.

<!-- badges placeholder -->

---

## Why gRPC?

The StyloBot sidecar exposes both gRPC and REST detection endpoints. The Caddy module uses gRPC because it maintains a persistent HTTP/2 connection: there is no TCP handshake or TLS negotiation per request. Binary protobuf encoding is also significantly smaller than JSON. In practice this means the detection round-trip from Caddy to a localhost sidecar is typically under 0.5ms, compared to 1-5ms for an equivalent HTTP/JSON call that sets up a new connection each time.

---

## How it works

1. A request arrives at Caddy.
2. The `stylobot` handler calls `DetectionService.Detect()` on the sidecar via gRPC, passing the method, path, headers, client IP, and protocol.
3. The sidecar runs its detector pipeline (up to 57 detectors) and returns a `DetectResponse` with risk scores, a recommended action, and classification metadata.
4. The handler injects nine `X-StyloBot-*` headers onto the proxied request before it reaches your application.
5. If the sidecar returns `action=Block` and `on_block` is non-zero, Caddy returns the configured HTTP status immediately; the request never reaches your app.
6. If the sidecar is unreachable or times out, the request forwards unchanged (fail-open). Your application stays up even if the sidecar is down.

---

## Prerequisites

- Go 1.22 or later (to build with xcaddy)
- [xcaddy](https://github.com/caddyserver/xcaddy): `go install github.com/caddyserver/xcaddy/cmd/xcaddy@latest`
- StyloBot sidecar running (see "Quick start" below)

---

## Quick start (5 minutes)

### Step 1: Start the StyloBot sidecar

The sidecar is a self-contained ASP.NET Core process that listens on port 5090 by default. It serves both gRPC and REST on the same port.

```bash
cd src/Mostlylucid.BotDetection.Sidecar
dotnet run
```

Confirm it is healthy:

```bash
curl http://localhost:5090/health
# {"status":"healthy","mode":"sidecar","port":5090}
```

Or pull the Docker image instead:

```bash
docker run -d --name stylobot-sidecar \
  -p 127.0.0.1:5090:5090 \
  scottgal/stylobot-sidecar:latest
```

### Step 2: Build Caddy with the stylobot module

```bash
xcaddy build --with github.com/scottgal/caddy-stylobot
```

This produces a `caddy` binary in the current directory. The standard Caddy binary from the official downloads does not include this module.

### Step 3: Write a minimal Caddyfile

Create a file named `Caddyfile`:

```caddyfile
:80 {
    stylobot {
        endpoint localhost:5090
        timeout  50ms
        on_block 403
    }
    reverse_proxy :3000
}
```

This tells Caddy to call the sidecar on every request, inject the detection headers, and block confirmed bots with a 403 before the request reaches your app on port 3000.

### Step 4: Start Caddy

```bash
./caddy run
```

### Step 5: Test it

The detection headers are injected onto the upstream request, not the response. Your app receives them. To verify they are arriving, add a debug endpoint that echoes all request headers, or check your application logs.

To test blocking behavior, use a tool-like User-Agent that the sidecar will flag:

```bash
curl -v -H "User-Agent: curl/7.68.0" http://localhost/
# HTTP/1.1 403 Forbidden
# Forbidden
```

A normal browser request should pass through:

```bash
curl -v -H "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0 Safari/537.36" http://localhost/
# HTTP/1.1 200 OK
```

---

## Installation

### Option 1: Build with xcaddy

```bash
go install github.com/caddyserver/xcaddy/cmd/xcaddy@latest
xcaddy build --with github.com/scottgal/caddy-stylobot
```

The resulting binary is a complete Caddy build with the stylobot module compiled in.

### Option 2: Docker multi-stage build

```dockerfile
FROM caddy:builder AS builder
RUN xcaddy build --with github.com/scottgal/caddy-stylobot

FROM caddy:latest
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
```

Build and tag:

```bash
docker build -t my-caddy-stylobot .
```

---

## Configuration reference

All directives live inside the `stylobot { }` block in your Caddyfile:

| Directive  | Type     | Default    | Description |
|------------|----------|------------|-------------|
| `endpoint` | string   | (required) | `host:port` of the StyloBot sidecar gRPC server. |
| `api_key`  | string   | (none)     | Optional API key forwarded as gRPC metadata. Matches a key in the sidecar's `BotDetection:ApiKeys` configuration. |
| `timeout`  | duration | `50ms`     | Per-request gRPC deadline. If the sidecar does not respond within this window, the request forwards unchanged (fail-open). |
| `on_block` | integer  | `403`      | HTTP status returned when the sidecar says `action=Block` and `is_bot=true`. Set to `0` to disable blocking entirely; headers are still injected. |

### Fully-annotated Caddyfile example

```caddyfile
:443 {
    tls internal

    stylobot {
        # Required: gRPC address of the StyloBot sidecar.
        endpoint localhost:5090

        # Optional: match a key in the sidecar's BotDetection:ApiKeys list.
        # Lets the sidecar attribute traffic to this Caddy instance.
        api_key my-secret-key

        # Per-request timeout. 50ms is safe on localhost.
        # Increase to 200ms during debugging if you see deadline exceeded errors.
        timeout 50ms

        # HTTP status returned for blocked requests. 403 is the default.
        # Set to 0 to inject headers but never block (observe-only mode).
        on_block 403
    }

    reverse_proxy localhost:3000
}
```

---

## Headers injected

These headers are set on the proxied (upstream) request before it reaches your application. They are not added to the HTTP response that the browser sees.

| Header | Example value | Description |
|---|---|---|
| `X-StyloBot-IsBot` | `true` | `true` if the request is classified as a bot, `false` otherwise. |
| `X-StyloBot-Probability` | `0.9312` | Bot probability from 0.0000 to 1.0000. Values above the sidecar's configured `BotThreshold` (default 0.7) trigger the Block action. |
| `X-StyloBot-Confidence` | `0.8750` | Detector confidence in the classification, 0.0000 to 1.0000. Low confidence with high probability means few detectors fired. |
| `X-StyloBot-BotType` | `Scraper` | Classified bot category. Empty string for human traffic. Common values: `Scraper`, `Scanner`, `CveProbe`, `AiScraper`. |
| `X-StyloBot-BotName` | `Shadowreaper-7` | Deterministic name assigned to this bot fingerprint. The same bot always gets the same name across requests and sessions. Empty for human traffic. |
| `X-StyloBot-RiskBand` | `High` | Ordinal risk category. Values in ascending order: `Unknown`, `VeryLow`, `Low`, `Elevated`, `Medium`, `High`, `VeryHigh`, `Verified`. |
| `X-StyloBot-Action` | `Block` | Recommended action: `Allow`, `Throttle`, `Challenge`, or `Block`. Caddy enforces Block when `on_block` is non-zero; all others are advisory for your app. |
| `X-StyloBot-ThreatScore` | `0.7410` | Threat intelligence score, 0.0000 to 1.0000. Elevated for CVE-targeting probes, scanner tools, and known malicious infrastructure. |
| `X-StyloBot-ThreatBand` | `Elevated` | Threat category: `None`, `Low`, `Elevated`, `High`, or `Critical`. |

---

## Deployment scenarios

### Scenario 1: Sidecar on the same host

```
Internet --> Caddy :80/:443 --> Your app :3000
                 |
                 +-> StyloBot sidecar :5090 (gRPC, localhost only)
```

Full Caddyfile:

```caddyfile
example.com {
    stylobot {
        endpoint localhost:5090
        timeout  50ms
        on_block 403
    }
    reverse_proxy localhost:3000
}
```

Bind the sidecar to `127.0.0.1:5090` so it is not reachable from the internet directly.

### Scenario 2: Sidecar in Docker Compose

Caddy references the sidecar by its Docker Compose service name. No ports need to be published to the host.

```yaml
version: "3.9"

services:
  caddy:
    image: my-caddy-stylobot        # built with xcaddy (see Installation)
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
    depends_on:
      stylobot-sidecar:
        condition: service_healthy

  stylobot-sidecar:
    image: scottgal/stylobot-sidecar:latest
    restart: unless-stopped
    environment:
      STYLOBOT_PORT: "5090"
      BotDetection__BotThreshold: "0.7"
      BotDetection__DefaultActionPolicyName: "block"
    volumes:
      - stylobot-data:/data
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5090/health"]
      interval: 10s
      timeout: 3s
      retries: 3
      start_period: 5s

  your-app:
    image: your-app:latest
    depends_on:
      caddy:
        condition: service_started

volumes:
  caddy-data:
  stylobot-data:
```

Caddyfile referencing the sidecar by service name:

```caddyfile
:80 {
    stylobot {
        endpoint stylobot-sidecar:5090
        timeout  50ms
        on_block 403
    }
    reverse_proxy your-app:3000
}
```

### Scenario 3: Observe-only mode

Set `on_block 0` to inject headers without ever blocking. Caddy becomes a passive observer and your application makes all enforcement decisions. This is a safe way to roll out StyloBot incrementally without risking false positives for real users.

```caddyfile
:80 {
    stylobot {
        endpoint localhost:5090
        timeout  50ms
        on_block 0        # never block; headers injected, app decides
    }
    reverse_proxy localhost:3000
}
```

Your app then reads `X-StyloBot-Action` and `X-StyloBot-RiskBand` to decide what to do.

### Scenario 4: App-driven challenge enforcement

When `on_block` is non-zero, Caddy only enforces the `Block` action. The `Challenge` and `Throttle` actions are advisory: they appear in `X-StyloBot-Action` but Caddy passes the request through. Your app reads the header and serves a CAPTCHA or adds a delay.

This lets you handle elevated-risk traffic with a soft response while still having Caddy cut off confirmed bots:

```caddyfile
:80 {
    stylobot {
        endpoint localhost:5090
        timeout  50ms
        on_block 403      # hard block for confirmed bots
    }
    reverse_proxy localhost:3000   # app handles Challenge/Throttle
}
```

In your application:

```javascript
const action = req.headers['x-stylobot-action'];
if (action === 'Challenge') {
    return res.redirect('/challenge');  // serve CAPTCHA
}
if (action === 'Throttle') {
    await sleep(2000);  // add deliberate delay
}
```

---

## Reading headers in your app

The headers arrive on the incoming request inside your application. Here are short examples in common languages.

### Node.js (Express)

```javascript
app.use((req, res, next) => {
    const isBot    = req.headers['x-stylobot-isbot'] === 'true';
    const riskBand = req.headers['x-stylobot-riskband'] ?? 'Unknown';
    const action   = req.headers['x-stylobot-action']  ?? 'Allow';

    console.log(`[stylobot] isBot=${isBot} riskBand=${riskBand} action=${action}`);

    if (action === 'Block') {
        return res.status(403).send('Forbidden');
    }
    next();
});
```

### Python (Flask)

```python
from flask import request, abort

@app.before_request
def check_bot():
    is_bot   = request.headers.get('X-StyloBot-IsBot', 'false') == 'true'
    action   = request.headers.get('X-StyloBot-Action', 'Allow')
    riskband = request.headers.get('X-StyloBot-RiskBand', 'Unknown')

    app.logger.info(f"stylobot: is_bot={is_bot} risk={riskband} action={action}")

    if action == 'Block':
        abort(403)
```

### PHP

```php
$isBot    = ($_SERVER['HTTP_X_STYLOBOT_ISBOT']   ?? 'false') === 'true';
$riskBand = $_SERVER['HTTP_X_STYLOBOT_RISKBAND'] ?? 'Unknown';
$action   = $_SERVER['HTTP_X_STYLOBOT_ACTION']   ?? 'Allow';

error_log("stylobot: isBot=$isBot riskBand=$riskBand action=$action");

if ($action === 'Block') {
    http_response_code(403);
    exit('Forbidden');
}
```

### Go (net/http)

```go
func botMiddleware(next http.Handler) http.Handler {
    return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
        isBot    := r.Header.Get("X-StyloBot-IsBot")    == "true"
        riskBand := r.Header.Get("X-StyloBot-RiskBand")
        action   := r.Header.Get("X-StyloBot-Action")

        log.Printf("stylobot: isBot=%v riskBand=%s action=%s", isBot, riskBand, action)

        if action == "Block" {
            http.Error(w, "Forbidden", http.StatusForbidden)
            return
        }
        next.ServeHTTP(w, r)
    })
}
```

---

## TLS between Caddy and sidecar

By default the module uses insecure (plaintext) gRPC. This is appropriate when Caddy and the sidecar are on the same host or in the same Docker network, where the traffic does not leave a trusted boundary. Do not expose the sidecar's gRPC port to an untrusted network without TLS.

If you need encrypted gRPC between Caddy and a remote sidecar, TLS support can be added to the module. Open an issue or pull request on the repository.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Headers not appearing on upstream request | Caddy binary was not built with xcaddy and this module | Rebuild with `xcaddy build --with github.com/scottgal/caddy-stylobot` |
| All requests are blocked | `on_block` is set and the sidecar is returning `Block` for all traffic | Set `on_block 0` temporarily; check sidecar logs; raise `BotDetection:BotThreshold` (e.g. to `0.85`) |
| High latency on every request | Sidecar is running on a different host | gRPC is fast on localhost; each network hop adds latency. Co-locate the sidecar. |
| `connection refused` on startup | Sidecar is not running or is on a different port | Start the sidecar: `dotnet run --project src/Mostlylucid.BotDetection.Sidecar`. Check `STYLOBOT_PORT`. |
| `context deadline exceeded` in logs | `timeout` is too short for the current sidecar load | Increase `timeout` to `200ms` for debugging, then tune down once you understand normal latency |
| Sidecar health check fails in Docker Compose | Sidecar container still starting when Caddy starts | Add `depends_on` with `condition: service_healthy` as shown in Scenario 2 |

---

## Performance

The gRPC connection is established once when the Caddy module provisions on startup. The first request may take a few milliseconds extra while the HTTP/2 connection is being established. All subsequent requests reuse that connection and incur only the serialization and network round-trip overhead.

On localhost with a warmed sidecar, typical overhead is under 0.5ms per request. This is well within the budget for a detection layer in front of a web application.

The 50ms default timeout gives the sidecar 100x its typical response time as slack for garbage collection pauses, cold I/O, and system noise. If your sidecar consistently uses more than 5ms, check whether it is under unexpected load or whether SQLite is contending on a slow disk.

For detailed information about headers and their interpretation, see the [header reference](docs/header-reference.md). For deployment tutorials, see the [Express tutorial](docs/tutorial-express.md) and [Docker Compose example](docs/docker-compose-example.md).
