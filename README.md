# StyloBot

> TOTALLY FREE TO USE - Commercial addons coming in June 2026! 

**Self-hosted bot detection and anonymous entity resolution for ASP.NET Core.** 49 detectors across 4 waves, sub-millisecond inference, progressive identity that survives rotation. One binary. No cloud scoring dependency.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.botdetection)](https://www.nuget.org/packages/mostlylucid.botdetection)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](https://unlicense.org/)

> **This repo is the FOSS product.** Full detection engine, dashboard, entity resolution, simulation packs. The [commercial product](https://stylobot.net) uses the same engine with enterprise add-ons (see [FOSS vs Commercial](#foss-vs-commercial)).

---

## Why StyloBot

Cloud-based bot services work until your attacker adapts. When a sophisticated scraper learns to look like a user from your IP allow-list, a scoring API that doesn't know your application's normal behaviour has nothing to go on.

StyloBot runs in your process. It knows what a real page-load sequence looks like on *your* site: document, then asset burst in 80-500ms, then API calls, then optionally SignalR. It knows the timing signatures of your real users' sessions compressed into 129-dimensional Markov chain vectors. It tracks identity across rotation attempts using cosine similarity walks across fingerprint neighbours. All of this runs in ~150µs per request on commodity hardware, with no network call and no PII leaving your server.

---

## Quick start

**macOS (Homebrew)**
```bash
brew install scottgal/stylobot/stylobot
stylobot 5080 http://localhost:3000
```

**Linux (apt - Debian/Ubuntu)**
```bash
curl -1sLf 'https://dl.cloudsmith.io/public/mostlylucid/stylobot/setup.deb.sh' | sudo bash
sudo apt update && sudo apt install stylobot
stylobot 5080 http://localhost:3000
```

**Linux (manual / ARM64)**
```bash
# Download from GitHub Releases: stylobot-linux-x64.tar.gz or stylobot-linux-arm64.tar.gz
tar xzf stylobot-linux-x64.tar.gz && chmod +x stylobot && sudo mv stylobot /usr/local/bin/
stylobot 5080 http://localhost:3000
```

**Docker**
```bash
docker run --rm -p 8080:8080 -e DEFAULT_UPSTREAM=http://host.docker.internal:3000 \
  scottgal/stylobot-gateway:latest
```

**NuGet (embed as ASP.NET Core middleware)**
```bash
dotnet add package mostlylucid.botdetection
dotnet add package mostlylucid.botdetection.ui
```

```csharp
builder.Services.AddStyloBot(dashboard => {
    dashboard.AllowUnauthenticatedAccess = true; // dev only
});

app.UseRouting();
app.UseStyloBot();   // broadcast, detection, dashboard: correct ordering guaranteed
app.MapControllers();
```

Dashboard at `/_stylobot`. Detection at `~150µs` per request from first request.

---

## Core capabilities

- **Content sequence detection**: tracks the natural document/asset/API page-load order per fingerprint. Bots hitting APIs directly, or at machine speed (<20ms inter-request), diverge from the expected human chain and get flagged. Centroid freshness suppresses false positives during deploys by detecting ETag changes and divergence rate spikes
- **129-dim session vectors**: Markov chain transition probabilities + timing entropy + protocol fingerprint dimensions, all in one vector. Partial chain archetypes detect bots at 3-5 requests before full session maturity. L2 velocity between consecutive sessions catches rotation and account takeover
- **Anonymous entity resolution**: builds progressive identity (L0 to L5) from IP+UA, TLS, HTTP/2, client-side JS, and behavioural patterns. Merge/split/rewind operations backed by immutable snapshots. Rotation creates a trail of near-miss cosine neighbours that get linked back to the same actor
- **Leiden clustering**: groups signatures into bot networks by behavioural similarity. HNSW graph for sub-millisecond approximate nearest-neighbour search. Emergent bot clusters surface when new attack patterns are still unlabelled
- **Simulation packs**: honeypots that look like real products (WordPress 5.9 + 8 CVE modules). Bots that hit them get engaged by the holodeck with HMAC-canary-embedded fake responses. Canary replay links rotated fingerprints back to the original actor
- **Local GPU tunnel**: route LLM inference from a cloud instance to a local GPU via `stylobot llmtunnel` + Cloudflare tunnel. HMAC-SHA256 per-request signing, 30s TTL nonces, loopback-only listener
- **Zero PII**: HMAC-SHA256 hashed signatures. Raw UAs stored PII-stripped. No raw IPs persisted. Blackboard signals are privacy-safe keys, never raw data
- **Headless framework naming**: identifies Puppeteer, Playwright, Selenium, PhantomJS by name from timing and API surface, not UA string

---

## Architecture

### Detector pipeline: 49 detectors, 4 waves

```
Request -> Wave 0 (< 1ms)          -> Wave 1 (behavioral)    -> Wave 2 (AI)         -> Verdict
           Signature (identity)       Session vectors,          Heuristic model,      Bot probability
           UA, Header, IP,            Periodicity, Cookies,     Intent scoring,       Risk band
           TLS/TCP/H2/H3,             Resource waterfall,       Cluster detection,    Action policy
           Transport, Haxxor,         CVE probes, Waveform      LLM escalation        Entity resolution
           ContentSequence
```

| Layer | Detectors | What it catches |
|-------|-----------|-----------------|
| **Identity** | Signature, HeaderCorrelation, Periodicity | UA rotation, identity factors, temporal patterns |
| **Protocol** | TLS (JA3/JA4), TCP/IP (p0f), HTTP/2, HTTP/3, Transport, StreamAbuse | Spoofed browser fingerprints, protocol inconsistencies |
| **Behavioral** | Waveform, SessionVector, AdvancedBehavioral, CacheBehavior, CookieBehavior, ResourceWaterfall, ContentSequence | Timing patterns, Markov chains, missing assets, page-load sequence divergence |
| **Content** | UserAgent, Header, AiScraper, Haxxor, SecurityTool, VersionAge | Known bots, attack payloads, impossible browser versions |
| **Network** | IP, GeoChange, ResponseBehavior, MultiLayerCorrelation, CveProbe | Datacenter IPs, impossible travel, CVE scanning, cross-layer mismatches |
| **Intelligence** | FastPathReputation, ReputationBias, TimescaleReputation, Cluster, Similarity, Intent | Historical reputation, Leiden clustering, HNSW similarity, threat scoring |
| **Ad Fraud** | ClickFraud, PiiQueryString | IAB SIVT: datacenter/VPN/headless on paid traffic, referrer spoofing, immediate bounce |
| **AI** | Heuristic, HeuristicLate, LLM | 50-feature model (<1ms), optional LLM for ambiguous cases |
| **Client** | ClientSide, FingerprintApproval, ChallengeVerification | JS timing probes, headless detection, PoW challenges |

### Identity model

Each visitor builds a progressive identity across requests:

```
L0: IP + UA hash (immediate)
L1: TLS fingerprint correlation
L2: HTTP/2 frame signature
L3: Client-side JS probes (Canvas, WebGL, audio context)
L4: Behavioural pattern matching (session vector cosine similarity)
L5: Confirmed human (challenge solved or approved fingerprint)
```

Rotation is detected by walking cosine neighbours in the HNSW graph. If a "new" fingerprint lands within distance 0.15 of a known bad actor, it inherits reputation, even if IP, UA, and TLS all changed.

### Session vectors

Sessions compress into 129-dimensional vectors:

```
[0..99]   Markov transition probabilities (10 states x 10 states)
[100..109] Stationary distribution (time in each request state)
[110..117] Temporal features (timing entropy, burst ratio, error rate, ...)
[118..128] Fingerprint dimensions (TLS, HTTP protocol, TCP OS, headless, datacenter, ...)
```

States: `PageView, ApiCall, StaticAsset, WebSocket, SignalR, ServerSentEvent, FormSubmit, AuthAttempt, NotFound, Search`

Fingerprint mutation (new TLS JA3, new HTTP/2 settings) shows up as velocity in dimensions 118-128; the same L2 delta that catches behavioural rotation also catches protocol rotation.

### Content sequence detection

Real browsers follow a predictable request sequence after a page load. StyloBot tracks this per fingerprint with four time-phase windows:

| Phase | Window | Expected states |
|-------|--------|-----------------|
| Critical | 0-500ms | StaticAsset, PageView |
| Mid | 500ms-2s | StaticAsset, ApiCall, PageView |
| Late | 2s-30s | ApiCall, SignalR, WebSocket, SSE |
| Settled | 30s+ | ApiCall, SignalR, SSE |

Divergence score = machine-speed timing + unexpected state for phase + high request volume. Threshold: 0.4. When 40%+ of sessions on an endpoint diverge within a 1-hour window, the centroid is marked stale, suppressing false positives during deploys rather than flagging your own users.

### Privacy model

```
Raw request  ->  HMAC-SHA256  ->  PrimarySignature  ->  blackboard signals
     |                                                         |
Never persisted                                      Privacy-safe keys only
(IP, raw UA)                                         (no IP, no raw UA, no body)
```

Blackboard is ephemeral per-request. Signals are hierarchical keys (`request.ip.is_datacenter`, `sequence.diverged`). Raw PII stays in `DetectionContext`, never written to signals.

---

## Why it's different

| | StyloBot | Cloud scoring APIs |
|---|---|---|
| Latency | ~150µs in-process | 20-200ms network round-trip |
| Privacy | No data leaves your server | Request metadata sent to third party |
| Explainability | Full signal trace per request | Black-box score |
| Customisation | YAML manifests, per-endpoint policy overrides | Limited or none |
| Continuity | Works if internet is down | Fails open or closed |
| Cost model | Fixed (your hardware) | Per-request or per-seat |
| Context | Knows your site's normal patterns | Generic baselines |

---

## Use cases

- **Web scraping**: sequence divergence catches scrapers that skip the asset burst and jump straight to API endpoints; UA + TLS mismatch catches headless frameworks claiming to be Chrome
- **Credential stuffing**: velocity detection via inter-session L2 distance; session vector clustering groups attack waves by shared behavioural signature even when IPs rotate
- **API abuse**: no document request means no sequence context, so the full deferred detector stack always runs; machine-speed timing detected regardless of IP
- **Click fraud**: dedicated `ClickFraudContributor` scores IAB SIVT patterns - datacenter/VPN/headless on paid-ad landings, referrer spoofing, immediate bounce fraud; UTM and click-ID signals extracted and hashed by `PiiQueryStringContributor` before any PII reaches the blackboard
- **Automated account creation**: client-side fingerprinting detects missing JS APIs (canvas, WebGL, audio) and Puppeteer/Playwright named by timing characteristics
- **CVE probing**: simulation packs serve fake vulnerable endpoints; canary-embedded responses link probe attempts to the same actor across IP rotation

---

## LLM providers

Detection works fully without any LLM. LLM enriches bot names and handles ambiguous cases at the edge of the heuristic model's confidence range.

```bash
stylobot 5080 http://localhost:3000 --llm ollama           # local (default: gemma4)
stylobot 5080 http://localhost:3000 --llm openai --llm-key sk-...
stylobot 5080 http://localhost:3000 --llm anthropic --llm-key sk-ant-...

# Route cloud LLM inference to a local GPU
stylobot llmtunnel                                          # on GPU machine, prints connection key
stylobot 5080 http://localhost:3000 --llm localtunnel --llm-key "sb_llmtunnel_v1_..."
```

| Provider | Default model | Cost |
|----------|---------------|------|
| `ollama` | gemma4 | Free (local) |
| `openai` | gpt-4o-mini | ~$0.15/1M tokens |
| `anthropic` | claude-haiku-4-5 | ~$0.25/1M tokens |
| `gemini` | gemini-2.0-flash | Free tier |
| `groq` | llama-3.3-70b | Free tier |
| `localtunnel` | your local model | Free (`Mostlylucid.BotDetection.Llm.Tunnel`) |

---

## Dashboard

Real-time monitoring at `/_stylobot`. All data persists to SQLite.

- **Overview**: top threats, traffic chart, world threat map
- **Visitors**: signature-level cards with probability badges (Bot/Suspicious/Uncertain/Human)
- **Sessions**: Markov chain timeline with behavioral radar and session playback
- **Threats**: CVE probe feed, honeypot engagements, severity badges
- **Clusters**: Leiden community detection visualization
- **User Agents**: family breakdown, version distribution, full-text search
- **Configuration**: Monaco YAML editor (read-only in FOSS)

---

## FOSS vs Commercial

Two products, same detection engine. FOSS is complete for detection, entity resolution, and the dashboard. The [commercial product](https://stylobot.net) adds enterprise operational features via DI; gateways run unmodified FOSS detection.

### What's in FOSS (this repo)

- All 49 detectors, same pipeline as commercial
- Anonymous entity resolution (merge/split/rewind, L0-L5 confidence)
- Real-time dashboard (Overview, Visitors, Sessions, Threats, Clusters, User Agents, Configuration)
- Session vectors, Markov chains, behavioral radar charts
- Simulation packs (WordPress 5.9 with 8 CVE modules)
- SQLite persistence (zero external dependencies)
- Local GPU tunnel for LLM inference routing
- BDF replay testing
- CLI binary (6 platforms)
- Docker gateway (YARP reverse proxy)
- Optional LLM enrichment (any provider)
- Public REST API + Node.js SDK

### What commercial adds

**Persistence & scale:** PostgreSQL + pgvector, Redis cross-gateway cache and pub/sub config reload, TimescaleDB retention

**Fleet management:** multi-gateway coordination, fleet dashboard, leader election, Kubernetes Helm chart

**Live configuration:** forms-based detector config editor with hot-reload, per-endpoint policy overrides, config audit trail

**Identity & access:** Keycloak + Ed25519 JWT license validation, OIDC/SAML SSO, protected identity policies

**Reporting:** scheduled threat intelligence digests, webhook alerting, data retention controls

**Additional packs:** Django, Rails, Laravel, Spring Boot, Strapi, Shopify simulation packs; identity graph explorer

**License model:** capability-based JWT tiers. If a license expires, the system reverts to FOSS mode: detection continues, PostgreSQL falls back to SQLite, config editor goes read-only. No downtime.

---

## Repo layout

```
Mostlylucid.BotDetection/              Core detection library (NuGet)
Mostlylucid.BotDetection.UI/           Dashboard + SignalR hub (NuGet)
Mostlylucid.BotDetection.Api/          Public REST API
Mostlylucid.BotDetection.Llm.Tunnel/   GPU tunnel relay
Mostlylucid.BotDetection.Console/      Standalone CLI (6 platforms)
Stylobot.Gateway/                       Docker YARP reverse proxy
test-bdf-scenarios/                     BDF replay test scenarios
docs/                                   Architecture + specs
```

## Documentation

- [Quick start](Mostlylucid.BotDetection/docs/quickstart.md)
- [Configuration reference](Mostlylucid.BotDetection/docs/configuration.md)
- [Integration levels](Mostlylucid.BotDetection/docs/integration-levels.md)
- [Action policies](Mostlylucid.BotDetection/docs/action-policies.md)
- [Content sequence detection](Mostlylucid.BotDetection/docs/content-sequence-detection.md)
- [Centroid freshness](Mostlylucid.BotDetection/docs/centroid-freshness.md)
- [Click fraud detection](Mostlylucid.BotDetection/docs/click-fraud-detection.md)
- [Local GPU tunnel](Mostlylucid.BotDetection/docs/local-llm-tunnel.md)
- [CHANGELOG](CHANGELOG.md)

## Requirements

- .NET 10.0 (building from source)
- No external dependencies for FOSS (SQLite is embedded)
- Commercial: PostgreSQL, optional Redis

## License

[The Unlicense](https://unlicense.org/) FOSS core is public domain. Commercial features licensed separately.
