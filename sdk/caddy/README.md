# caddy-stylobot

Caddy v2 middleware for StyloBot bot detection via gRPC. Persistent HTTP/2 connection to the StyloBot sidecar — sub-0.5ms overhead on localhost.

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
            endpoint localhost:5090
            timeout  50ms
            on_block 403
        }
        reverse_proxy :3000
    }

## Directives

| Directive | Default    | Description |
|-----------|------------|-------------|
| endpoint  | (required) | host:port of StyloBot sidecar gRPC endpoint |
| api_key   |            | Optional API key (sent as gRPC metadata) |
| timeout   | 50ms       | Per-request timeout. Fails open on timeout. |
| on_block  | 403        | HTTP status when action=Block. 0 = headers only. |

## Headers injected (upstream request)

- `X-StyloBot-IsBot` — true/false
- `X-StyloBot-Probability` — 0.0000-1.0000
- `X-StyloBot-Confidence` — 0.0000-1.0000
- `X-StyloBot-BotType` — e.g. "Scraper"
- `X-StyloBot-BotName` — e.g. "Shadowreaper-7"
- `X-StyloBot-RiskBand` — VeryLow/Low/Elevated/Medium/High/VeryHigh/Verified
- `X-StyloBot-Action` — Allow/Throttle/Challenge/Block
- `X-StyloBot-ThreatScore` — 0.0000-1.0000
- `X-StyloBot-ThreatBand` — None/Low/Elevated/High/Critical

## Fail-open

If the sidecar is down or times out, the request forwards unchanged. Your app stays up.
