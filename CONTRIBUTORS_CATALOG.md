# Bot Detection Contributors Catalog

This document catalogs ALL 24+ contributing detectors in the bot detection system and what behaviors/signals they analyze.

## Core Fast-Path Contributors (Wave 0)

### 1. **FastPathReputationContributor**
- **What it detects:** Pattern reputation from learned signatures
- **Signals:** Known bad/good patterns from historical data
- **Speed:** Instant (cache lookup)
- **Can abort early:** Yes (ConfirmedBad patterns)

### 2. **UserAgentContributor**
- **What it detects:** User-Agent string analysis
- **Signals:**
  - Known bot signatures (Googlebot, curl, python-requests, scrapy)
  - Version age (too old or suspiciously new)
  - Missing/incomplete UA strings
  - Inconsistent browser/OS combinations
  - Generic patterns (^\\w+/[\\d\\.]+$)
- **Speed:** Fast (regex matching)

### 3. **SecurityToolContributor**
- **What it detects:** Security testing tools
- **Signals:**
  - Burp Suite, ZAP, Metasploit signatures
  - Penetration testing tools
  - Vulnerability scanners
- **Speed:** Fast (pattern matching)

### 4. **HeaderContributor**
- **What it detects:** HTTP header completeness and consistency
- **Signals:**
  - Missing Accept headers
  - Missing Accept-Language
  - Missing Accept-Encoding
  - Unusual header combinations
  - Missing modern browser headers (Sec-Fetch-*)
- **Speed:** Fast

### 5. **IpContributor**
- **What it detects:** IP address reputation and characteristics
- **Signals:**
  - Datacenter IP ranges (AWS, Azure, GCP, DigitalOcean)
  - Known proxy/VPN services
  - Tor exit nodes
  - IP reputation scores
  - ASN analysis (hosting vs residential)
- **Speed:** Fast (IP range lookups)

## Behavioral Contributors (Wave 1)

### 6. **BehavioralContributor**
- **What it detects:** Request timing and rate patterns
- **Signals:**
  - Request velocity (requests/second)
  - Time between requests
  - Burst patterns
  - Session duration anomalies
- **Speed:** Medium (session analysis)

### 7. **AdvancedBehavioralContributor**
- **What it detects:** Advanced behavioral patterns
- **Signals:**
  - Path sequence analysis
  - Navigation patterns (depth-first vs breadth-first)
  - Backtracking behavior
  - Sequential ID scraping detection
- **Speed:** Medium

### 8. **BehavioralWaveformContributor**
- **What it detects:** Temporal waveform patterns
- **Signals:**
  - Request timing waveforms
  - Periodic/rhythmic patterns (bots)
  - Natural variance (humans)
  - Sleep patterns (day/night cycles)
- **Speed:** Medium (time-series analysis)

### 9. **CacheBehaviorContributor**
- **What it detects:** HTTP caching behavior
- **Signals:**
  - If-Modified-Since usage
  - ETag validation
  - Cache-Control respect
  - 304 Not Modified handling
  - Bots often ignore caching
- **Speed:** Fast

### 10. **ResponseBehaviorContributor**
- **What it detects:** How client responds to server responses
- **Signals:**
  - 404 handling (humans stop, bots continue)
  - Redirect following patterns
  - Rate limiting respect (429 responses)
  - Error recovery behavior
- **Speed:** Fast

## Network Fingerprinting Contributors (Wave 1-2)

### 11. **TlsFingerprintContributor**
- **What it detects:** TLS/SSL handshake fingerprints
- **Signals:**
  - JA3 hash (TLS fingerprint)
  - Cipher suite preferences
  - Extension order
  - TLS version
  - Matches known browsers or bots
- **Speed:** Fast (connection-level)

### 12. **Http2FingerprintContributor**
- **What it detects:** HTTP/2 protocol fingerprints
- **Signals:**
  - SETTINGS frame order and values
  - PRIORITY frames
  - WINDOW_UPDATE patterns
  - Stream handling
- **Speed:** Fast (connection-level)

### 13. **TcpIpFingerprintContributor**
- **What it detects:** TCP/IP stack fingerprints
- **Signals:**
  - TTL (Time To Live) values
  - TCP window sizes
  - TCP options order
  - MTU values
  - OS fingerprinting
- **Speed:** Fast (connection-level)

## Client-Side Contributors (Wave 1)

### 14. **ClientSideContributor**
- **What it detects:** Browser-side JavaScript execution
- **Signals:**
  - Canvas fingerprint
  - WebGL fingerprint
  - Screen resolution
  - Timezone
  - Plugins
  - Touch support
  - Battery API
  - Device memory/cores
  - Headless browser detection
- **Speed:** Slow (requires JS callback)
- **Requires:** Client-side JavaScript injection

## Content & Path Contributors (Wave 1-2)

### 15. **HoneypotLinkContributor** (ApiHolodeck plugin)
- **What it detects:** Clicks on honeypot trap links
- **Signals:**
  - Hidden links (CSS display:none)
  - Links in robots.txt Disallow
  - Invisible links (same color as background)
  - Only bots click these
