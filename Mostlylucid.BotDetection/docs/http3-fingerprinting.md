# HTTP/3 (QUIC) Fingerprinting

## Overview

The `Http3FingerprintContributor` analyzes QUIC/HTTP3 protocol characteristics to distinguish real browsers from automation tools. Since most bot frameworks do not yet support HTTP/3, the mere presence of a QUIC connection is a mild human signal. The detector then examines transport parameters, version negotiation, session resumption, and connection migration to build a detailed fingerprint.

## Detection Signals

### 1. QUIC Transport Parameter Fingerprinting

Different QUIC implementations use distinctive `initial_max_data` values in their transport parameters (exposed via `X-QUIC-Transport-Params` header from the reverse proxy):

| Client | `initial_max_data` | Classification |
|--------|-------------------|----------------|
| Chrome | 15,728,640 (15 MB) | Browser |
| Firefox | 10,485,760 (10 MB) | Browser |
| Safari | 8,388,608 (8 MB) | Browser |
| Go quic-go | 1,048,576 (1 MB) | Bot library |
| Python aioquic | 2,097,152 (2 MB) | Bot library |
| curl+quiche | 10,000,000 | Bot library |

**Signal keys:** `h3.client_type`, `h3.transport_params`

### 2. QUIC Version Analysis

| Version | Meaning | Signal |
|---------|---------|--------|
| `v1` (RFC 9000) | Standard, current | Neutral |
| `v2` (RFC 9369) | Very modern | Human (only latest browsers) |
| `draft-*` | Obsolete draft versions | Bot (old tooling) |

**Signal key:** `h3.quic_version`

### 3. 0-RTT Resumption

When `X-QUIC-0RTT` is `1` or `true`, the client used QUIC 0-RTT early data. This means the visitor has a cached session ticket from a previous visit - a strong returning-visitor signal that bots rarely exhibit.

**Signal key:** `h3.zero_rtt`

### 4. Connection Migration

When `X-QUIC-Connection-Migrated` is `true`, the QUIC connection migrated across network interfaces (e.g., WiFi to cellular). This is characteristic of mobile users and is effectively impossible for headless bots.

**Signal key:** `h3.connection_migrated`

### 5. Spin Bit

The QUIC spin bit is used for passive RTT measurement. When `X-QUIC-Spin-Bit` is `0`, the client has disabled cooperative RTT measurement, which some bot frameworks do.

### 6. Alt-Svc Upgrade

When `X-QUIC-Alt-Svc-Used` is `1`, the client arrived via Alt-Svc negotiation (HTTP/2 -> HTTP/3 upgrade). This multi-step protocol negotiation is a strong human signal as bots rarely implement Alt-Svc discovery.

### 7. HTTP/3 as Positive Signal

Using HTTP/3 at all produces a mild human signal. Most automation frameworks (Selenium, Puppeteer, curl, scrapy, etc.) do not support QUIC, so HTTP/3 traffic is disproportionately from real browsers.

## Configuration

YAML manifest: `Orchestration/Manifests/detectors/http3.detector.yaml`

| Parameter | Default | Description |
|-----------|---------|-------------|
| `quic_bot_confidence` | 0.6 | Confidence delta for known bot QUIC stacks |
| `quic_browser_confidence` | -0.2 | Confidence delta for known browser QUIC stacks |
| `zero_rtt_human_bonus` | -0.15 | Bonus for 0-RTT session resumption |
| `connection_migration_human_bonus` | -0.1 | Bonus for connection migration |
| `draft_version_penalty` | 0.3 | Penalty for obsolete QUIC draft versions |
| `alt_svc_upgrade_bonus` | -0.2 | Bonus for Alt-Svc protocol upgrade |

## Interaction with Http2FingerprintContributor

HTTP/3 requests are detected early in `Http2FingerprintContributor` and returned with a neutral signal (`h2.is_http3 = true`). The actual HTTP/3 analysis is performed entirely by `Http3FingerprintContributor`, preventing false positive penalties from the HTTP/2 detector's "not HTTP/2" path.

## Reverse Proxy Requirements

The QUIC headers (`X-QUIC-*`) must be set by the reverse proxy (e.g., Caddy, nginx with QUIC module). Without these headers, the detector can only detect HTTP/3 usage as a positive signal but cannot perform transport-level fingerprinting.
