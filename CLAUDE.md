# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**StyloBot** is an enterprise-grade bot detection and anonymous entity resolution framework for ASP.NET Core. It uses a blackboard architecture (via StyloFlow) with 57 contributors across 4 waves, real-time inference with <1ms fast path, intent classification with threat scoring, Leiden clustering for bot network discovery, and zero-PII design. The system combines fast-path detection with optional LLM enrichment (not decision-making) for edge cases. Sessions are the primary behavioral unit - compressed into 129-dimensional Markov chain vectors with unified fingerprint dimensions and per-transition timing anomaly detection, enabling inter-session velocity analysis and behavioral anomaly detection. **Metastable fingerprint identity** (6.4.7+, opt-in via `Identity:Enabled = true`) treats each visitor as a learned vector *shape* - centroid + per-fp weight vector + observation cloud - and uses a two-pass match (L1 IP+UA point lookup → L2 weighted-cosine via `IIdentityAnchorIndex`) so the fast path stays sub-ms for stable visitors while rotated identities still resolve to a single fingerprint. Drift verifier, calibration via Fisher discriminant ratios, and self-refining archetypes close the learning loop. See [`docs/architecture/fingerprint-match.md`](docs/architecture/fingerprint-match.md). **Anonymous Entity Resolution** progressively builds identity from multiple factors (IP+UA → TLS → HTTP/2 → client-side JS → behavioral patterns), discovers stable identity anchors per visitor (PersonalStability × GlobalRarity scoring), and detects rotation trails via cosine neighbor walking. Entity merge/split/rewind operations are backed by immutable session snapshots. Persistence uses SQLite everywhere (zero-dependency) for the FOSS product, with PostgreSQL as the commercial upgrade path (in the `stylobot-commercial` repo). The website/portal has been moved to `stylobot-commercial` as it depends on commercial packages. The real-time dashboard features session timeline visualization with Markov chain drill-in, behavioral shape radar charts (8-axis projection from 129-dim vectors), world threat map, traffic charts, country analytics, cluster visualization, threat scoring, deterministic bot naming, live signature feed, and Threats tab for CVE probe monitoring. All dashboard data persists to SQLite (no in-memory stores). **Simulation packs** (WordPress FOSS, others commercial) simulate vulnerable endpoints to detect CVE-targeting bots. The `UseStyloBot()` method provides single-call setup with correct middleware ordering.

## Critical Rules

- **NEVER add hard-coded site-specific exceptions, bypass keys, or allowlists.** StyloBot is a detection product - the fix is always to make detection *correct*, not to add workarounds. The live site (www.stylobot.net) runs the product as-is to test it.
- **The `X-SB-Api-Key` header** is part of the product's detection policy system (for customers to exempt their own monitoring/health-check traffic). It is NOT for operational use to bypass detection on the StyloBot site itself.
- **All detection improvements must be generic** - based on protocol specs (W3C Fetch Metadata, RFC 6455, etc.), not site-specific paths or domains.
- **NEVER use in-memory stores for persistence.** All state must persist to SQLite (FOSS) or PostgreSQL (commercial). `ConcurrentDictionary` is fine for per-request transient state and performance caches only. No `InMemory*Store` classes for anything that matters.
- **NEVER skip detection.** No skip paths, no logonly workarounds. Use `BotPolicyAttribute(BlockThreshold = 0.95)` for internal endpoints that need to be reachable by edge-case visitors.
- **Dashboard logins are unlimited.** The "users" limit in commercial tiers refers to protected identity policy overrides (`ConfigResolutionContext.UserId`), not dashboard seats.
- **Foundation contributors are not policy-gated.** Anything implementing `IFoundationContributor` runs unconditionally; policy filters classifiers only. Foundation = Compute (derive identity from the request: `Signature`, `TransportProtocol`, `PiiQueryString`, `IdentityVector`) + Match (look up prior knowledge keyed on identity: `FingerprintPrior`, `FastPathReputation`, `ContentSequence`, `FingerprintMatch`). Before adding a contributor, changing the signal merge in the orchestrator, or introducing a parallel store for an existing fact, read [`docs/architecture/signal-contracts.md`](docs/architecture/signal-contracts.md) and [`docs/architecture/fingerprint-match.md`](docs/architecture/fingerprint-match.md). Approval / Challenge / ClientSide are NOT foundation because they depend on prior round-trips. The BDF rig at `src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs` runs under `DetectionPolicy.Default` and asserts on the read surface; if you add a foundation signal, add a probe.

