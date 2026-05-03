# Changelog

All notable changes to the Mostlylucid.BotDetection package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [6.0.0-alpha] - 2026-04-21

### Breaking Changes

- **Unified Signature System**: `WaveformSignature` deprecated (`[Obsolete]`). All detectors use `PrimarySignature` (HMAC-SHA256) via blackboard. Session store keyed by PrimarySignature. Existing session data will not match new keys - sessions re-accumulate automatically.
- **Default LLM**: `qwen3:0.6b` → `gemma4`. All defaults consolidated in `LlmDefaults` class (single source of truth).
- **Risk scoring**: Changed from max-single-detector to weighted average with multi-signal corroboration. Prevents single detector from producing VeryHigh risk.
- **IP reputation**: Removed IP-range as standalone reputation key. Reputation anchors on PrimarySignature (HMAC of IP+UA), not raw IP subnet. Prevents cross-contamination (e.g., curl traffic poisoning browser traffic from same IP).

### Added - Anonymous Entity Resolution

The identity system learns who keeps coming back, even when they rotate their fingerprint.

- **Entity graph** with SQLite persistence (`entities`, `entity_edges` tables)
- **Merge**: Cosine neighbor detection links rotated fingerprints to the same entity (similarity > 0.85)
- **Split**: Oscillation detection via lag-2 autocorrelation identifies two actors sharing one fingerprint
- **Convergence**: Pairwise behavioral similarity detection flags related entities (similarity > 0.92)
- **Rewind**: Post-merge divergence detection undoes bad merges from immutable session snapshots
- **L0-L5 confidence levels**: Infrastructure → Browser Guess → Transport → Runtime → Behavioral → Persistent Actor
- **Rotation cadence estimation**: Detects systematic rotation via low velocity variance
- **EntityResolutionService**: Background service (60s interval) for merge/split/rotation analysis

### Added - Progressive Identity

- **HeaderHashCollector**: HMAC hashes of discriminatory HTTP headers per session (sec-ch-ua, accept-language, sec-fetch-*, header ordering pattern)
- **StabilityAnalyzer**: PersonalStability × GlobalRarity scoring - stable-for-this-visitor AND rare-globally = strong identity anchor
- **SignatureContributor** (Priority 1): Computes PrimarySignature + header hashes, writes to blackboard before all other detectors
- All identity data stored per session for retroactive stability analysis

### Added - New Detectors (14 new, 45 total)

- **SignatureContributor**: Unified identity computation (Priority 1, Wave 0)
- **PeriodicityContributor**: Temporal pattern analysis - fixed-interval polling, cron schedules, hour-of-day entropy, rotation cadence detection via autocorrelation
- **CveProbeContributor**: CVE vulnerability probe detection via simulation packs
- **PiiQueryStringContributor**: Detects PII patterns in query strings (email, tokens, credentials)
- **CookieBehaviorContributor**: Tracks cookie acceptance across requests - bots using HTTP libraries ignore Set-Cookie entirely
- **ResourceWaterfallContributor**: Document-to-asset ratio analysis - 50 HTML fetches + 0 CSS/JS = definitive bot
- **SimulationPackResponder**: IActionPolicy that serves fake responses from simulation packs
- **JS Execution Timing**: DOM layout timing, setTimeout drift, performance.now() resolution probes - catches Puppeteer/Playwright stealth
- **Headless framework identification**: Names specific automation (Puppeteer, Playwright, Selenium, PhantomJS, Nightmare) instead of "Unknown Bot"

### Added - Simulation Pack Framework

- **SimulationPack** models: packs, honeypot paths, response templates, CVE modules, timing profiles, LLM response hints
- **SimulationPackLoader**: Loads YAML pack definitions from embedded resources
- **WordPress 5.9 Pack** (FOSS): 11 honeypot paths, 7 response templates, 8 CVE modules (CVE-2024-6386 WPML RCE, CVE-2023-2982 miniOrange, etc.)
- CVE probe signals feed threat intelligence for prioritization over time

### Added - Dashboard

