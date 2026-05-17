# StyloBot

***COMING SOON***
Current versions are in development. With version 7 (due early June 2026) this will stabilize (read fewer releases). 

## STATUS
With version 6.x these are the stabilisation versions - IT IS LIKELY THEY WILL BREAK - as I work through this. StyloBot is now *feature complete* and I'm now stabilising it with a view to a June 1st RTM

The website https://www.stylobot.net will also be updated at that time (right now it's VERY old and unstable). 

[PLEASE REPORT ANY BUGS IN ISSUES ](https://github.com/scottgal/stylobot/issues)

## RELEASE ARTICLES
This is the series of articles on my blog about StyloBot 

[StyloBot Release Series: Behaviour, Not Identity](https://www.mostlylucid.net/blog/stylobot-fingerprint)

[StyloBot Release Series: Behaviour-Aware ASP.NET UI](https://www.mostlylucid.net/blog/behaviour-aware-ux)

[StyloBot Release Series: Finding and Fixing Unbounded Growth in Long-Running .NET Services](https://www.mostlylucid.net/blog/stylobot-release-reliability)

[StyloBot Release Series: Behaviour-Aware TypeScript UI ](https://www.mostlylucid.net/blog/typescript-sdk)

[StyloBot Release Series: The Sidecar Architecture](https://www.mostlylucid.net/blog/typescript-sdk)

[StyloBot Release Series: Learning to Get Faster](https://www.mostlylucid.net/blog/stylobot-release-learning)

And NUMEROUS others in the coming weeks covering all of StyloBot's features at release. (These will also form the basis of a lucidSupport AI support system ;)) 

**Bot detection that knows your site.** Cloud scoring services evaluate your traffic against generic baselines trained on other people's users. StyloBot learns what normal looks like on your specific application: the document-asset-API request sequence, the timing distribution of your real users, the session shape your checkout flow produces. Bots that adapt to evade a cloud service still diverge from those patterns.

Runs in your own infrastructure: in-process ASP.NET Core middleware, standalone YARP gateway proxy, or sidecar detection API. 49 detectors, <150µs per request, no PII leaves your server.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.botdetection)](https://www.nuget.org/packages/mostlylucid.botdetection)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](https://unlicense.org/)

> **This repo is the FOSS product.** Full detection engine, dashboard, entity resolution, simulation packs. The [commercial product](https://stylobot.net) uses the same engine with enterprise add-ons (see [FOSS vs Commercial](#foss-vs-commercial)).

---

## Why StyloBot

Cloud-based bot services work until your attacker adapts. When a sophisticated scraper learns to look like a user from your IP allow-list, a scoring API that doesn't know your application's normal behaviour has nothing to go on.

StyloBot runs in your own infrastructure. It knows what a real page-load sequence looks like on *your* site: document, then asset burst in 80-500ms, then API calls, then optionally SignalR. It knows the timing signatures of your real users' sessions compressed into 129-dimensional Markov chain vectors. It tracks identity across rotation attempts using cosine similarity walks across fingerprint neighbours. All of this runs in ~150µs per request on commodity hardware, with no network call to a third party and no PII leaving your server.

---

## Quick start

**macOS (Homebrew — recommended)**
```bash
brew install scottgal/stylobot/stylobot
stylobot 5080 http://localhost:3000        # starts in demo mode (observe only)
stylobot 5080 http://localhost:3000 --mode production   # enable blocking
```
Homebrew strips macOS's quarantine flag automatically. If you'd rather download the tarball, see the macOS first-run note in [docs/RELEASE_SIGNING.md](docs/RELEASE_SIGNING.md).

**Linux (apt - Debian/Ubuntu — recommended)**
```bash
curl -1sLf 'https://dl.cloudsmith.io/public/mostlylucid/stylobot/setup.deb.sh' | sudo bash
sudo apt update && sudo apt install stylobot
stylobot 5080 http://localhost:3000
stylobot genkey   # generate a secure HMAC key for production
```
The apt repo is signed (managed by Cloudsmith); `apt update` verifies the repo signature on every fetch.

**Linux (manual tarball)**
```bash
# Download from GitHub Releases
VER=X.Y.Z
curl -L -O https://github.com/scottgal/stylobot/releases/download/allbot-v${VER}/stylobot-linux-x64.tar.gz

# Verify provenance (proves this binary was built from this repo's workflow)
gh attestation verify stylobot-linux-x64.tar.gz --owner scottgal

tar xzf stylobot-linux-x64.tar.gz && chmod +x stylobot && sudo mv stylobot /usr/local/bin/
stylobot 5080 http://localhost:3000
```

**macOS (manual tarball)**
```bash
VER=X.Y.Z
curl -L -O https://github.com/scottgal/stylobot/releases/download/allbot-v${VER}/stylobot-osx-arm64.tar.gz
tar xzf stylobot-osx-arm64.tar.gz && cd stylobot-osx-arm64
./clear-quarantine.sh                # strip the download quarantine flag (one-time)
./stylobot 5080 http://localhost:3000
```
Or `brew install scottgal/stylobot/stylobot` and skip the quarantine dance.

**Docker (gateway - transparent proxy in front of your app)**
```bash
docker run --rm -p 8080:8080 -e DEFAULT_UPSTREAM=http://host.docker.internal:3000 \
  scottgal/stylobot-gateway:latest
```

**Docker (sidecar - detection API your app calls explicitly, gRPC + REST)**

The sidecar is a deliberately low-surface-area build of the same detection engine, scoped to the sidecar topology: your app makes a per-request call (gRPC or REST), gets a verdict back, decides what to do with it. No dashboard, no Razor, no SignalR, no HTML — just the detector pipeline behind a thin auth + transport layer. Run it next to your app (same pod, same host, same container) so the network hop is loopback. The proto and the REST schema are stable; the binary is single-file and self-contained.

```bash
docker run --rm -p 5090:5090 \
  -e BotDetection__ApiKeys__0__Key=changeme \
  scottgal/stylobot-sidecar:latest
# gRPC: localhost:5090  |  REST: POST localhost:5090/api/v1/detect
```

**Docker (all-in-one — gateway + dashboard for the simplest deployment)**

`stylobot-all` bundles YARP detection proxy and the dashboard into one process. One container, hit `/_stylobot` for the dashboard, point YARP at your upstream via `ReverseProxy:Routes` in appsettings.

```bash
docker run --rm -p 8080:8080 scottgal/stylobot-all:latest
# Dashboard: http://localhost:8080/_stylobot   |  Proxy + detection on the same port
```

**Docker (dashboard viewer — `stylobot-ui` against a remote gateway)**

`stylobot-ui` is the dashboard-host product. It runs *next to* a `stylobot` gateway (started with `--enable-api`) and proxies every dashboard read over HTTP. Hosted inside your network with local-only access; nothing leaves the LAN.

```bash
# 1. Run the gateway with the API enabled (one or more X-SB-Api-Key entries required)
stylobot 5080 http://your-app:3000 --enable-api

# 2. Run stylobot-ui pointed at the gateway
docker run --rm -p 5095:8080 \
  -e StyloBot__Source__Pull__Url=http://host.docker.internal:5080 \
  -e StyloBot__Source__Pull__ApiKey=SB-... \
  -e StyloBot__Source__Live__Url=http://host.docker.internal:5080/api/v1/hub \
  scottgal/stylobot-ui:latest
# Dashboard: http://localhost:5095/_stylobot (read-only viewer; gateway owns data + writes)
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

Dashboard at `/stylobot`. Detection at `~150µs` per request from first request.

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
stylobot 5080 http://localhost:3000 --mode production --llm ollama           # local (default: gemma4)
stylobot 5080 http://localhost:3000 --mode production --llm openai --llm-key sk-...
stylobot 5080 http://localhost:3000 --mode production --llm anthropic --llm-key sk-ant-...

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

Real-time monitoring at `/stylobot`. All data persists to SQLite.

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
Mostlylucid.BotDetection.Console/      Standalone CLI / gateway (6 platforms, AOT, --enable-api)
Mostlylucid.BotDetection.Sidecar/      Headless sidecar (gRPC + REST, AOT)
Stylobot.Gateway/                       Docker YARP reverse proxy
Stylobot.Ui/                            Dashboard host (rest/local mode, not AOT)
Stylobot.All/                           Gateway + dashboard in one process (not AOT)
sdk/node/                               Node.js SDK (core, node, elements packages)
bot-signatures/                         BDF replay test signatures
test-bdf-scenarios/                     BDF replay test scenarios
docs/                                   Architecture, specs, security review
scripts/                                Load tests, Docker compose, build tooling
```

### Binary topology

| Binary | Role | Detection | Dashboard | AOT | When |
|---|---|---|---|---|---|
| `stylobot` (Console) | Gateway / reverse-proxy | yes | no (`--enable-api` exposes REST + SignalR hub) | yes (35MB) | Edge gateway. Minimal surface; pair with `stylobot-ui` for the dashboard. |
| `stylobot-sidecar` | gRPC + REST detection sidecar | yes | no | yes (37MB) | App calls a per-request detect; loopback. |
| `stylobot-ui` | Dashboard host | no | yes | no | Hosted inside a network as the dashboard for a remote `stylobot` gateway. Loopback bind by default. |
| `stylobot-all` | Gateway + dashboard | yes | yes | no | One container, simplest deployment. Trades binary size for "it just works". |

## Documentation

### Getting Started

- [Quick start](src/Mostlylucid.BotDetection/docs/quickstart.md)
- [Integration levels](src/Mostlylucid.BotDetection/docs/integration-levels.md)
- [Tutorial](src/Mostlylucid.BotDetection/docs/tutorial.md)
- [Configuration reference](src/Mostlylucid.BotDetection/docs/configuration.md)
- [Configuration reference (full)](src/Mostlylucid.BotDetection/docs/configuration-reference.md)
- [Deployment guide](src/Mostlylucid.BotDetection/docs/deployment-guide.md)
- [Sidecar deployment](src/Mostlylucid.BotDetection/docs/sidecar-deployment.md) - gRPC/REST detection API for non-.NET backends
- [YARP integration](src/Mostlylucid.BotDetection/docs/yarp-integration.md)
- [YARP gateway](src/Mostlylucid.BotDetection/docs/yarp-gateway.md)
- [Proxy topologies](src/Mostlylucid.BotDetection/docs/proxy-topologies.md)

### Detection and Policies

- [Action policies](src/Mostlylucid.BotDetection/docs/action-policies.md)
- [Blocking and filters](src/Mostlylucid.BotDetection/docs/blocking-and-filters.md)
- [Signals and custom filters](src/Mostlylucid.BotDetection/docs/signals-and-custom-filters.md)
- [Detection strategies](src/Mostlylucid.BotDetection/docs/detection-strategies.md)
- [Policies](src/Mostlylucid.BotDetection/docs/policies.md)
- [Extensibility](src/Mostlylucid.BotDetection/docs/extensibility.md)

### Detectors

- [User agent detection](src/Mostlylucid.BotDetection/docs/user-agent-detection.md)
- [Header detection](src/Mostlylucid.BotDetection/docs/header-detection.md)
- [IP detection](src/Mostlylucid.BotDetection/docs/ip-detection.md)
- [AI detection](src/Mostlylucid.BotDetection/docs/ai-detection.md)
- [AI scraper detection](src/Mostlylucid.BotDetection/docs/ai-scraper-detection.md)
- [Behavioral analysis](src/Mostlylucid.BotDetection/docs/behavioral-analysis.md)
- [Advanced behavioral detection](src/Mostlylucid.BotDetection/docs/advanced-behavioral-detection.md)
- [Behavioral waveform](src/Mostlylucid.BotDetection/docs/behavioral-waveform.md)
- [Content sequence detection](src/Mostlylucid.BotDetection/docs/content-sequence-detection.md)
- [Centroid freshness](src/Mostlylucid.BotDetection/docs/centroid-freshness.md)
- [Cache behavior detection](src/Mostlylucid.BotDetection/docs/cache-behavior-detection.md)
- [Client-side fingerprinting](src/Mostlylucid.BotDetection/docs/client-side-fingerprinting.md)
- [TLS/TCP/HTTP/2/HTTP/3 fingerprinting](src/Mostlylucid.BotDetection/docs/AdvancedFingerprintingDetectors.md)
- [HTTP/3 fingerprinting](src/Mostlylucid.BotDetection/docs/http3-fingerprinting.md)
- [TCP/IP fingerprint](src/Mostlylucid.BotDetection/docs/tcp-ip-fingerprint.md)
- [Transport protocol detection](src/Mostlylucid.BotDetection/docs/transport-protocol-detection.md)
- [Stream/transport detection](src/Mostlylucid.BotDetection/docs/stream-transport-detection.md)
- [Inconsistency detection](src/Mostlylucid.BotDetection/docs/inconsistency-detection.md)
- [Cluster detection](src/Mostlylucid.BotDetection/docs/cluster-detection.md)
- [Response behavior](src/Mostlylucid.BotDetection/docs/response-behavior.md)
- [Fast-path reputation](src/Mostlylucid.BotDetection/docs/fast-path-reputation.md)
- [Reputation bias](src/Mostlylucid.BotDetection/docs/reputation-bias.md)
- [Learning and reputation](src/Mostlylucid.BotDetection/docs/learning-and-reputation.md)
- [Timescale reputation](src/Mostlylucid.BotDetection/docs/timescale-reputation.md)
- [Geo change detection](src/Mostlylucid.BotDetection/docs/geo-change-detection.md)
- [Haxxor detection](src/Mostlylucid.BotDetection/docs/haxxor-detection.md)
- [Security tools detection](src/Mostlylucid.BotDetection/docs/security-tools-detection.md)
- [Version age detection](src/Mostlylucid.BotDetection/docs/version-age-detection.md)
- [Verified bot detection](src/Mostlylucid.BotDetection/docs/verified-bot-detection.md)
- [Adblocker detection](src/Mostlylucid.BotDetection/docs/adblocker-detection.md)
- [Click fraud detection](src/Mostlylucid.BotDetection/docs/click-fraud-detection.md)
- [Account takeover detection](src/Mostlylucid.BotDetection/docs/account-takeover-detection.md)
- [Project Honeypot](src/Mostlylucid.BotDetection/docs/project-honeypot.md)

### Features

- [Simulation packs](src/Mostlylucid.BotDetection/docs/simulation-packs.md)
- [Holodeck (honeypot response system)](src/Mostlylucid.BotDetection/docs/holodeck.md)
- [Custom pack authoring](src/Mostlylucid.BotDetection/docs/custom-pack-authoring.md)
- [Endpoint pinning](src/Mostlylucid.BotDetection/docs/endpoint-pinning.md)
- [Fingerprint approval](src/Mostlylucid.BotDetection/docs/fingerprint-approval.md)
- [Proof-of-work challenge](src/Mostlylucid.BotDetection/docs/proof-of-work-challenge.md)
- [Session analytics](src/Mostlylucid.BotDetection/docs/session-analytics.md)
- [Response PII masking](src/Mostlylucid.BotDetection/docs/response-pii-masking.md)
- [SignalR beacon architecture](src/Mostlylucid.BotDetection/docs/signalr-beacon-architecture.md)
- [Authenticated users](src/Mostlylucid.BotDetection/docs/authenticated-users-spec.md)
- [WAF comparison](src/Mostlylucid.BotDetection/docs/waf-comparison.md)

### Dashboard and API

- [API reference](src/Mostlylucid.BotDetection/docs/api-reference.md)
- [API keys](src/Mostlylucid.BotDetection/docs/api-keys.md)
- [Dashboard threat scoring](src/Mostlylucid.BotDetection/docs/dashboard-threat-scoring.md)
- [In-dashboard config editor](src/Mostlylucid.BotDetection/docs/in-dashboard-config-editor.md)
- [Training data API](src/Mostlylucid.BotDetection/docs/training-data-api.md)
- [UI components](src/Mostlylucid.BotDetection/docs/ui-components.md)
- [BDF replay system](src/Mostlylucid.BotDetection/docs/bdf-system-guide.md)

### Infrastructure and Ops

- [Local GPU tunnel](src/Mostlylucid.BotDetection/docs/local-llm-tunnel.md)
- [Telemetry and metrics](src/Mostlylucid.BotDetection/docs/telemetry-and-metrics.md)
- [External calls](src/Mostlylucid.BotDetection/docs/external-calls.md)
- [Data sources](src/Mostlylucid.BotDetection/docs/data-sources.md)
- [Licensing](src/Mostlylucid.BotDetection/docs/licensing.md)

### Architecture Reference

- [Signature coordinator](src/Mostlylucid.BotDetection/docs/signature-coordinator-architecture.md)
- [YAML configuration architecture](src/Mostlylucid.BotDetection/docs/YAML_CONFIGURATION_ARCHITECTURE.md)
- [Fast-path signature matching](src/Mostlylucid.BotDetection/docs/FAST_PATH_SIGNATURE_MATCHING.md)
- [Multi-factor signatures](src/Mostlylucid.BotDetection/docs/MULTI_FACTOR_SIGNATURES.md)

- [CHANGELOG](CHANGELOG.md)

## Requirements

- .NET 10.0 (building from source)
- No external dependencies for FOSS (SQLite is embedded)
- Commercial: PostgreSQL, optional Redis

## License

[The Unlicense](https://unlicense.org/) FOSS core is public domain. Commercial features licensed separately.