## Build Commands

```bash
# Build entire solution
dotnet build mostlylucid.stylobot.sln

# Build specific project
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj

# Run the full demo application (all 57 contributors + dashboard)
dotnet run --project src/Mostlylucid.BotDetection.Demo
# Visit: https://localhost:5001/SignatureDemo
# Dashboard: http://localhost:5080/_stylobot

# Run all tests
dotnet test

# Run specific test project
dotnet test src/Mostlylucid.BotDetection.Test/
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/

# Run single test file
dotnet test --filter "FullyQualifiedName~UserAgentDetectorTests"

# Run tests with coverage
dotnet test /p:CollectCoverage=true

# Run benchmarks
dotnet run --project src/Mostlylucid.BotDetection.Benchmarks -c Release

# Pack NuGet package
dotnet pack src/Mostlylucid.BotDetection -c Release
```

## Solution Structure

**Main Solution**: `mostlylucid.stylobot.sln`

| Project | Purpose |
|---------|---------|
| `Mostlylucid.BotDetection` | Core detection library (NuGet package) |
| `Mostlylucid.BotDetection.Api` | Public REST API for detection & dashboard data |
| `Mostlylucid.BotDetection.ApiHolodeck` | Honeypot responses, beacon tracking, holodeck coordinator |
| `Mostlylucid.BotDetection.UI` | Dashboard, TagHelpers, SignalR hub |
| `Mostlylucid.BotDetection.UI.PostgreSQL` | PostgreSQL persistence layer (in `stylobot-commercial` repo) |
| `Mostlylucid.BotDetection.Llm` | LLM abstraction (`ILlmProvider`, prompts, parsing) |
| `Mostlylucid.BotDetection.Llm.Ollama` | Ollama HTTP LLM provider |
| `Mostlylucid.BotDetection.Llm.LlamaSharp` | LlamaSharp in-process LLM provider |
| `Mostlylucid.BotDetection.Llm.Cloud` | Anthropic, OpenAI, Gemini LLM providers |
| `Mostlylucid.BotDetection.Llm.Holodeck` | LLM-powered dynamic honeypot response generation |
| `Mostlylucid.BotDetection.Llm.Tunnel` | GPU tunnel relay -route cloud LLM inference to a local GPU via Cloudflare tunnel |
| `Mostlylucid.BotDetection.Demo` | Dev test bench (controllers + test endpoints + mock LLM) |
| `Mostlylucid.BotDetection.Console` | `stylobot` gateway / proxy console (AOT, 35MB). `--enable-api` exposes `/api/v1/*` + SignalR hub at `/api/v1/hub` |
| `Mostlylucid.BotDetection.Sidecar` | `stylobot-sidecar` headless gRPC + REST detection (AOT, 37MB) |
| `Stylobot.Ui` | `stylobot-ui` dashboard host. Remote (REST viewer of a gateway) or local mode via `StyloBot:Source:Pull:Type`. Not AOT. |
| `Stylobot.All` | `stylobot-all` gateway + detection + dashboard in one process. Not AOT. |
| `Mostlylucid.BotDetection.Benchmarks` | YAML-driven BenchmarkDotNet harness |
| `Stylobot.Gateway` | Docker-first YARP reverse proxy |
| `Mostlylucid.GeoDetection` | Geographic routing (MaxMind, ip-api) |
| `Mostlylucid.GeoDetection.Contributor` | Geo enrichment for bot detection |
| `Mostlylucid.Common` | Shared utilities (caching, telemetry) |

**Test Projects**: `*.Test`, `*.Tests` - xUnit + Moq

**Website Solution**: Moved to `stylobot-commercial` repo (depends on commercial packages for portal/licensing)

## Architecture

### Blackboard Pattern (StyloFlow)

Detection uses an ephemeral blackboard where detectors write signals:
- `SignalSink` - In-memory signal store per request
- Raw PII (IP, UA) stays in `DetectionContext`, never on blackboard
- Signals use hierarchical keys: `request.ip.is_datacenter`, `detection.useragent.confidence`

### Detector Pipeline

**Identity (Priority 1)**: Signature (PrimarySignature computation + header hashes for progressive identity)

