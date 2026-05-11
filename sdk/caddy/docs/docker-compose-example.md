# Docker Compose deployment

This tutorial shows how to run StyloBot bot detection in a Docker Compose stack with Caddy as the entry point. It covers a four-service setup, health checks, volume mounts, environment variables, and how to verify everything is working.

---

## Architecture

```
Internet
    |
    v
+-------------------+
|     Caddy :80     |  TLS termination, stylobot gRPC middleware
+-------------------+
    |           |
    |           v
    |   +--------------------+
    |   | stylobot-sidecar   |  gRPC :5090  (internal network only)
    |   | :5090              |  All 49 detectors + REST health endpoint
    |   +--------------------+
    |
    v
+-------------------+
|    your-app :3000 |  Your application. Receives X-StyloBot-* headers.
+-------------------+
    |
    v (optional)
+-------------------+
|    Grafana/Loki   |  Log aggregation (optional, shown separately)
+-------------------+
```

Caddy sits in front of everything. On each incoming request it calls the sidecar over gRPC (internal Docker network, no published port), injects the detection headers, then forwards the request to your application. The sidecar is never reachable from outside the Docker network.

---

## The docker-compose.yml

Save this as `docker-compose.yml` in your project root:

```yaml
version: "3.9"

services:

  # ----------------------------------------------------------------
  # Caddy: TLS termination + StyloBot gRPC middleware
  # Build your own image with xcaddy (see Dockerfile below).
  # ----------------------------------------------------------------
  caddy:
    image: my-caddy-stylobot:latest
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    environment:
      # Caddy reads ACME email from environment for automatic TLS.
      ACME_EMAIL: "ops@example.com"
    depends_on:
      stylobot-sidecar:
        condition: service_healthy
      your-app:
        condition: service_started
    networks:
      - frontend
      - internal

  # ----------------------------------------------------------------
  # StyloBot sidecar: gRPC detection service
  # Internal only -- no published ports.
  # ----------------------------------------------------------------
  stylobot-sidecar:
    image: scottgal/stylobot-sidecar:latest
    restart: unless-stopped
    environment:
      # Port the sidecar listens on (gRPC + REST on the same port).
      STYLOBOT_PORT: "5090"

      # Detection threshold. Requests with bot probability above this
      # value get action=Block. Default is 0.7. Raise to 0.85 to reduce
      # false positives during initial rollout.
      BotDetection__BotThreshold: "0.7"

      # What to do with confirmed bots at the sidecar level.
      # "block" means the recommended action will be Block.
      # The Caddy middleware still controls whether to actually block.
      BotDetection__DefaultActionPolicyName: "block"

      # API key for the sidecar's REST endpoints.
      # gRPC traffic is network-isolated, so no key is needed there.
      BotDetection__ApiKeys__0__Key: "${STYLOBOT_API_KEY:-changeme}"
      BotDetection__ApiKeys__0__Name: "caddy"
    volumes:
      # Persistent SQLite database for reputation, sessions, and signals.
      - stylobot-data:/data
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:5090/health"]
      interval: 10s
      timeout: 3s
      retries: 3
      start_period: 10s
    networks:
      - internal

  # ----------------------------------------------------------------
  # Your application
  # Replace this with your actual service definition.
  # ----------------------------------------------------------------
  your-app:
    image: your-app:latest
    restart: unless-stopped
    environment:
      NODE_ENV: production
      PORT: "3000"
    networks:
      - internal
    # Do not publish port 3000 to the host. All traffic goes through Caddy.

  # ----------------------------------------------------------------
  # Optional: Grafana + Loki for log aggregation
  # Remove this block if you do not need centralized logging.
  # ----------------------------------------------------------------
  loki:
    image: grafana/loki:2.9.0
    restart: unless-stopped
    command: -config.file=/etc/loki/local-config.yaml
    networks:
      - internal

  grafana:
    image: grafana/grafana:10.2.0
    restart: unless-stopped
    ports:
      - "3001:3000"
    environment:
      GF_SECURITY_ADMIN_PASSWORD: "${GRAFANA_PASSWORD:-admin}"
    volumes:
      - grafana-data:/var/lib/grafana
    networks:
      - internal
      - frontend

networks:
  frontend:    # Caddy and Grafana are on the frontend network (published ports)
  internal:    # All services share the internal network; sidecar is not exposed

volumes:
  caddy-data:
  caddy-config:
  stylobot-data:
  grafana-data:
```

---

## Dockerfile for the Caddy + stylobot build

