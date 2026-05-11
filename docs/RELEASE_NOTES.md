# StyloBot Release Notes

## v6.0.1-beta.0 -2026-04-23

### New: Content Sequence Detection

StyloBot now tracks each visitor's request sequence -the natural order of events that follows a real browser loading a page -and uses divergence from that expected sequence as a strong bot signal.

When a browser loads a page it does a recognisable thing: the document comes first, then a burst of CSS, JS, and images within 500ms (the "critical window"), then API calls and optional SignalR connections. Bots almost never follow this pattern. They either skip directly to APIs, fire requests at machine speed (<20ms apart), or ignore assets entirely.

**How it works:**

The new `ContentSequenceContributor` (Priority 4) runs on every request before the expensive detectors:

1. Document requests (Sec-Fetch-Mode: navigate) reset the sequence at position 0 and load the best available expected chain -cluster-specific if enough session data exists, or the global human fallback.
2. Continuation requests classify the request type, advance position, and evaluate divergence across four time-based phase windows.
3. Divergence signals gate the expensive deferred detectors (SessionVector, BehavioralWaveform, Periodicity, ResourceWaterfall, CacheBehavior): they skip early on-track sequences and run only when the sequence is diverged, active long enough (position ≥ 3), or absent entirely (API-only bots always get the full analysis).

**Cache-warm detection:** Visitors whose browser cache is already primed skip the initial asset burst. The detector recognises this pattern (no static assets in the first 500ms) and suppresses the false-positive "no assets loaded" signal that would otherwise flag a repeat visitor.

**SignalR guard:** When the next expected chain step is SignalR on a human-centroid chain, `sequence.signalr_expected` is set and `StreamAbuseContributor` skips -avoiding false positives on expected WebSocket upgrades.

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