**Content Sequence (Priority 4, Wave 0)**: ContentSequence -tracks document→asset→API page-load order per fingerprint; writes `sequence.*` signals that gate 5 deferred detectors; detects machine-speed timing (<20ms), phase-window divergence, cache-warm, and expected SignalR; `CentroidSequenceStore` (SQLite) holds per-cluster expected chains; `EndpointDivergenceTracker` + `AssetHashMiddleware` suppress false positives during deploys

**Fast Path (<1ms)**: UserAgent, Header, Ip, SecurityTool, Behavioral, ClientSide, Inconsistency, VersionAge, Heuristic, FastPathReputation, CacheBehavior, CookieBehavior, ResourceWaterfall, ReputationBias, AiScraper, Haxxor, CveProbe, PiiQueryString, VerifiedBot, VerifiedBotInline, FediverseDomain, BrowserModeClassifier, CveFingerprint, HeuristicLate, ClaimedIdentity, ThreatIntel

**Slow Path (~100ms)**: ProjectHoneypot (DNS lookup)

**Advanced Fingerprinting**: TlsFingerprint (JA3/JA4), TcpIpFingerprint (p0f), Http2Fingerprint (AKAMAI), Http3Fingerprint (QUIC), MultiLayerCorrelation, BehavioralWaveform, ResponseBehavior, TransportProtocol, StreamAbuse

**Session Analysis**: SessionVector (Markov chain → 129-dim vector, partial chain archetypes at 3-5 requests, inter-session velocity), Periodicity (rotation cadence, temporal patterns via autocorrelation), ReactivePattern, Similarity, Cluster

**Entity Resolution**: Merge (cosine neighbor walking), Split (velocity oscillation), Convergence (parallel behavioral vectors), L0-L5 confidence levels, AccountTakeover, IdentityChange, GeoChange, PoolCollision

**Post-Round-Trip**: ChallengeVerification, FingerprintApproval, ClickFraud, Honeypot.EndpointHistory, Honeypot.HoneypotLink

**LLM Escalation**: Llm (enrichment only, not decision-making)

### Detector Benchmark Numbers

Measured via YAML-driven BenchmarkDotNet harness (`Mostlylucid.BotDetection.Benchmarks/Scenarios/*.benchmark.yaml`). Run: `dotnet run --project src/Mostlylucid.BotDetection.Benchmarks -c Release -- --filter '*DetectorBenchmarkRunner*'`

| Detector | Scenario | Mean | Allocated |
|----------|----------|------|-----------|
| Intent | Navigation | 2,341 ns | 5,448 B |
| Heuristic | Bot | 4,255 ns | 2,504 B |
| Heuristic | Human | 3,424 ns | 2,488 B |
| Behavioral | Normal | 1,446 ns | 2,112 B |
| Haxxor | SQL Injection | 1,306 ns | 1,608 B |
| Header | Bot (curl) | 507 ns | 1,520 B |
| Header | Human (Chrome) | 496 ns | 1,448 B |
| CacheBehavior | Normal | 1,335 ns | 1,400 B |
| Ip | Datacenter | 537 ns | 1,152 B |
| MultiLayerCorrelation | Full signals | 330 ns | 1,088 B |
| UserAgent | Googlebot | 829 ns | 1,072 B |
| AiScraper | GPTBot | 572 ns | 1,016 B |
| FastPathReputation | Cached signature | 308 ns | 928 B |
| UserAgent | Chrome | 2,058 ns | 928 B |
| Ip | Residential | 557 ns | 840 B |
| TransportProtocol | Document | 145 ns | 504 B |
| TlsFingerprint | Chrome/Bot | ~100-260 ns | 424 B |
| Inconsistency | TLS/UA mismatch | 135 ns | 376 B |
| CookieBehavior | With cookies | 33 ns | 184 B |
| Http2Fingerprint | Chrome | 120 ns | 152 B |
| HeaderCorrelation | Full headers | 50 ns | 104 B |
| Haxxor | Clean request | 204 ns | **0 B** |

### Session Vector Architecture

Sessions are the primary behavioral unit. Per-request Markov chain transitions are compressed into a fixed-dimension vector per session, enabling similarity search and inter-session anomaly detection.

**Vector dimensions (129 total):**
- `[0..99]` Markov transition probabilities (10 states × 10 states)
- `[100..109]` Stationary distribution (time spent in each state)
- `[110..117]` Temporal features (timing entropy, burst ratio, error rate, etc.)
- `[118..125]` Fingerprint features (TLS, HTTP protocol, TCP OS, headless, datacenter)
- `[126..128]` Transition timing features (per-transition timing anomaly scores)

