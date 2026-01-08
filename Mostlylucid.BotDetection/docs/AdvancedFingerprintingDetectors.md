# Advanced Fingerprinting Detectors v1

This document describes the newly implemented advanced bot detection techniques inspired by best-in-breed approaches
from Zeek, p0f, and modern threat intelligence.

## Overview

The advanced detection suite adds five new contributors that analyze network and protocol-layer characteristics to
detect sophisticated automation:

1. **TlsFingerprintContributor** - JA3/JA4-style TLS fingerprinting
2. **TcpIpFingerprintContributor** - TCP/IP stack fingerprinting (p0f-style)
3. **Http2FingerprintContributor** - HTTP/2 protocol fingerprinting (AKAMAI-style)
4. **MultiLayerCorrelationContributor** - Cross-layer consistency analysis
5. **BehavioralWaveformContributor** - Temporal pattern analysis across requests

## Inspiration from Zeek

These detectors incorporate several key ideas from Zeek's architecture:

### Signal Taxonomy (from Zeek's `http.log`)

- Per-request feature extraction: method, host, URI, referrer, UA, body lengths, status
- Transaction timing and state tracking
- This becomes our **signal schema** for behavioral waveforms

### Weird vs Notice Pattern (from `weird.log` and Notice Framework)

- **Weird signals**: Low-level protocol anomalies (unusual TTL, invalid cipher, etc.)
- **Notices**: Policy-significant detections (datacenter IP + browser UA)
- Decouples detection from policy → detectors raise signals, policies decide actions

### Multi-Flow Aggregation

- Behavioral waveform tracks patterns across multiple requests
- Inspired by Zeek's multi-threaded exfil detection
- Our Ephemeral coordinator layer provides similar cross-request state

## 1. TLS Fingerprinting (JA3/JA4 Style)

### Concept

Analyzes TLS handshake parameters to identify client implementations. Different browsers, automation tools, and custom
HTTP clients have distinct TLS "signatures".

### What It Detects

- **Known bot fingerprints**: cURL, Python requests, Go net/http, headless Chrome
- **Weak/outdated protocols**: SSL3, TLS 1.0/1.1 (modern browsers use TLS 1.2+)
- **Cipher suite anomalies**: Export-grade ciphers, NULL ciphers, weak hash algorithms
- **Client certificate usage**: Uncommon for browsers, common for automation

### Signals Raised

```csharp
tls.available          : bool
tls.protocol           : string  // "Tls12", "Tls13", etc.
tls.cipher_algo        : string
tls.hash_algo          : string
tls.key_exchange       : string
tls.ja3_hash          : string  // MD5 hash of fingerprint
tls.ja3_string        : string  // Raw fingerprint components
tls.client_cert_present: bool
tls.fingerprint_known  : bool
```

### Production Integration

For full JA3 fingerprinting, integrate with reverse proxy (nginx/HAProxy) that can extract TLS handshake and pass via
header:

```
X-JA3-Hash: a3b4c5d6e7f8...
```

### Example Detection

```
User-Agent: Mozilla/5.0 (Chrome 120...)
TLS Fingerprint: e7d1b9f8... (matches cURL)
→ MISMATCH → Bot detected with 85% confidence
```

## 2. TCP/IP Stack Fingerprinting (p0f Style)

### Concept

Passive OS fingerprinting by analyzing TCP/IP characteristics. Different operating systems and network stacks have
distinct "signatures" at the packet level.

### What It Detects

- **TCP window size patterns**: Windows (65535), Linux (5840), bots (4096/32768)
- **TTL (Time To Live) values**: Linux/Mac (64), Windows (128), unusual values (30)
- **TCP options**: Modern stacks use timestamps, SACK, window scaling
- **IP Don't Fragment flag**: Modern systems set it, some custom stacks don't
- **MSS (Maximum Segment Size)**: Standard (1460), old/custom (536)

### Signals Raised

```csharp
tcp.window_size        : int
tcp.ttl                : int
tcp.options_pattern    : string
tcp.mss                : int
tcp.os_hint           : string  // "Windows", "Linux", "Bot"
tcp.has_timestamp      : bool
tcp.has_sack           : bool
tcp.modern_options     : bool
ip.dont_fragment       : bool
ip.id_pattern          : string  // "sequential" (Windows) or "random" (Linux)
```

### Production Integration

Requires reverse proxy configuration to capture and forward TCP/IP metadata:

```
X-TCP-Window: 65535
X-TCP-TTL: 64
X-TCP-Options: MSS,SACK,TS,WS
X-TCP-MSS: 1460
X-IP-DF: 1
```

### Example Detection

```
User-Agent: Windows NT 10.0
TCP TTL: 64 (Linux pattern)
TCP Window: 4096 (Bot pattern)
→ OS MISMATCH → Bot detected with 55% confidence
```

## 3. HTTP/2 Fingerprinting (AKAMAI Style)

### Concept

Analyzes HTTP/2-specific parameters: SETTINGS frame, stream priorities, pseudoheader order. Browsers have consistent
HTTP/2 behavior; automation tools often differ.

