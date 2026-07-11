# StyloBot Release Notes

> Curated marketing-style notes for major releases. The full per-version detail (including patch versions, fixes, and refactors) lives in the root [`CHANGELOG.md`](../CHANGELOG.md).

## v6.7.0 - 2026-05-21

### Pre-launch hardening

Detection is feature-frozen; this release is the operator-ergonomics + dashboard pass before RTM. Highlights:

- **Edge-injected client signals**: behind a reverse proxy (Cloudflare / Caddy / nginx / AWS ALB), the gateway now reads the client's real HTTP version, TLS version + cipher + handshake hash, and ASN from headers injected by the edge. One Cloudflare Transform Rule does the whole job; recipes for every common proxy in [`REVERSE_PROXY_SIGNALS.md`](REVERSE_PROXY_SIGNALS.md). The Fingerprint Profile card on signature detail finally shows the right values for traffic behind a CDN.
- **Admin reload + restart endpoints**: apply config changes (action-policy weights, path policies, learning toggles) without redeploying. `POST /admin/reload` triggers `IConfigurationRoot.Reload()` so `IOptionsMonitor` consumers see the new values; `POST /admin/restart` flushes the response and lets the supervisor bring a fresh process up. Off by default, fail-closed if you forget the token. See [`admin-endpoints.md`](admin-endpoints.md).
- **Observe-only default + pre-launch banner**: the Gateway image ships with `BlockDetectedBots = false` and `throttle-stealth` so operators can watch the dashboard for a day before flipping to enforcement. A pre-launch banner across the dashboard chrome signals this state. Flipping is one config change; the new "Default posture" section in [`action-policies.md`](../src/Mostlylucid.BotDetection/docs/action-policies.md#default-posture-observe-only) walks through the four enforcement-mode policies (`block` / `throttle-status` / `throttle-tools` / `challenge`).

### Dashboard restructure

- **Compact metric strip** replaces the two big header cards.
- **Map + chart on top, fewer tabs**: world threat map and traffic chart sit equal-height side-by-side under the strip; the tabbed surface is leaner.
- **Session detail full-page route** at `/_stylobot/sessions/{id}` with a synthetic in-flight view (a mid-session fingerprint still renders with its accumulated state).
- **Endpoint detail panel**: per-endpoint response times (min / avg / p95 / max), top visitors with grouped identities collapsed, recent activity, and a UA-version time series.
- **Behavioural-shape radar** now shown on the bot-detection-details card, not only on the session timeline.
- **Theme picker** that actually picks themes; one shared early-paint init kills the light-then-dark flash on page load.
- **Vendored country flags**: 271 SVGs ship locally; no more `flagcdn.com` dependency.
- **Live-update arbitration**: user filter / sort / page always wins over a SignalR background refresh, with a cooldown that absorbs late-arriving responses so user-active widgets are never clobbered mid-interaction.

### Naming pipeline (one display name per fingerprint)

Six naming paths collapsed to one canonical pipeline owned by `FingerprintNameComposer`. Distinctive-modifier guarantees a unique display name per fingerprint id; verified-bot rows that resolve to the same canonical name collapse to one row at the data layer (humans + tool clients stay distinct). Groupable identities (Amazonbot, GPTBot, etc.) collapse to one row on the Visitors list and on the endpoint detail "Most regular" table. Friendly-bot names live in `bot-patterns.yaml`, not in hard-coded lists.

### Pipeline quality sweep

- 29 dead `SignalKeys` removed (no detector wrote them, no consumer read them).
- Rate-limiter TOCTOU fixed: check and update under one lock.
- `PeriodicityAtom` + `IdentityChangeAtom` marked `IFoundationContributor` so they run unconditionally on every request.
- `FingerprintMatchAtom` self-computes the identity vector when the wave race elides the upstream signal.
- `SignatureAggregateCache` warms from the persisted `signatures` table on startup (distinct-by-signature so a chatty source can't blank the cache).
- `ExtractThreatScore` reads honeypot + attack signals too.
- YARP integration null-guards `evidence.Signals` before dereference.

---

## v6.0.1-beta.0 -2026-04-23

### New: Content Sequence Detection

StyloBot now tracks each visitor's request sequence -the natural order of events that follows a real browser loading a page -and uses divergence from that expected sequence as a strong bot signal.

When a browser loads a page it does a recognisable thing: the document comes first, then a burst of CSS, JS, and images within 500ms (the "critical window"), then API calls and optional SignalR connections. Bots almost never follow this pattern. They either skip directly to APIs, fire requests at machine speed (<20ms apart), or ignore assets entirely.

**How it works:**

The new `ContentSequenceAtom` (Priority 4) runs on every request before the expensive detectors:

1. Document requests (Sec-Fetch-Mode: navigate) reset the sequence at position 0 and load the best available expected chain -cluster-specific if enough session data exists, or the global human fallback.
2. Continuation requests classify the request type, advance position, and evaluate divergence across four time-based phase windows.
3. Divergence signals gate the expensive deferred detectors (SessionVector, BehavioralWaveform, Periodicity, ResourceWaterfall, CacheBehavior): they skip early on-track sequences and run only when the sequence is diverged, active long enough (position ≥ 3), or absent entirely (API-only bots always get the full analysis).

**Cache-warm detection:** Visitors whose browser cache is already primed skip the initial asset burst. The detector recognises this pattern (no static assets in the first 500ms) and suppresses the false-positive "no assets loaded" signal that would otherwise flag a repeat visitor.

**SignalR guard:** When the next expected chain step is SignalR on a human-centroid chain, `sequence.signalr_expected` is set and `StreamAbuseAtom` skips -avoiding false positives on expected WebSocket upgrades.

Full documentation: `docs/content-sequence-detection.md`

---

### New: Centroid Freshness -False-Positive Suppression After Deploys

Content sequence detection compares sessions against a stored centroid (the "normal" chain for an endpoint). When your site gets redeployed -restructured HTML, new JS framework, renamed assets -real browser sessions temporarily diverge from the old centroid and would be incorrectly flagged as bots.

Centroid Freshness detects this situation and suppresses divergence scoring for 1 hour while the centroid adapts.

**Two detection mechanisms:**

1. **Divergence rate spike:** `EndpointDivergenceTracker` keeps a rolling 1-hour per-path window. When ≥40% of sessions hitting an endpoint diverge (minimum 10 sessions), the endpoint's centroid is marked stale. A bot wave doesn't cause uniform divergence across all sessions -a content change does.

2. **Static asset fingerprint change:** `AssetHashMiddleware` reads the `ETag` or `Last-Modified` of every static asset response. When the fingerprint changes between requests, a deploy is detected and `sequence.centroid_stale` is written on the next document request.

Full documentation: `docs/centroid-freshness.md`

---

### New: Local LLM GPU Tunnel

Route LLM classification work from a cloud/VPS instance (no GPU) to a local machine with a GPU and Ollama, using a Cloudflare tunnel as the transport.

**New NuGet package:** `Mostlylucid.BotDetection.Llm.Tunnel`

**New console command:** `stylobot llmtunnel`

The tunnel agent probes your local Ollama instance, binds a loopback Kestrel server, starts a Cloudflare tunnel (anonymous quick tunnel or stable named tunnel), and prints a single `sb_llmtunnel_v1_<key>` connection string. Paste that key into the remote StyloBot config and the remote site will route all LLM inference through your GPU.

```bash
# On the local GPU machine
stylobot llmtunnel

# On the remote site
stylobot 5080 https://mysite.example.com --llm localtunnel --llm-key "sb_llmtunnel_v1_..."
```

**Security:** HMAC-SHA256 per-request signing, 30-second TTL nonces with 60-second replay protection window. All traffic flows through Cloudflare's encrypted tunnel; the agent only listens on loopback.

**Named vs anonymous tunnels:**
- Anonymous (quick): no Cloudflare account needed, URL changes on restart- must re-import key after each restart
- Named: requires a Cloudflare tunnel token, stable URL across restarts

**Dashboard status strip:** New "GPU Tunnels" widget shows active node count, per-node status badges with model list and queue depth.

**Configuration:**
```json
{
  "BotDetection": {
    "AiDetection": {
      "LocalTunnel": {
        "ConnectionKey": "sb_llmtunnel_v1_..."
      }
    }
  }
}
```

### Bug Fixes

- **HNSW graph deserialization:** Stale HNSW graph files (from older HNSW.Net versions) now deleted automatically on MessagePack deserialization failure- no more `FormatterNotRegisteredException` noise on startup after a library update.

- **AOT JSON serialization:** All JSON serialization in the tunnel package now uses source-generated contexts (`TunnelJsonContext`), removing any reflection-based JSON calls. The `stylobot` console binary publishes correctly as a NativeAOT single-file executable.

---

## v6.0.0-alpha- 2026-04-21

### Architecture

- **Local LLM Tunnel** package skeleton, crypto layer (HMAC-SHA256, nonce replay, optional AES-256-GCM envelope), agent endpoints, Cloudflare launcher, console command wiring
- **StatusStrip** dashboard widget for GPU tunnel node status
- **Content Sequence Detection**- divergence tracking per endpoint using Markov-chain centroid comparison; staleness signals from ETag/content-hash changes (`AssetHashStore`, `AssetHashMiddleware`)
- **Centroid Freshness**- endpoint staleness state in `CentroidSequenceStore`; `EndpointDivergenceTracker` rolling per-path divergence rate

### Public API & Node SDK

- Canonical REST API (`Mostlylucid.BotDetection.Api`) at `/api/v1/*`
- Node SDK: `@stylobot/core` (zero-dep types + client), `@stylobot/node` (Express middleware, Fastify plugin)
- API auth tiers: proxy headers (zero-latency), `X-SB-Api-Key` (detection + read), OIDC bearer (management)

---

## v5.6.0- 2026-04-17

### RTM Release

- 45 detectors across 4 waves, <1ms fast path, Leiden clustering
- Zero-PII design with HMAC-SHA256 signature hashing
- SQLite persistence (FOSS), PostgreSQL upgrade path (commercial)
- Dashboard: session timeline, Markov chain drill-in, behavioral radar charts, world threat map, Threats tab (CVE probes)
- Simulation packs (WordPress FOSS)
- Holodeck honeypot response system with beacon canary tracking
- PoW challenge system (SHA-256 micro-puzzles)
- Anonymous entity resolution (L0–L5 confidence levels)
- Session vectors: 129-dimensional Markov chain compression
- `UseStyloBot()` single-call setup