**Markov states:** PageView, ApiCall, StaticAsset, WebSocket, SignalR, ServerSentEvent, FormSubmit, AuthAttempt, NotFound, Search

**Key concepts:**
- **Retrogressive session boundary:** Sessions are defined by inter-request gaps (default 30min), detected when the NEXT request reveals the gap - not by fixed time windows
- **Unified fingerprint dimensions:** TLS/TCP/H2 fingerprints are vector dimensions, so fingerprint mutation across sessions appears as velocity in those dimensions
- **Snapshot compaction:** Old session snapshots merge into a maturity-weighted root vector preserving the behavioral baseline while discarding per-session detail
- **Inter-session velocity:** L2 magnitude of the delta vector between consecutive sessions; high velocity = sudden behavioral shift (bot rotation, account takeover)

**Key files:**
- `Analysis/SessionVector.cs` - SessionStore, SessionVectorizer, FingerprintContext, snapshot compaction
- `Orchestration/ContributingDetectors/SessionVectorContributor.cs` - Detection contributor
- `Orchestration/Manifests/detectors/sessionvector.detector.yaml` - YAML config

### Persistence

**Core product (SQLite, zero-dependency):**
- `Data/SqliteSessionStore.cs` - ISessionStore implementation
- `Data/SessionPersistenceService.cs` - Background service bridging in-memory SessionStore events to SQLite
- Tables: `sessions` (vector + Markov chains), `signatures` (cumulative reputation), `buckets` (1-minute counters)
- ~100x compression vs per-request storage (200 sessions/day vs 10,000 requests/day)
- `Identity/SqliteFingerprintStore.cs` (6.4.7+) - separate `fingerprints.db` file for the metastable identity layer. Tables: `fingerprints` (centroid + per-fp weights + cached score), `fingerprint_keys` (primary_signature → fingerprint_id), `fingerprint_observations` (per-request vectors awaiting absorption), `fingerprint_corrections` (Pass-2-corrects-Pass-1 events), `identity_dimension_weights` (calibrated global weights), `identity_archetypes` (refined archetype centroids), `identity_vector_layout` (versioned dim layout). Dormant unless `Identity:Enabled = true`.

**Commercial (PostgreSQL + pgvector):**
- `Mostlylucid.BotDetection.UI.PostgreSQL` - PostgreSQL persistence (enterprise feature)
- Native HNSW indexing for sub-millisecond vector similarity queries at scale

### Key Files

- `Extensions/ServiceCollectionExtensions.cs` - DI registration entry points
- `Orchestration/BlackboardOrchestrator.cs` - Main detection orchestration
- `Orchestration/ContributingDetectors/` - All 57 contributor implementations
- `Orchestration/Manifests/detectors/*.yaml` - Detector configurations
- `Models/BotDetectionOptions.cs` - Configuration model
- `Actions/*.cs` - Response policies (block, throttle, challenge, redirect)

### Transport-Aware Detection

Detectors are aware of transport protocol context (API, SignalR, WebSocket, gRPC) to avoid false positives on non-document traffic. The `TransportProtocolContributor` (Priority 5) writes signals that downstream detectors consume:
- `transport.protocol_class` - document, api, signalr, grpc, static
- `transport.is_streaming` - WebSocket, SSE, SignalR
- `transport.is_upgrade` - WebSocket upgrade

Detectors that consume transport context: HeuristicFeatureExtractor (8 features), InconsistencyDetector, MultiLayerCorrelation, ResponseBehavior, AdvancedBehavioral, Header, CacheBehavior.

### Configuration Pattern

Detectors are configured via YAML manifests with appsettings.json overrides:

```json
{
  "BotDetection": {
    "BotThreshold": 0.7,
    "NonAiMaxProbability": 0.90,
    "DefaultActionPolicyName": "throttle-stealth",
    "EnableLlmDetection": true,
    "Detectors": {
      "UserAgentContributor": {
        "Weights": { "BotSignal": 2.0 }
      }
    }
  }
}
```

**Oscillation prevention:** `NonAiMaxProbability` (default 0.90) controls the probability ceiling when AI hasn't run. ConfirmedBad reputation patterns use longer decay tau (12h vs 3h) and wider demotion hysteresis (0.5 vs 0.9) to prevent block/allow flapping. Browser attestation downgrade is configurable via YAML (`browser_attestation_max_confidence`, `browser_attestation_weight`).