- **Threats tab**: CVE probe feed, severity badges, honeypot status indicator, real-time SignalR updates
- **Session-first layout**: Behavioral sessions promoted as primary view, raw requests collapsed
- **Live session radar**: Real-time behavioral shape from write-through cache, radar playback animation
- **Inter-session velocity/drift**: Drift column in sessions table, high behavioral shift highlighted
- **Fingerprint Profile card**: 8 fingerprint dimension placeholders
- **Probability badges**: Bot/Suspicious/Uncertain/Human based on probability (not binary)
- **Friendly action names**: throttle-stealth → "Silent Throttle", logonly → "Monitor Only"
- **Config editor security**: `EnableConfigEditing` + `WriteAuthorizationFilter` required. Default DENY. Monaco editor read-only when not authorized.

### Added - Infrastructure

- **`UseStyloBot()`**: Single method for detection + dashboard with correct middleware ordering. Broadcast wraps detection so blocked requests are always recorded.
- **`AddStyloBot()`**: Registers both detection and dashboard services.
- **LlmDefaults**: Single constant class for default model, endpoint, timeouts (no more duplicated "qwen3:0.6b" across 10+ files)
- **Thinking-aware LLM**: `EnableThinking` configurable per-options and per-request. Ollama provider captures thinking content separately from classification JSON.
- **Proxy/CDN header config**: `proxy-headers.yaml` with 10 providers (Cloudflare, AWS, Akamai, Fastly, etc.) - detection headers, real IP headers, country headers
- **Partial Markov chains**: 5 behavioral archetypes score first 3-5 requests before full session maturity

### Changed - Zero Magic Numbers