### What It Detects

- **SETTINGS frame fingerprint**: Chrome, Firefox, Safari have distinct patterns
- **Pseudoheader order**: Standard order vs non-standard
- **Stream priority usage**: Browsers use it, many bot libraries don't
- **WINDOW_UPDATE behavior**: Absence indicates simple/bot client
- **Server Push support**: Modern browsers support it, bots often disable

### Signals Raised

```csharp
h2.is_http2            : bool
h2.settings_fingerprint: string
h2.client_type         : string  // "Chrome", "Firefox", "Go_HTTP2_Client"
h2.pseudoheader_order  : string  // "method,path,authority,scheme"
h2.uses_priority       : bool
h2.window_update_pattern: string
h2.push_enabled        : bool
h2.preface_valid       : bool
```

### Production Integration

Requires HTTP/2-aware reverse proxy to capture SETTINGS frame and stream metadata:

```
X-HTTP2-Settings: 1:65536,2:0,3:1000,4:6291456,6:262144
X-HTTP2-Pseudoheader-Order: method,path,scheme,authority
X-HTTP2-Stream-Priority: weight=256
X-HTTP2-Push-Enabled: 1
```

### Example Detection

```
User-Agent: Chrome/120...
HTTP/2 Settings: 3:100,4:65536 (Go HTTP2 Client pattern)
→ BROWSER MISMATCH → Bot detected with 70% confidence
```

## 4. Multi-Layer Correlation

### Concept

Cross-checks consistency across multiple detection layers. Legitimate browsers show perfect consistency; bots often have
layer mismatches.

### What It Analyzes

- **OS Correlation**: TCP TTL/window → User-Agent claimed OS
- **Browser Correlation**: HTTP/2 fingerprint → User-Agent claimed browser
- **TLS Correlation**: TLS version → User-Agent claimed browser version
- **Geographic Correlation**: IP geolocation → Accept-Language headers
- **IP-Browser Correlation**: Datacenter IP + browser User-Agent

### Signals Raised

```csharp
correlation.network_os         : string
correlation.claimed_os         : string
correlation.os_mismatch        : bool
correlation.h2_client          : string
correlation.claimed_browser    : string
correlation.browser_mismatch   : bool
correlation.tls_browser_mismatch: bool
correlation.ip_country         : string
correlation.primary_language   : string
correlation.geo_mismatch       : bool
correlation.consistency_score  : double  // 0.0-1.0
correlation.anomaly_count      : int
correlation.anomaly_layers     : string  // "OS,Browser,Geo"
```

### Detection Rules

- **1 mismatch**: 40% confidence (minor inconsistency)
- **2 mismatches**: 60% confidence (significant inconsistency)
- **3+ mismatches**: 85% confidence (highly suspicious)
- **Perfect consistency**: -25% confidence (strong human indicator)

### Example Detection

```
TCP: Linux (TTL=64)
User-Agent: Windows NT 10.0, Chrome 120
HTTP/2: Go HTTP2 Client
IP: AWS Datacenter
→ 3 MISMATCHES (OS, Browser, IP-Browser) → Bot detected with 85% confidence
```

## 5. Behavioral Waveform Analysis

### Concept

Analyzes temporal patterns across multiple requests to detect automation. Inspired by Zeek's multi-flow behavior
analysis and exfil detection patterns.

### What It Analyzes

- **Timing Regularity**: Too-regular intervals (CV < 0.15) = bot, natural variation (CV 0.3-2.0) = human
- **Request Rate**: >30 req/min = scraper, 10-30 req/min = suspicious
- **Path Diversity**: Low diversity (<30% unique) = scanning
- **Sequential Patterns**: /page/1, /page/2, /page/3 = automated crawling
- **Traversal Patterns**: Strict depth-first = crawler, mixed = human
- **User-Agent Changes**: Changing UA in same session = IP rotation/spoofing
- **Burst Detection**: >10 requests in 10 seconds = bot burst

### Signals Raised

```csharp
waveform.signature             : string  // Client identity hash
waveform.interval_mean         : double  // Average time between requests
waveform.interval_stddev       : double  // Standard deviation
waveform.timing_regularity_score: double // Coefficient of variation
waveform.burst_detected        : bool
waveform.burst_size            : int
waveform.path_diversity        : double  // Unique paths / total paths
waveform.sequential_pattern    : bool
waveform.traversal_pattern     : string  // "depth-first-strict", "mixed"
waveform.request_rate          : double  // Requests per minute
waveform.user_agent_changes    : int
waveform.session_duration_minutes: double
waveform.mouse_events          : int     // From client-side tracking
waveform.keyboard_events       : int
```

### Timing Analysis Math

```csharp
// Coefficient of Variation (CV) = stddev / mean
// CV < 0.15: Too regular (likely bot)
// CV 0.3-2.0: Natural human variation
// CV > 2.0: Very random (could be human or sophisticated bot)

intervals = [2.1s, 2.0s, 2.2s, 1.9s, 2.1s]  // Bot
mean = 2.06s, stddev = 0.11s
CV = 0.11 / 2.06 = 0.053 → TOO REGULAR → 70% bot confidence

intervals = [5s, 12s, 3s, 18s, 7s]  // Human
mean = 9s, stddev = 5.7s
CV = 5.7 / 9 = 0.63 → NATURAL VARIATION → -15% human indicator
```

