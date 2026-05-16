# StyloBot Integration Test Environments

Integration environments for testing the sidecar gRPC detection service with different middleware stacks.

## Prerequisites

- .NET 10 SDK (to run the sidecar)
- Docker + Docker Compose (for upstream/proxy containers)
- k6 (for load tests): `brew install k6`
- Node.js 22+ (for Node SDK tests)

## Architecture

```
k6 → [Node proxy OR Caddy] → Upstream echo server
              ↓ gRPC
           Sidecar (host)
```

The sidecar always runs on the host machine because it depends on sibling repos (styloflow, atoms) via local project references, making containerization complex.

## Running the Sidecar

The sidecar binds **loopback only** by default. k6 running on the host can reach
`localhost:5090` as-is, but the Dockerised proxies (Caddy, Node) reach the host
sidecar via `host.docker.internal` - that needs `STYLOBOT_BIND=any`. Binding all
interfaces with no API keys is refused unless `STYLOBOT_ALLOW_INSECURE=true` is
set, which is acceptable for this local test rig.

```bash
# Direct gRPC test (k6 on the host) - loopback default is fine
dotnet run --project src/Mostlylucid.BotDetection.Sidecar

# Behind a Dockerised proxy - expose it and allow the unauthenticated test rig
STYLOBOT_BIND=any STYLOBOT_ALLOW_INSECURE=true STYLOBOT_GRPC_ONLY=true \
  dotnet run --project src/Mostlylucid.BotDetection.Sidecar
```

The sidecar listens on port 5090 by default (`STYLOBOT_PORT` env var to change).

## Test Environments

### baseline-grpc: Direct gRPC

Tests the sidecar directly with no proxy. Establishes the raw detection latency baseline.

```bash
k6 run tests/k6/baseline-grpc.js
# Override sidecar address:
k6 run -e SIDECAR_ENDPOINT=localhost:5090 tests/k6/baseline-grpc.js
```

### node-sidecar: Node Middleware + Sidecar

```bash
# 1. Start the sidecar on the host (exposed so the container can reach it)
STYLOBOT_BIND=any STYLOBOT_ALLOW_INSECURE=true STYLOBOT_GRPC_ONLY=true \
  dotnet run --project src/Mostlylucid.BotDetection.Sidecar

# 2. Start the docker-compose environment
cd tests/integration/node-sidecar
npm install --prefix app   # install @grpc/grpc-js etc.
docker compose up

# 3. Run k6
k6 run tests/k6/node-sidecar.js
# Override app URL:
k6 run -e APP_URL=http://localhost:13001 tests/k6/node-sidecar.js
```

### caddy-sidecar: Caddy Plugin + Sidecar

```bash
# 1. Start the sidecar on the host (exposed so the container can reach it)
STYLOBOT_BIND=any STYLOBOT_ALLOW_INSECURE=true STYLOBOT_GRPC_ONLY=true \
  dotnet run --project src/Mostlylucid.BotDetection.Sidecar

# 2. Build and start Caddy (first run builds xcaddy image, ~2min)
cd tests/integration/caddy-sidecar
docker compose up --build

# 3. Run k6
k6 run tests/k6/caddy-sidecar.js
# Override Caddy URL:
k6 run -e CADDY_URL=http://localhost:14080 tests/k6/caddy-sidecar.js
```

## Thresholds

| Script | Threshold |
|--------|-----------|
| baseline-grpc | p(99) gRPC < 30ms, p(99) detection latency < 10ms |
| node-sidecar | p(95) HTTP < 500ms, stylobot headers present > 95% |
| caddy-sidecar | p(95) HTTP < 500ms, blocked < 1000 total |