Save this as `Dockerfile.caddy` in the same directory:

```dockerfile
# Stage 1: build Caddy with the stylobot module compiled in
FROM caddy:builder AS builder
RUN xcaddy build \
    --with github.com/scottgal/caddy-stylobot

# Stage 2: final image -standard Caddy runtime with the custom binary
FROM caddy:latest
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
```

Build and tag it:

```bash
docker build -f Dockerfile.caddy -t my-caddy-stylobot:latest .
```

You only need to rebuild this image when you update the caddy-stylobot module version or upgrade Caddy itself.

---

## Caddyfile

Save this as `Caddyfile` in the same directory. It is mounted read-only into the Caddy container.

```caddyfile
{
    # Your email for Let's Encrypt automatic TLS.
    email {$ACME_EMAIL}
}

example.com {
    # StyloBot gRPC middleware.
    # The sidecar is reachable by its Docker Compose service name on the internal network.
    stylobot {
        endpoint stylobot-sidecar:5090
        timeout  50ms
        on_block 403
    }

    # Forward everything else to your application.
    reverse_proxy your-app:3000
}
```

For local development without a real domain, replace `example.com` with `:80` and remove the `email` block:

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

---

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `STYLOBOT_PORT` | `5090` | Port the sidecar listens on for gRPC and REST. |
| `STYLOBOT_API_KEY` | `changeme` | API key for the sidecar's REST `/api/v1/*` endpoints. Set a strong value in production. |
| `BotDetection__BotThreshold` | `0.7` | Probability above which a request is classified as a bot. Raise to reduce false positives. |
| `BotDetection__DefaultActionPolicyName` | `block` | Default policy for confirmed bots. Options: `block`, `throttle-stealth`, `challenge`, `logonly`. |
| `ACME_EMAIL` | (none) | Email address for Let's Encrypt TLS certificate provisioning. |
| `GRAFANA_PASSWORD` | `admin` | Grafana admin password. Change in production. |

---

## Starting and testing

Create a `.env` file with your secrets:

```bash
STYLOBOT_API_KEY=my-strong-key-here
ACME_EMAIL=ops@example.com
GRAFANA_PASSWORD=my-grafana-password
```

Start all services:

```bash
docker compose up -d
```

Watch the sidecar start up:

```bash
docker compose logs -f stylobot-sidecar
```

You should see:

```
[10:01:02 INF] StyloBot sidecar starting on port 5090 (gRPC + REST)
[10:01:02 INF] Now listening on: http://0.0.0.0:5090
```

Check that Caddy started without errors:

```bash
docker compose logs -f caddy
```

Test the sidecar health endpoint directly on the internal Docker network:

```bash
docker compose exec caddy curl -s http://stylobot-sidecar:5090/health
# {"status":"healthy","mode":"sidecar","port":5090}
```

Send a test request through Caddy:

```bash
curl -si http://localhost/
```

Send a bot-like request to verify blocking works:

```bash
curl -si -H "User-Agent: curl/7.68.0" http://localhost/
# HTTP/1.1 403 Forbidden
```

---

## Health check

The sidecar exposes a `/health` endpoint that returns HTTP 200 when it is ready. Docker Compose uses this for the `healthcheck` directive, which means Caddy will not start until the sidecar has passed three consecutive health checks.

You can also poll it manually from the host:

```bash
curl http://localhost:5090/health    # only works if you published port 5090
```

Or from inside the Caddy container:

```bash
docker compose exec caddy curl -s http://stylobot-sidecar:5090/health
```

Note: the `docker-compose.yml` above does not publish port 5090 to the host. If you want to call the sidecar's REST API for debugging from your machine, temporarily add the port mapping:

```yaml
stylobot-sidecar:
  ports:
    - "127.0.0.1:5090:5090"   # localhost only, not exposed to the internet
```

---

## Scaling

If you run multiple replicas of your application, Caddy load-balances between them automatically. The sidecar is stateless for detection (all persistent state lives in the SQLite database in the mounted volume), so a single sidecar handles detection for all app replicas without coordination overhead.

```bash
docker compose up -d --scale your-app=3
```

Caddy's `reverse_proxy` directive automatically distributes requests across all `your-app` containers on the internal network using round-robin by default.

The sidecar itself does not need to be scaled for detection: its fast path (most requests) completes in under 1ms and the bottleneck in practice is SQLite writes, not CPU. If you do run multiple Caddy instances in front of the same application (for example, behind a load balancer), point all of them at the same sidecar instance so reputation and session state accumulates correctly.