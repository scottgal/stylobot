# TCP/IP Fingerprint Detection

Wave: 0 (Fast Path)
Priority: 11

## Purpose

p0f-style passive OS fingerprinting via TCP/IP stack characteristics. Analyzes TCP window sizes, TTL values, TCP options, MSS, IP fragmentation patterns, and connection reuse behavior to detect automation tools and OS mismatches.

## Signals Emitted

| Signal Key | Type | Description |
|------------|------|-------------|
| `tcp.window_size` | int | TCP window size |
| `tcp.ttl` | int | Time To Live value |
| `tcp.options_pattern` | string | TCP options fingerprint (e.g., "MSS,SACK,TS,WS") |
| `tcp.os_hint` | string | OS hint from IP ID patterns (Windows/Linux/BSD) |
| `tcp.os_hint_ttl` | string | OS hint from TTL (Linux: 64, Windows: 128) |
| `tcp.os_hint_window` | string | OS/client hint from window size |
| `tcp.mss` | int | Maximum Segment Size |
| `tcp.has_timestamp` | bool | TCP timestamp option present |
| `tcp.has_sack` | bool | Selective ACK option present |
| `tcp.has_window_scale` | bool | Window scaling option present |
| `tcp.modern_options` | bool | Modern TCP features present |
| `tcp.connection_header` | string | keep-alive vs close |
| `ip.dont_fragment` | bool | DF flag set |
| `ip.id_pattern` | string | Sequential vs random IP ID |
| `http.pipelining_supported` | bool | HTTP pipelining detected |

## Detection Logic

**Known bot TCP window sizes:**
- 4096 (Go HTTP client)
- 65536 (Go custom)
- 32768 (Python/cURL)
- 87380 (Python default)
- 65535 (Java/.NET/Node/Scrapy)
- 1, 512, 1024 (suspicious/malformed)

**TTL analysis:**
- 64: Linux/macOS (normal)
- 128: Windows (normal)
- 255: Network device
- 1, 2, 10, 30, 100, 200: Suspicious/non-standard

**Cross-correlation:** Window size + TTL + options pattern compared against known fingerprint database to detect OS spoofing.

## Performance

Typical execution: <1ms (header parsing only).
Requires reverse proxy to forward TCP-level headers (X-TCP-Window-Size, X-IP-TTL, etc.).