- **Speed:** Fast
- **Can abort early:** Yes (instant bot)

## Heuristic Contributors (Wave 2)

### 16. **HeuristicContributor**
- **What it detects:** General heuristic patterns
- **Signals:**
  - User-Agent + Headers consistency
  - Referer chain validity
  - Cookie acceptance
  - JavaScript execution capability
- **Speed:** Medium

### 17. **HeuristicLateContributor**
- **What it detects:** Late-stage heuristics after other detectors
- **Signals:**
  - Cross-signal correlation
  - Conflict resolution
  - Confidence refinement
- **Speed:** Medium
- **Runs:** After other contributors

## Geo-Location Contributors (Wave 1)

### 18. **GeoContributor**
- **What it detects:** Geographic location analysis
- **Signals:**
  - IP geolocation
  - Accept-Language vs location mismatch
  - Timezone vs location mismatch
  - Unusual geo patterns (rapid location changes)
- **Speed:** Fast (IP lookup)

### 19. **GeoClientContributor**
- **What it detects:** Client-reported geo vs IP geo
- **Signals:**
  - Browser timezone from JavaScript
  - Comparison with IP-based geo
  - VPN/proxy detection
- **Speed:** Slow (requires JS)

## Reputation & Correlation Contributors (Wave 1-2)

### 20. **ReputationBiasContributor**
- **What it detects:** Adjusts confidence based on reputation
- **Signals:**
  - Historical patterns for this IP/UA
  - Prior bot/human classifications
  - Reputation decay over time
- **Speed:** Fast (cache lookup)

### 21. **MultiLayerCorrelationContributor**
- **What it detects:** Cross-layer signal correlation
- **Signals:**
  - TLS fingerprint + User-Agent consistency
  - IP geo + Accept-Language consistency
  - HTTP/2 + browser version consistency
  - Client-side fingerprint + UA consistency
- **Speed:** Medium
- **Runs:** After multiple contributors

## External API Contributors (Wave 2-3)

### 22. **ProjectHoneypotContributor**
- **What it detects:** IP reputation via Project Honeypot API
- **Signals:**
  - Known spam IPs
  - Known harvesters
  - Known comment spammers
  - Threat score
- **Speed:** Slow (external API call)
- **Requires:** API key

## AI-Powered Contributors (Wave 3 - AI Path)

### 23. **LlmContributor**
- **What it detects:** LLM-based analysis of request patterns
- **Signals:**
  - Natural language analysis of:
    - User-Agent reasonableness
    - Request path intent
    - Header combinations
    - Overall request "smell"
  - Context-aware decision making
- **Speed:** Very slow (LLM inference)
- **Requires:** Local Ollama or API key
- **Models supported:** Ollama (gemma2, llama3, etc.)

### 24. **OnnxContributor** (if exists)
- **What it detects:** ML model-based classification
- **Signals:**
  - Feature vector classification
  - Trained on historical bot/human data
  - Multi-feature correlation
- **Speed:** Medium (ONNX inference)

## Detection Signals Summary

### Total Detection Dimensions: 10+ categories

1. **Request Timing** (3 contributors)
   - Velocity, bursts, waveforms, sleep patterns

2. **Path Patterns** (2 contributors)
   - Navigation sequences, honeypots, crawling patterns

3. **Headers** (3 contributors)
   - Completeness, consistency, modern browser signals

4. **User-Agent** (2 contributors)
   - Signatures, version age, consistency

5. **Client-Side Execution** (2 contributors)
   - JavaScript fingerprints, device capabilities

6. **IP & Network** (5 contributors)
   - Geo, datacenter detection, TLS/HTTP2/TCP fingerprints

7. **Behavioral** (4 contributors)
   - Timing, navigation, caching, error handling

8. **Cache & State** (1 contributor)
   - HTTP caching behavior

9. **Reputation** (2 contributors)
   - Historical patterns, learned signatures

10. **AI Analysis** (1-2 contributors)
    - LLM reasoning, ONNX classification

## Policy-Based Activation

Different policies enable different contributors:

- **Demo Policy:** ALL contributors (for demonstration)
- **Fast Policy:** UserAgent, Header, IP, FastPathReputation only
- **Strict Policy:** All non-AI contributors
- **Static Policy:** Minimal (just UserAgent + FastPath for CSS/JS requests)
- **Learning Policy:** Enables feedback loop to PatternReputation cache

## Wave-Based Execution

Contributors run in waves based on trigger conditions:

- **Wave 0:** Fast-path contributors (always run)
  - UserAgent, SecurityTool, FastPathReputation, IP, Header

- **Wave 1:** Triggered by fast-path suspicion
  - Behavioral, ClientSide, Geo, Cache, Response, TLS/HTTP2/TCP fingerprints

- **Wave 2:** Triggered by accumulated suspicion
  - Heuristics, AdvancedBehavioral, Correlation, ProjectHoneypot

- **Wave 3:** AI escalation (high suspicion)
  - LLM, ONNX

This layered approach balances accuracy with performance.