## Service Registration

```csharp
// Recommended: detection + dashboard, correct middleware ordering
builder.Services.AddStyloBot(dashboard => {
    dashboard.AllowUnauthenticatedAccess = true; // dev only
});
app.UseRouting();
app.UseStyloBot();  // broadcast → detection → dashboard, all wired correctly

// Detection only (no dashboard)
builder.Services.AddBotDetection();
app.UseBotDetection();

// User-agent only (minimal)
builder.Services.AddSimpleBotDetection();

// With LLM escalation (requires Ollama, default model: gemma4)
builder.Services.AddAdvancedBotDetection("http://localhost:11434", "gemma4");

// Remote dashboard viewer (Stylobot.Ui in rest mode): reads everything from a
// remote stylobot gateway's /api/v1/* + relays SignalR beacons into the local hub.
// The Remote* impls in UI/Adapters/Remote/ are registered BEFORE AddStyloBotDashboard
// so the TryAdd fallbacks (SqliteDashboardEventStore etc.) skip themselves.
builder.Services.AddStyloBotDashboardRemote(builder.Configuration);
builder.Services.AddStyloBotDashboard(builder.Configuration);
// Plus SignalRBeaconRelay HostedService for the live feed.
```

### Binary topology

- **`stylobot` (Console)** - edge gateway. Default surface is detection + reverse-proxy. `--enable-api` opts into the `/api/v1/*` REST surface + SignalR invalidation hub at `/api/v1/hub`. Fails fast if `StyloBot:ApiKeys` is empty when `--enable-api` is set. `-d` / `--daemon` shorthand for the existing `start` subcommand. `--output-config <file>` dumps the effective `BotDetectionOptions` for editing.
- **`stylobot-sidecar`** - headless gRPC + REST detection sidecar. App calls per-request; loopback hop.
- **`stylobot-ui` (`Stylobot.Ui`)** - dashboard host. `StyloBot:Source:Pull:Type = rest` (default) reads from a remote gateway; `local` runs detection + local SQLite store (typically only useful for development). `StyloBot:Source:Live:Type = signalr` connects to the gateway hub for live invalidation beacons; `none` is poll-only.
- **`stylobot-all` (`Stylobot.All`)** - YARP gateway + detection + dashboard in one process. Single-host topology.

## Key Patterns

### Zero-PII Architecture
- Raw IP/UA only in-memory, never persisted
- Signatures use HMAC-SHA256 hashing
- Blackboard contains only privacy-safe signals

### Action Policies
Separation of detection (WHAT) from response (HOW):
- `block` - HTTP 403
- `throttle-stealth` - Silent delay
- `throttle-tools` - HTTP 429 + Retry-After (delay-derived) + exponential backoff (for curl/wget/etc.)
- `throttle-status` - Fast HTTP 429 + fixed `Retry-After: 60` (informational). Orchestrator auto-routes friendly bot types (`SocialMediaBot`, `MonitoringBot`, `SearchEngine`, `GoodBot`, `VerifiedBot`) through this when probability > threshold and `ThreatScore < 0.55` - the fediverse link-preview stampede case. Set `TriggeredActionPolicyName` explicitly in a transition to opt out. Friendly set + threat-gate constant live in `Models/BotTypeClassification.cs`.
- `challenge` - CAPTCHA/proof-of-work
- `redirect-honeypot` - Trap redirect
- `logonly` - Shadow mode

### Naming surface
`FingerprintNameComposer` produces the display name. Two extension points worth knowing:
- **Per-instance discriminator**: `UserAgentDiscriminator.ExtractDiscriminator` pulls the hostname from the RFC 7231 `+https://host/` product-comment used by fediverse servers and some AI scrapers. Result is appended to the Priority 1 name (e.g. `Mastodon mastodon.social`). The vendor-home skiplist (openai.com, google.com, etc.) lives in `Definitions/VendorHomeHosts/vendor-home-hosts.yaml` as an embedded resource - edit the YAML, not the code.
- **Deceptive-claim marker**: When `VerifiedBotContributor` flags `verifiedbot.spoofed` or `verifiedbot.rdns_mismatch`, the composer appends `FingerprintNameComposer.SpoofedMarker` (` (!)`) to the name. Downstream consumers can filter on the constant.

