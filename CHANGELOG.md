# Changelog

All notable changes to StyloBot are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [5.0.0] - 2026-02-22

### Added

#### Intent Classification and Threat Scoring
- **IntentContributor** — new Wave 3 detector that classifies request intent (reconnaissance, exploitation, scraping, benign, etc.) using HNSW-backed similarity search and cosine vectorization
- **Threat scoring orthogonal to bot probability** — a human probing `.env` files has low bot probability but high threat score; both dimensions are now independently surfaced
- **ThreatBand enum** — `None`, `Low`, `Elevated`, `High`, `Critical` with configurable score thresholds (0.15 / 0.35 / 0.55 / 0.80)
- **IntentClassificationCoordinator** — orchestrates intent vectorization, similarity search, and threat band assignment
- **HnswIntentSearch** — HNSW approximate nearest-neighbor index for real-time intent matching with configurable M/efConstruction/efSearch parameters
- **IntentVectorizer** — converts request features (path patterns, method, headers) into dense vectors for similarity search
- **IntentLearningHandler** — feeds confirmed intent classifications back into the HNSW index for adaptive improvement
- Intent signals: `intent.category`, `intent.threat_score`, `intent.threat_band`, `intent.confidence`, `intent.similarity_score`, `intent.nearest_label`

#### Dashboard Threat Visualization
- Threat badges on detection detail, "your detection" panel, visitor list rows, and cluster cards
- Cluster enrichment: `DominantIntent` (most common intent) and `AverageThreatScore` per cluster
- Narrative enhancement: threat qualifier prefix on bot narratives (`CRITICAL THREAT:`, `High-threat`, `Elevated-threat`)
- `intent.*` signals in dedicated "Intent / Threat" signal category with target icon
- `threatBandClass()` helper for DaisyUI badge coloring by threat band
- Threat data in all API endpoints: `/api/detections`, `/api/signatures`, `/api/topbots`, `/api/clusters`, `/api/me`, `/api/diagnostics`
- Threat data in CSV export
- Threat data in SignalR real-time broadcasts

#### Stream and Transport Detection
- **StreamAbuseContributor** — new Wave 1 detector that catches attackers hiding behind streaming traffic using per-signature sliding window tracking
- Stream abuse patterns: connection churn, payload flooding, protocol switching, rapid reconnection
- `stream-abuse.detector.yaml` manifest with configurable thresholds for all abuse patterns
- Enhanced **TransportProtocolContributor** — improved WebSocket, SSE, SignalR, gRPC, and GraphQL classification with `transport.is_streaming` signal for downstream consumption
- Five existing detectors now consume `transport.is_streaming` to suppress false positives on legitimate streaming traffic (CacheBehavior, BehavioralWaveform, AdvancedBehavioral, ResponseBehavior, MultiFactorSignature)
- Documentation: [`stream-transport-detection.md`](Mostlylucid.BotDetection/docs/stream-transport-detection.md)

#### Detection Accuracy Improvements
- Enhanced **BehavioralWaveformContributor** — stream-aware burst thresholds, excludes streaming requests from page rate calculations
- Enhanced **CacheBehaviorContributor** — skips cache validation for streaming requests entirely
- Enhanced **AdvancedBehavioralContributor** — skips path entropy, navigation pattern, and burst analysis for streaming
- Enhanced **ResponseBehaviorContributor** — new signals for response analysis
- Updated response behavior, transport protocol, and stream abuse detector YAML manifests
- **PolicyEvaluator** improvements — threat-aware policy evaluation
- **DetectionPolicy** updates — new policy fields for threat-based responses

#### Infrastructure
- New `HttpContext` extension methods for intent/threat access
- `BotCluster` enrichment with `DominantIntent` and `AverageThreatScore`
- `BotClusterService` computes cluster-level intent and threat aggregates
- `ILearningEventBus` extensions for intent learning feedback
- `DetectionLedgerExtensions` — threat band computation from aggregated evidence
- `DetectionContribution` — `ThreatBand` enum and threat fields on `AggregatedEvidence`
- Updated `BotDetectionOptions` with intent detection configuration
- Updated `ServiceCollectionExtensions` with intent detector registration

### Changed

- Dashboard now shows 30 detectors (was 29) — IntentContributor added to Wave 3
- Default `EnabledDetectorCount` increased to 30
- Cluster visualization includes threat percentage and dominant intent
- Bot narratives include threat qualifier prefix for elevated+ threats
- Diagnostics endpoint now includes `ThreatScore`/`ThreatBand` on detections, signatures, and top bots

### Fixed

- Missing `threatBandClass` function in inline Razor dashboard script (NuGet package users would have gotten a JS ReferenceError)
- Missing `Critical` threshold (>= 0.80) in cluster threat badge ternary
- Visitor row threat badge missing DaisyUI `badge` class (visual rendering was inconsistent)
- Removed dead `threatBandColor` function from dashboard.ts

### Documentation

- [`dashboard-threat-scoring.md`](Mostlylucid.BotDetection/docs/dashboard-threat-scoring.md) — full architecture, data flow, API endpoints, UI elements, security considerations
- [`stream-transport-detection.md`](Mostlylucid.BotDetection/docs/stream-transport-detection.md) — stream-aware detection architecture, transport classification, abuse patterns
- [`transport-protocol-detection.md`](Mostlylucid.BotDetection/docs/transport-protocol-detection.md) — updated with streaming classification
- Updated `SESSION_SUMMARY.md` with v5 section

---

## [4.0.0] - 2026-01-25

### Added

- Programmatic request attestation via `Sec-Fetch-Site` headers
- YARP API key passthrough for upstream services
- BDF (Bot Detection Format) export/replay system
- Standardized signal key usage across all contributors

## [3.0.0] - 2025-12-15

### Added

- Real-time dashboard with interactive world map
- Country analytics and reputation tracking
- Cluster visualization (Leiden algorithm)
- User agent breakdown with category badges
- Live signature feed with risk bands and sparklines
- SignalR-based live updates
- Server-side rendering for initial dashboard load

## [2.0.0] - 2025-10-01

### Added

- Wave-based detection pipeline (4 waves)
- Protocol-level fingerprinting (JA3/JA4, p0f, AKAMAI, QUIC)
- Heuristic AI model with ~50 features per request
- Action policies (block, throttle, challenge, redirect, logonly)
- Training data API for ML export
- PostgreSQL/TimescaleDB persistence layer

## [1.0.0] - 2025-07-01

### Added

- Initial release with 20 detectors
- Blackboard architecture via StyloFlow
- Zero-PII design with HMAC-SHA256 signatures
- YARP reverse proxy integration
- Basic dashboard