### Example Detection

```
Request history (20 requests):
- Interval CV: 0.08 (highly regular)
- Request rate: 45/minute (high)
- Path pattern: /api/product/1, /api/product/2, ... /api/product/45 (sequential)
- Traversal: depth-first-strict
- No mouse events
→ SCRAPER PATTERN → Bot detected with 75% confidence
```

## Integration Architecture

### Reverse Proxy Requirements

For full functionality, configure your reverse proxy (nginx/HAProxy) to capture and forward network/protocol metadata:

```nginx
# nginx configuration example
location / {
    proxy_pass http://backend;

    # TLS fingerprinting
    proxy_set_header X-JA3-Hash $ssl_ja3;

    # TCP/IP fingerprinting
    proxy_set_header X-TCP-Window $tcp_info_snd_wnd;
    proxy_set_header X-TCP-TTL $realip_remote_ttl;

    # HTTP/2 metadata
    proxy_set_header X-HTTP2-Settings $http2_settings;
    proxy_set_header X-HTTP2-Pseudoheader-Order $http2_pseudoheader_order;
}
```

### Signal Flow

```
Request → Reverse Proxy → ASP.NET Core Application
            ↓
    Captures metadata
    (TLS, TCP, HTTP/2)
            ↓
    Forwards as headers
    (X-JA3-Hash, X-TCP-Window, etc.)
            ↓
    BlackboardOrchestrator
            ↓
    Wave 0: TLS/TCP/HTTP2 Contributors
            → Raise fingerprint signals
            ↓
    Wave 1: MultiLayerCorrelation
            → Cross-check consistency
            ↓
    Wave 2: BehavioralWaveform
            → Analyze temporal patterns
            ↓
    Policy Evaluation
            → Aggregate evidence
            → Determine action
```

## Performance Characteristics

Based on benchmarks with optimized BlackboardOrchestrator:

- **Latency**: +2-5μs per fingerprinting contributor (~24-30μs total)
- **Allocations**: +1-2KB per contributor (~43-48KB total per request)
- **GC Impact**: Gen0/Gen1 only (no Gen2 collections)
- **Throughput**: Maintains >30,000 requests/sec on Ryzen 9 9950X

The overhead is minimal because:

1. Most contributors run in parallel (Wave 0)
2. Signal extraction is optimized (pooled collections)
3. Fingerprint matching uses hash lookups (O(1))
4. Waveform analysis uses in-memory cache (fast)

## Security Considerations

### ThreatFox Integration (TODO)

The TLS contributor has a TODO to integrate with ThreatFox for known malicious JA3 fingerprints:

```
https://threatfox.abuse.ch/export/json/recent/
```

This will provide real-time threat intelligence for TLS fingerprints associated with malware, C2 frameworks, and other
threats.

### Privacy

- All signals use privacy-safe hashing (IP addresses masked in logs)
- No PII stored in fingerprints
- Waveform history expires after 30 minutes
- Complies with GDPR data minimization principles

## Future Enhancements

### Inspired by Zeek Frameworks

1. **File Analysis** (from Zeek's file framework)
    - Track MIME types per client
    - Detect "this client only downloads PDFs/ZIPs" patterns
    - Scraper/downloader detection

2. **Service Classification** (from Zeek's conn.log)
    - Detect protocols on non-standard ports
    - "HTTP-looking traffic on port 8443" = tunnel/proxy

3. **Exfiltration Detection** (from Salesforce Zeek module)
    - Aggregate byte counts across parallel connections
    - Detect chopped data streams
    - Multi-threaded data extraction

4. **WebSocket Analysis**
    - Zeek doesn't have native WebSocket support yet
    - Opportunity to be ahead: WebSocket fingerprinting
    - Detect automation over WS connections

## References

- **JA3 Fingerprinting
  **: [Salesforce Engineering Blog](https://engineering.salesforce.com/tls-fingerprinting-with-ja3-and-ja3s-247362855967)
- **p0f (Passive OS Fingerprinting)**: [lcamtuf.coredump.cx/p0f3](https://lcamtuf.coredump.cx/p0f3/)
- **HTTP/2 Fingerprinting**: [AKAMAI Research](https://www.akamai.com/blog/security/passive-fingerprinting-http2)
- **Zeek Documentation**: [docs.zeek.org](https://docs.zeek.org/)
- **ThreatFox**: [threatfox.abuse.ch](https://threatfox.abuse.ch/)

## Testing

See `Mostlylucid.BotDetection.Benchmarks` for performance tests and `Mostlylucid.BotDetection.Test` for integration
tests.

Run benchmarks:

```bash
cd Mostlylucid.BotDetection.Benchmarks
dotnet run -c Release
```

Expected results: ~24-30μs latency, ~43-48KB allocations, no Gen2 collections.