- All 12 remaining detectors migrated from hardcoded weights to YAML-configurable parameters via `ConfiguredContributorBase`
- 38 YAML manifests for 45 detectors (4 don't need YAML by design)
- Every weight, confidence delta, and threshold is now tunable via `appsettings.json` or YAML override
- Priority collisions resolved, stale YAML trigger references updated

### Fixed

- **Reputation poisoning**: IP-range reputation removed - curl traffic no longer poisons browser traffic from same IP
- **Blocked requests invisible**: `UseStyloBot()` middleware ordering ensures broadcast wraps detection, blocked 403s recorded in dashboard
- **Nullable ThreatScore crash**: SQLite parameter binding for nullable `double?` fields
- **Visitor cache not populated**: `visitorListCache.Upsert()` moved before `ExcludeLocalIpFromBroadcast` check
- **strip-pii ActionMarker**: Was incorrectly set to "mask-pii"
- **Orphaned ai.detector.yaml**: Deleted (no matching AiContributor class)
- **BrowserFingerprint analyzer tests**: Updated for headless framework naming

### Performance

- Full pipeline (all 45 detectors): ~140µs per request, ~175KB allocation
- Fast path detectors: 9-550ns per detector
- Signature computation (HMAC + header hashes): ~8µs, 11KB allocation
- 33 individual detector benchmarks with BenchmarkDotNet

## [3.0.0] - 2025-02-15

### Added - Major Features

#### Clustering & Community Detection
- **Leiden clustering** with configurable resolution for bot network discovery
  - Replaced Label Propagation with native C# Leiden algorithm (CPM quality function)
  - Optional semantic embeddings (384-dim ONNX all-MiniLM-L6-v2) blended with heuristic features
  - Configurable via `BotDetection:Cluster:Algorithm` (`leiden` or `label_propagation`)
  - Semantic similarity weight: configurable blend (default 40% embedding cosine + 60% heuristic)

- **LLM-based cluster descriptions** (GraphRAG-style)
  - Background service generates creative names and descriptions for detected bot networks
  - Analyzes behavior, country, ASN, timing patterns, path diversity
  - Live SignalR updates push descriptions to dashboard as they're generated
  - Uses qwen3:0.6b by default (async, never blocks detection pipeline)
  - Graceful fallback to heuristic labels when Ollama unavailable
  - Enable via `BotDetection:Cluster:EnableLlmDescriptions=true`

#### New Detectors (7 new, 26 total)
- **TimescaleReputationContributor**: Time-series IP/signature reputation with automatic PostgreSQL fallback
- **ClusterContributor**: Multi-request bot network detection using FFT-based spectral analysis
- **SimilarityContributor**: Fuzzy signature matching via HNSW or Qdrant vector search
- **BehavioralWaveformContributor**: Spectral fingerprinting of request timing patterns
- **CacheBehaviorContributor**: HTTP cache header interaction analysis
- **ResponseBehaviorContributor**: Bot-specific response handling pattern detection
- **TcpIpFingerprintContributor**: p0f-style passive OS fingerprinting

#### Infrastructure
- **Qdrant vector database** integration for semantic similarity search
  - Dual-vector support: 64-dim heuristic + 384-dim semantic
  - Auto-migration from file-backed HNSW index
  - gRPC client with health checks
  - Docker Compose integration

- **ONNX embedding provider** for local ML inference
  - CPU-quantized all-MiniLM-L6-v2 model (~22MB)
  - Pure CPU inference (~1-5ms per embedding)
  - Auto-download from HuggingFace on first use

- **TimescaleDB** support with automatic PostgreSQL fallback
  - Continuous aggregates for time-series reputation data
  - Graceful degradation when TimescaleDB extension unavailable

- **Real client IP detection** via forwarded headers
  - X-Forwarded-For, X-Real-IP, CF-Connecting-IP support
  - Configurable trust depth for proxy chains

#### Dashboard
- **Bot Clusters widget** with live SignalR updates
  - Cluster cards show type, member count, bot probability, country
  - LLM-generated descriptions appear live as they're generated
  - Description updates via `BroadcastClusterDescriptionUpdate` SignalR event

### Changed

#### Model Updates
- **Default LLM**: gemma3:4b -> **qwen3:0.6b** (3x faster, 32K context)
  - Disabled internal reasoning mode (`Think = false`) for JSON output
  - Lighter weight (0.6B vs 4B params)

#### De-techify Pass
- All detector reasons converted to human-friendly language
  - "User-Agent header indicates bot software" -> "Browser identifies as a known bot"
  - "TLS JA3 fingerprint mismatch" -> "Browser fingerprint doesn't match its claimed identity"
  - "Request timing coefficient of variation below threshold" -> "Makes requests with robotic timing precision"

#### Clustering Algorithm
- Label Propagation -> **Leiden** (configurable, label_propagation still available)
- Hand-crafted 12-dim features -> **Blended semantic + heuristic** (configurable)
- Static heuristic labels -> **LLM-generated descriptions** (with heuristic fallback)

### Security
- **Zero-PII semantic embeddings**: Raw IP/UA never in embedding text, only derived features
- **Deterministic cluster IDs**: SHA256-based, no random seeds
- **Privacy-safe LLM prompts**: Only aggregated behavioral stats, no raw request data

### Migration Notes

#### New Configuration Options
```json
{
  "BotDetection": {
    "Cluster": {
      "Algorithm": "leiden",
      "EnableSemanticEmbeddings": true,
      "SemanticWeight": 0.4,
      "LeidenResolution": 1.0,
      "EnableLlmDescriptions": false,
      "DescriptionModel": "qwen3:0.6b",
      "DescriptionEndpoint": "http://localhost:11434"
    }
  }
}
```

#### Docker Compose
- Add Qdrant service (optional): `docker compose up -d qdrant`
- Qdrant collection auto-created on first use

### Performance
- Clustering with semantic embeddings: +1-5ms per signature (384-dim ONNX inference)
- LLM cluster descriptions: ~200ms per cluster (async background, does not block detection)
- Leiden vs Label Propagation: comparable O(E) complexity, better community quality

---

## [1.5.0] - 2024-12-05

### Added

- **SecurityToolContributor** - Detects penetration testing tools (SQLMap, Nikto, Nmap, Burp Suite, Acunetix, etc.)
- **ProjectHoneypotContributor** - HTTP:BL IP reputation lookups via DNS with caching
- **HeuristicLateContributor** - Post-AI refinement layer that runs after LLM for final classification
- **Honeypot test mode** - Use `<test-honeypot:harvester|spammer|suspicious>` markers for testing without real DNS
  lookups
- **Security scanner patterns** - Auto-fetched from digininja/scanner_user_agents and OWASP CoreRuleSet
- **Demo enhancements** - Interactive bot simulator with 20+ preconfigured bot types, custom UA input, honeypot buttons
- **Integration tests** - Production security defaults verification, honeypot simulation tests, contributor registration
  tests
- **New documentation** - security-tools-detection.md, project-honeypot.md, updated ai-detection.md with HeuristicLate

### Changed

- **Default LLM model** upgraded to `gemma3:4b` for better reasoning accuracy
- **LLM prompt** improved for better accuracy with smaller models
- **Localhost IP detection** fixed - no longer incorrectly flagged as datacenter IP
- **Detection results** flow downstream only via `HttpContext.Items` by default

### Security

- **ResponseHeaders.Enabled** defaults to `false` (never leak detection details to clients)
- **EnableTestMode** defaults to `false` (test mode must be explicitly enabled)
- **IncludeFullJson** defaults to `false` (never expose full detection JSON)

### New Signal Keys

- `security_tool.detected`, `security_tool.name`, `security_tool.category`
- `honeypot.checked`, `honeypot.listed`, `honeypot.threat_score`, `honeypot.visitor_type`
- `honeypot.days_since_activity`, `HoneypotTestMode`

---

## [1.0.0] - 2024-12-04

### Added

- **Stable release** - Production-ready bot detection middleware
- **Heuristic AI Provider** - Sub-millisecond classification with continuous learning
- **Composable Action Policies** - Separate detection (WHAT) from response (HOW)
- **Multi-signal detection** - User-Agent, headers, IP ranges, behavioral analysis, client-side fingerprinting
- **Stealth responses** - Throttle, challenge, or honeypot bots without revealing detection
- **Auto-updated threat intel** - isbot patterns and cloud IP ranges
- **Full observability** - OpenTelemetry traces and metrics

### Changed

- **Default LLM model** changed from `gemma3:1b` to `gemma3:4b` for better reasoning accuracy
- **Default LLM timeout** increased from 2000ms to 15000ms for larger model and cold start
- **LLM orchestrator timeout** fixed - LlmContributor now uses 2x `AiDetection.TimeoutMs` (was using default 2s)
- **Improved LLM error logging** - clear messages for timeout, model not found (404), and server errors (500)
- **Improved LLM prompt** - stricter JSON-only output format to reduce parsing failures
- **Heuristic provider** is now the recommended AI provider (replaces ONNX)
- Simplified LLM prompt to prevent small model hallucinations
- Localhost IP detection improved - no longer incorrectly flagged as datacenter IP

### Removed

- ONNX provider removed in favor of Heuristic provider (faster, no external dependencies)

### Migration Guide

If upgrading from preview versions:

1. Replace `"Provider": "Onnx"` with `"Provider": "Heuristic"` in configuration
2. Update Ollama model if using LLM escalation: `gemma3:4b` recommended
3. Default `TimeoutMs` is now 15000ms (15s) to handle cold start - adjust if needed

---

## [0.5.0-preview2] - 2024-11

### Added

- Composable Action Policy System
- Named action policies: block, throttle, challenge, redirect, logonly
- `[BotAction("policy-name")]` attribute for endpoint overrides
- IActionPolicyFactory for configuration-based creation
- IActionPolicyRegistry for runtime policy lookup

---

## [0.5.0-preview1] - 2024-11

### Added

- Policy-Based Detection with named policies
- Path-based resolution with glob patterns
- Built-in policies: default, strict, relaxed, allowVerifiedBots
- Policy transitions based on risk thresholds
- Management endpoints: MapBotPolicyEndpoints()
- `[BotPolicy("strict")]` attribute for controllers
- `[BotDetector("UserAgent,Header")]` for inline detection
- `[SkipBotDetection]` to bypass detection
- Response headers and TagHelpers
- Blackboard architecture with event-driven detection
- Pattern reputation system with time decay
- Fast/Slow path execution model

---

## [0.0.5-preview1] - 2024-10

### Added

- Client-Side Fingerprinting with BotDetectionTagHelper
- Signed token system prevents spoofing
- Headless browser detection
- Inconsistency detection for UA/header mismatches
- RiskBand enum (Low, Elevated, Medium, High)
- Session-level behavioral analysis

---

## [0.0.4-preview1] - 2024-10

### Added

- ONNX-based detection (1-10ms latency)
- Source-generated regex for performance
- OpenTelemetry metrics integration
- YARP reverse proxy integration

---

## [0.0.3-preview2] - 2024-10

### Fixed

- Security fixes (ReDoS, CIDR validation)

---

## [0.0.3-preview1] - 2024-10

### Changed

- Documentation improvements

---

## [0.0.2-preview1] - 2024-10

### Added

- Background updates
- SQLite storage

---

## [0.0.1-preview1] - 2024-10

### Added

- Initial release
- Basic bot detection middleware
- User-Agent analysis
- Header inspection
- IP-based detection