### Daemon mode (production execution path)
The `stylobot` CLI runs **foreground by default** so demos and CI behave predictably, but production deployments must use daemon mode. Three equivalent invocations:
- `stylobot 5080 http://upstream -d` (short)
- `stylobot 5080 http://upstream --daemon`
- `stylobot start 5080 http://upstream`

Double-forks, writes a PID file, returns. `stylobot stop` SIGTERMs the running daemon; `stylobot status` checks the PID + hits `/health` (non-zero exit on missing daemon). systemd unit shape: `Type=forking` + `PIDFile=` + `ExecStart=stylobot ... -d`. Don't add `-d` to `docker run` - containers run foreground by design and the container runtime supervises.

### HttpContext Extensions
```csharp
context.IsBot()
context.GetBotConfidence()
context.GetBotType()
```

### Default posture: observe-only

The Gateway image ships with `BlockDetectedBots = false` and `DefaultActionPolicyName = "throttle-stealth"` for the pre-launch calibration window. Detection runs as normal; responses are delayed rather than refused. A pre-launch banner across the dashboard chrome (`130ebc0`) signals this state. Flipping to enforcement is one config change: set `BlockDetectedBots = true` and pick a non-throttle policy (`block` / `throttle-status` / `throttle-tools` / `challenge`). See [`action-policies.md`](src/Mostlylucid.BotDetection/docs/action-policies.md#default-posture-observe-only).

### Admin endpoints (off by default)

Two operator endpoints under `/stylobot/admin/` let you apply config changes without redeploying:
- `POST /admin/reload` triggers `IConfigurationRoot.Reload()`; `IOptionsMonitor` consumers see new values on next read.
- `POST /admin/restart` calls `IHostApplicationLifetime.StopApplication()` after flushing; the supervisor (Docker / systemd / launchctl) restarts the process.

Fail-closed: routing is unmapped unless `StyloBot:Dashboard:Admin:Enabled = true` AND a non-empty `Token` is configured. Bearer comparison is constant-time; attempts log at Warning with source IP. See [`docs/admin-endpoints.md`](docs/admin-endpoints.md).

### Edge-injected client signals (behind a reverse proxy)

When the gateway sits behind Cloudflare / Caddy / nginx / AWS ALB, the proxy-to-origin hop's protocol and TLS are not the client's. The detection pipeline reads injected headers first and falls back to `HttpContext.Request.*` only when none is present:

- `X-Client-HTTP-Version` (also accepts `Sb-Http-Version`)
- `X-Client-TLS-Version`, `X-Client-TLS-Cipher`, `X-Client-TLS-Ext-Sha1`
- `X-Client-ASN`

For JA3/JA4: `TlsFingerprintContributor` reads `X-JA3-Hash` and `X-JA3-String` from any edge that can compute and forward them (CF Bot Management Enterprise via Transform Rules, nginx `ssl_ja3` module, Caddy `ja3` plugin, HAProxy Lua, or `Stylobot.Gateway`'s own Kestrel TLS metadata capture). Single header name, source-agnostic. Recipes for each proxy in [`docs/REVERSE_PROXY_SIGNALS.md`](docs/REVERSE_PROXY_SIGNALS.md).

## Adding a New Detector

Every detector touches exactly 5 files. Use `Http3FingerprintContributor` as a reference implementation.

### 5-File Checklist

1. **C# class** - `Orchestration/ContributingDetectors/{Name}Contributor.cs`
   - Inherit `ConfiguredContributorBase` (for YAML config) or `ContributingDetectorBase` (for no-config detectors)
   - Constructor takes `ILogger<T>` + `IDetectorConfigProvider` and calls `base(configProvider)`
   - Override `Name` (string), `Priority` (int), `TriggerConditions` (empty array for Wave 0, or signal triggers for later waves)
   - Implement `ContributeAsync(BlackboardState state, CancellationToken)` returning `IReadOnlyList<DetectionContribution>`
   - Use `GetParam<T>(name, default)` for all tunable values - no magic numbers in code

2. **YAML manifest** - `Orchestration/Manifests/detectors/{name}.detector.yaml`
   - Follows the schema: `name`, `priority`, `enabled`, `scope`, `taxonomy`, `input`, `output`, `triggers`, `emits`, `defaults` (weights, confidence, timing, features, parameters)
   - The `*.yaml` glob in `.csproj` auto-includes it as an embedded resource

3. **SignalKeys** - `Models/DetectionContext.cs`
   - Add constants in the `SignalKeys` class grouped with a section header comment
   - Use hierarchical naming: `h3.protocol`, `h3.client_type`, etc.

4. **DI registration** - `Extensions/ServiceCollectionExtensions.cs`
   - Add `services.AddSingleton<IContributingDetector, {Name}Contributor>();` in the appropriate wave section

5. **Narrative builder** - `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`
   - Add entries to both `DetectorFriendlyNames` and `DetectorCategories` dictionaries

### Key Rules

- **No magic numbers** - all confidence, weight, and threshold values come from YAML `defaults.parameters` via `GetParam<T>()`
- **Always add signals to the last contribution** - the orchestrator reads signals from contributions; ensure the final contribution carries the full `signals.ToImmutable()` dictionary
- **Cross-detector communication** - use `TriggerConditions` (e.g., `SignalExistsTrigger`, `AnyOfTrigger`, `AllOfTrigger`) to declare dependencies, and `GetSignal<T>(state, key)` to read signals from earlier detectors
- **Use helper methods** - `BotContribution()`, `HumanContribution()`, `NeutralContribution()`, `StrongBotContribution()` from `ConfiguredContributorBase`

## Versioning

Uses MinVer with tag prefix `allbot-v{version}`. NuGet packages auto-version from git tags.

## Target Frameworks

.NET 10.0

## External Dependencies (Local Project References)

The solution uses local project references for development. Related repos that must be cloned as siblings:

```
D:\Source\
├── mostlylucid.stylobot\     # This repo
├── styloflow\                # StyloFlow.Core, StyloFlow.Retrieval.Core
└── mostlylucid.atoms\        # mostlylucid.ephemeral and atoms
```

**From StyloFlow** (`D:\Source\styloflow\`):
- `StyloFlow.Core` - Manifest-driven component configuration
- `StyloFlow.Retrieval.Core` - Signal/analysis wave framework

**From Ephemeral** (`D:\Source\mostlylucid.atoms\mostlylucid.ephemeral\`):
- `mostlylucid.ephemeral` - Core signal sink and coordination
- `mostlylucid.ephemeral.atoms.taxonomy` - DetectionLedger, DetectionContribution, IDetectorAtom
- `mostlylucid.ephemeral.atoms.keyedsequential` - Keyed sequential processing
- `mostlylucid.ephemeral.atoms.slidingcache` - Sliding window cache

**NuGet Packages**:
- **OllamaSharp** - LLM integration (optional)
- **YamlDotNet** - Manifest parsing
- **MathNet.Numerics** - Statistical analysis

## Dashboard

Session-centric dashboard at `/_stylobot` with:
- **Sessions tab** - Timeline of behavioral sessions with Markov chain previews, HTMX drill-in
- **Session detail** - Behavioral radar chart (ApexCharts), transition bar visualization, paths visited
- **Visitors/Top Bots** - Signature-level views with sparklines
- **Countries/Endpoints** - Geographic and path-level aggregations
- **Clusters** - Leiden community detection with diagnostics
- **User Agents** - UA family breakdown with version distribution

**API endpoints:** `/api/sessions`, `/api/sessions/recent`, `/api/sessions/signature/{id}`, `/api/detections`, `/api/summary`, `/api/timeseries`, `/api/clusters`, `/api/countries`, `/api/endpoints`, `/api/topbots`, `/api/me`, `/api/diagnostics`, `/api/export`

## Public API & SDKs

**Canonical REST API** (`Mostlylucid.BotDetection.Api`) at `/api/v1/*` - the foundation for all SDK clients.

**Auth tiers:** Tier 1 (proxy headers, zero-latency), Tier 2 (`X-SB-Api-Key` for detection + read), Tier 3 (OIDC bearer for management, commercial).

**Key endpoints:** `POST /api/v1/detect`, `POST /api/v1/detect/batch`, `GET /api/v1/detections`, `/summary`, `/timeseries`, `/signatures`, `/countries`, `/endpoints`, `/topbots`, `/threats`, `/me`. OpenAPI spec at `/api/v1/openapi.json`.

**Gateway header injection:** `X-StyloBot-IsBot`, `X-StyloBot-Probability`, `X-StyloBot-Confidence`, `X-StyloBot-BotType`, `X-StyloBot-BotName`, `X-StyloBot-RiskBand`, `X-StyloBot-Action`, `X-StyloBot-ThreatScore`, `X-StyloBot-ThreatBand`, `X-StyloBot-Policy`.

### Node SDK

Two npm packages in `sdk/node/`:
- **`@stylobot/core`** - Zero-dep types, `StyloBotClient`, header parser. Works in Node/Deno/Bun.
- **`@stylobot/node`** - Express middleware (`styloBotMiddleware`), Fastify plugin (`styloBotPlugin`).

Two modes: `headers` (behind Gateway, zero-latency) or `api` (sidecar, calls `POST /api/v1/detect`).

```bash
# Build
cd sdk/node && npm install && npm run build --workspaces

# Test
cd sdk/node/packages/core && node --experimental-strip-types --test src/__tests__/*.test.ts
cd sdk/node/packages/node && node --experimental-strip-types --loader ../../ts-loader.mjs --test src/__tests__/*.test.ts
```

### Holodeck (Honeypot Response System)

Three-layer architecture for serving fake responses to bots hitting honeypot paths:

1. **`HoneypotPathTagger`** (pre-detection middleware) - tags honeypot paths on `HttpContext.Items` before detection runs. Solves the early-exit bypass: `FastPathReputation` can no longer kill the holodeck.
2. **`HolodeckCoordinator`** - one engagement slot per fingerprint, global cap of 10. Overflow gets normal 403.
3. **`SimulationPackResponder`** - serves fake responses from simulation packs. Dynamic templates use `IHolodeckResponder` (LLM generation); static templates use `{{nonce}}` canary placeholders.

**Beacon tracking:** `BeaconCanaryGenerator` embeds HMAC canaries in fake responses. `BeaconContributor` (priority 2) scans incoming requests for canary replay. Match links rotated fingerprints via `beacon.original_fingerprint` signal.

**Capability-aware:** `AddLlmHolodeck()` registers `IHolodeckResponder`. Nodes without it serve static templates. No hard dependency on LLM being available.

Core interfaces in `Mostlylucid.BotDetection/SimulationPacks/`: `IHolodeckResponder`, `ICanaryGenerator`, `IBeaconStore`.

### Benchmark Harness

YAML-driven BenchmarkDotNet harness in `Mostlylucid.BotDetection.Benchmarks/Scenarios/*.benchmark.yaml`.

```bash
dotnet run --project src/Mostlylucid.BotDetection.Benchmarks -c Release -- --filter '*DetectorBenchmarkRunner*'
dotnet run --project src/Mostlylucid.BotDetection.Benchmarks -c Release -- --list-scenarios
dotnet run --project src/Mostlylucid.BotDetection.Benchmarks -c Release -- --regression  # CI mode
```

## Production Architecture

```
Internet → Cloudflare Tunnel → Caddy (TLS) → YARP Gateway (bot detection) → Website
                                            → Website (direct for /_stylobot* / SignalR)
```

- **Gateway** (`Stylobot.Gateway`) - YARP reverse proxy with all 57 contributors, no dashboard
- **Website** (`mostlylucid.stylobot.website`) - ASP.NET Core MVC + dashboard UI + SignalR hub
- **Caddy** routes `/_stylobot*` directly to website (bypasses gateway for SignalR WebSocket)
- **PostgreSQL** - Dashboard event persistence (commercial); SQLite for core product (FOSS)
- **Ollama** - Local LLM for AI bot classification escalation

Config: `mostlylucid.stylobot.website/docker-compose.local.yml`

## Documentation

Detailed docs in `Mostlylucid.BotDetection/docs/`:
- `quickstart.md` - Getting started with zero dependencies
- `integration-levels.md` - Five integration levels from minimal to YARP gateway
- `blocking-and-filters.md` - All bot type allow flags, geo/network blocking
- `signals-and-custom-filters.md` - Signal access API, custom filters, GeoDetection integration
- `action-policies.md` - Block, Throttle, Challenge, Redirect, LogOnly responses
- `configuration.md` - Full options reference
- `ai-detection.md` - Heuristic model and LLM escalation
- `learning-and-reputation.md` - Adaptive learning system
- `identity-fingerprint-match.md` (6.4.7+) - Metastable fingerprint identity layer (two-pass match, drift, calibration)
- `fingerprint-verdict-cache.md` - Per-signature verdict cache and gate
- `yarp-integration.md` - Reverse proxy setup

Architecture specs in `docs/architecture/`:
- `signal-contracts.md` - Foundation vs classifier contract; signal merge rules
- `fingerprint-match.md` - Metastable identity full design (storage, vector composition, learning loop)