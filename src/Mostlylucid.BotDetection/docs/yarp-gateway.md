# Stylobot Gateway

A lightweight, Docker-first YARP reverse proxy gateway - the companion project to Mostlylucid.BotDetection.

> **Full Documentation**: See the [Stylobot.Gateway README](../../Stylobot.Gateway/README.md) for complete
> documentation.

## Overview

Stylobot.Gateway is a standalone Docker image that provides:

- Zero-config reverse proxy (just set `DEFAULT_UPSTREAM`)
- YARP-based routing with hot-reload configuration
- Admin API for health checks, config inspection, and metrics
- SQLite persistence (FOSS); PostgreSQL/SQL Server via commercial add-on packs
- Multi-architecture support (amd64, arm64, arm/v7 for Raspberry Pi)

## Quick Start

```bash
# Zero-config mode
docker run -p 8080:8080 -e DEFAULT_UPSTREAM=http://your-backend:3000 scottgal/stylobot-gateway

# With file configuration
docker run -p 8080:8080 -v ./config:/app/config:ro scottgal/stylobot-gateway
```

## Transport fingerprint headers and trust gate

When `Stylobot.Gateway` sits behind a CDN or upstream reverse proxy that injects transport fingerprint headers (`X-JA3-*`, `X-JA4*`, `X-Client-TLS-*`, `X-HTTP2-*`, `X-QUIC-*`, `X-TCP-*`), the gateway gates those headers on the **immediate TCP peer IP** before passing them to the detection pipeline. Headers from untrusted public-IP peers are rejected and generate a bot signal rather than a human bias.

Configure trusted upstream proxies under `BotDetection:TransportTrust:TrustedProxyIps` (CIDR list). When the gateway itself terminates TLS (no upstream proxy), Kestrel provides TLS metadata natively and no header forwarding is needed.

See [`docs/REVERSE_PROXY_SIGNALS.md`](../../../docs/REVERSE_PROXY_SIGNALS.md#trusted-proxy-gate-transport-fingerprint-headers) for the full configuration reference and per-CDN recipes.

## Using with BotDetection

The Stylobot Gateway pairs naturally with Mostlylucid.BotDetection for edge protection:

### Architecture

```
Internet → Stylobot Gateway → Your App (with BotDetection)
              ↓
        Load balancing
        Health checks
        TLS termination
```

### Deployment Pattern

1. **Stylobot Gateway** handles:
    - Edge routing and load balancing
    - TLS termination
    - Request forwarding
    - Health monitoring

2. **BotDetection** handles:
    - Bot classification
    - Rate limiting
    - Challenge/response
    - Learning and adaptation

### Docker Compose Example

```yaml
services:
  gateway:
    image: scottgal/stylobot-gateway:latest
    ports:
      - "80:8080"
    environment:
      - ADMIN_SECRET=gateway-secret
    volumes:
      - ./yarp.json:/app/config/yarp.json:ro

  webapp:
    build: .
    environment:
      - BotDetection__EnableAiDetection=true
      - BotDetection__Learning__Enabled=true
    # Not exposed - only accessible via gateway
```

With `yarp.json`:

```json
{
  "ReverseProxy": {
    "Routes": {
      "webapp": {
        "ClusterId": "webapp",
        "Match": { "Path": "/{**catch-all}" }
      }
    },
    "Clusters": {
      "webapp": {
        "Destinations": {
          "primary": { "Address": "http://webapp:8080" }
        },
        "HealthCheck": {
          "Passive": { "Enabled": true }
        }
      }
    }
  }
}
```

## Raspberry Pi Deployment

Stylobot Gateway is optimized for Raspberry Pi as a home network gateway:

```bash
# On Raspberry Pi (64-bit OS recommended)
docker run -d --name gateway \
  -p 80:8080 \
  --restart unless-stopped \
  -e DEFAULT_UPSTREAM=http://192.168.1.100:3000 \
  scottgal/stylobot-gateway
```

See the [full Pi documentation](../../Stylobot.Gateway/README.md#raspberry-pi-deployment) for:

- Memory-optimized settings
- Performance tips
- Home network routing examples

## Key Features

| Feature         | Description                                        |
|-----------------|----------------------------------------------------|
| **Zero-config** | Single env var to get started                      |
| **Hot reload**  | Config changes without restart                     |
| **Admin API**   | `/admin/health`, `/admin/routes`, `/admin/metrics` |
| **Multi-arch**  | amd64, arm64, arm/v7                               |
| **Lightweight** | ~90MB Alpine-based image                           |

## Configuration Reference

For complete configuration options including:

- Environment variables (`GATEWAY_HTTP_PORT`, `DEFAULT_UPSTREAM`, `ADMIN_SECRET`, etc.)
- Volume mounts (`/app/config`, `/app/data`, `/app/logs`, `/app/plugins`)
- YARP routing configuration (`yarp.json`)
- Database setup (Postgres, SQL Server)
- Admin API endpoints

**See: [Full Configuration Documentation](../../Stylobot.Gateway/README.md#configuration)**

## Available Tags

The Docker image is published with multiple tags:

- `scottgal/stylobot-gateway:latest` - Latest release
- `scottgal/stylobot-gateway:X.Y.Z[-previewN]` - Specific version (e.g., `1.0.0-preview1`)
- `scottgal/stylobot-gateway:YYYYMMDD` - Date-based (e.g., `20231203`)

## Links

- [Docker Hub](https://hub.docker.com/r/scottgal/stylobot-gateway)
- [Full Documentation](../../Stylobot.Gateway/README.md)
- [Source Code](../../Stylobot.Gateway/)
