# Mostlylucid.BotDetection

**Enterprise bot detection and anonymous entity resolution for ASP.NET Core.** Probabilistic, behavioural, and protocol-deep — not just User-Agent matching.

This is the detection engine that powers **[StyloBot](https://stylo.bot)** — a self-hosted bot defense platform you can run in front of any web application.

[![NuGet](https://img.shields.io/nuget/v/mostlylucid.botdetection.svg)](https://www.nuget.org/packages/mostlylucid.botdetection)
[![GitHub](https://img.shields.io/badge/GitHub-scottgal%2Fstylobot-blue)](https://github.com/scottgal/stylobot)
[![StyloBot](https://img.shields.io/badge/Website-stylo.bot-blue)](https://stylo.bot)

---

## What it does

57 contributors fire in a wave-based pipeline. The fast path (<1 ms) handles 90% of traffic. Slow-path and session contributors only activate when upstream signals justify it.

- **57 detection contributors** across 4 waves — UA, headers, IP, protocol fingerprinting (JA3/JA4/H2/QUIC/TCP-IP), behavioural, AI, cluster discovery, CVE probes
- **Transport header trust gate** (7.5) — X-JA3/X-JA4/X-H2/QUIC headers are gated behind peer-IP trust so attackers can't inject spoofed fingerprints
- **arcjet well-known-bots catalog** (7.5) — 635 additional bot UA patterns downloaded hourly; fills gaps in YAML definitions (TurnitinBot, SemanticScholarBot, monitoring bots, etc.)
- **Forward-DNS verified-bot confirmation** (7.5) — ActivityPub `+URL` claims in fediverse UAs are confirmed against A/AAAA records; spoofed Mastodon UAs are rejected
- **Metastable fingerprint identity** — each visitor is a learned vector *shape*, not a static cookie. Persistent trust state, claim-first display naming.
- **Blackboard architecture** via StyloFlow — detectors read/write ephemeral signals; zero-PII design (all persistence uses HMAC-SHA256 hashes)
- **Leiden clustering** finds coordinated bot campaigns
- **129-dim Markov chain session vectors** — inter-session velocity, partial-chain archetypes, snapshot compaction
- **Anonymous entity resolution** — merge/split/rewind backed by immutable session snapshots; L0-L5 confidence levels
- **Policy stack** — YAML-backed rules separate detection (WHAT) from action (HOW); full editor in the dashboard (7.5)
- **robots.txt + sitemap** extensions (7.5) — `MapStyloBotRobotsTxt()` generates policy-aware Disallow lines; `MapStyloBotSitemap()` serves verdict-adaptive sitemaps
- **SQLite everywhere** for FOSS — zero-dependency persistence; PostgreSQL is the commercial upgrade path

---

## Quick start

```bash
dotnet add package Mostlylucid.BotDetection
```

```csharp
// Program.cs
builder.Services.AddStyloBot(dashboard =>
{
    dashboard.AllowUnauthenticatedAccess = true; // dev only
});
app.UseRouting();
app.UseStyloBot();  // detection + dashboard, correct middleware ordering
```

That's it. The dashboard is at `/_stylobot`. All 57 contributors are active. SQLite databases are created in the working directory.

---

## Common configurations

```csharp
// Detection only — no dashboard
builder.Services.AddBotDetection();
app.UseBotDetection();

// User-agent only — minimal footprint
builder.Services.AddSimpleBotDetection();

// Ephemeral mode — no SQLite, state evaporates on restart (dev/serverless)
builder.Services.AddBotDetectionInMemory();

// LLM escalation for edge cases
builder.Services.AddAdvancedBotDetection("http://localhost:11434", "gemma4");

// robots.txt + sitemap driven by policy rules
app.MapStyloBotRobotsTxt();   // serves Disallow: lines for blocked bots
app.MapStyloBotSitemap();     // serves different URLs by detection verdict
```

---

## Transport header trust (new in 7.5 — security fix)

If you run behind a reverse proxy (Cloudflare, nginx, Caddy, YARP), configure the trust list so injected edge headers (JA3/JA4, HTTP/2, QUIC, TCP/IP) are accepted only from your proxy:

```json
{
  "BotDetection": {
    "TransportTrust": {
      "TrustedProxyIps": ["10.0.0.1/24", "172.16.0.0/12"],
      "Mode": "Strict"
    }
  }
}
```

Without this, an attacker hitting the gateway over direct HTTPS can inject a known-Chrome JA3 and receive a human-signal bias. See [proxy-topologies.md](docs/proxy-topologies.md).

---

## HttpContext extensions

```csharp
if (context.IsBot()) return Results.StatusCode(403);

var confidence = context.GetBotConfidence();  // 0.0–1.0
var botType    = context.GetBotType();        // BotType enum
var botName    = context.GetBotName();        // "Googlebot", "curl", etc.
```

---

## Action policies

| Policy | Effect |
|--------|--------|
| `block` | HTTP 403 |
| `throttle-stealth` | Silent delay (bots don't know they're throttled) |
| `throttle-tools` | HTTP 429 + `Retry-After` |
| `throttle-status` | Fast HTTP 429 for friendly bots (Mastodon, UptimeRobot) |
| `challenge` | Proof-of-work or CAPTCHA |
| `redirect-honeypot` | Silent redirect to trap |
| `logonly` | Shadow mode — observe without acting |

Apply globally or per-path:

```json
{
  "BotDetection": {
    "DefaultActionPolicyName": "throttle-stealth",
    "BlockDetectedBots": true
  }
}
```

---

## Detection waves

### Fast path (<1 ms, every request)

UserAgent (YAML + arcjet catalog fallback), Header, IP, SecurityTool, Behavioral, ClientSide, Inconsistency, VersionAge, Heuristic, FastPathReputation, CacheBehavior, CookieBehavior, ResourceWaterfall, ReputationBias, AiScraper (YAML + arcjet AI fallback), Haxxor, CveProbe, PiiQueryString, VerifiedBot, VerifiedBotInline, FediverseDomain, BrowserModeClassifier, CveFingerprint, HeuristicLate, ClaimedIdentity, ThreatIntel

### Advanced fingerprinting

TlsFingerprint (JA3/JA4), TcpIpFingerprint (p0f), Http2Fingerprint (AKAMAI), Http3Fingerprint (QUIC), MultiLayerCorrelation, BehavioralWaveform, ResponseBehavior, TransportProtocol, StreamAbuse

### Session / behavioural analysis

SessionVector (Markov chain → 129-dim vector), Periodicity, ReactivePattern, Similarity, Cluster

### Entity resolution

AccountTakeover, IdentityChange, GeoChange, PoolCollision

### Post-round-trip

ChallengeVerification, FingerprintApproval, ClickFraud, Honeypot.EndpointHistory, Honeypot.HoneypotLink

### LLM escalation (opt-in)

Llm (enrichment only, not the decision-maker)

### Slow path (~100 ms, opt-in)

ProjectHoneypot (DNS lookup against http:BL)

---

## Detector timing (Apple M5, .NET 10, warm cache, full 57-contributor pipeline)

| Scenario | Mean | Allocated |
|----------|------|-----------|
| AiScraper — GPTBot | 269 ns | 1,008 B |
| Haxxor — clean | 198 ns | 0 B |
| Haxxor — SQL injection | 1,202 ns | 1,744 B |
| Heuristic — bot | 1,653 ns | 2,528 B |
| Heuristic — human | 1,704 ns | 2,512 B |
| Intent — navigation | 2,540 ns | 5,784 B |
| IP — datacenter | 320 ns | 1,136 B |
| TLS fingerprint — Chrome | 262 ns | 896 B |
| Header — curl (bot) | 424 ns | 1,544 B |
| Header — Chrome (human) | 417 ns | 1,320 B |
| CookieBehavior — cookies | 18 ns | 184 B |
| Http2 — Chrome | 110 ns | 176 B |
| HeaderCorrelation — full | 15 ns | 104 B |
| UserAgent — Googlebot (full pipeline) | 13,272 ns | 2,568 B |
| UserAgent — Chrome (full pipeline) | 104,821 ns | 1,817 B |

The full-pipeline Chrome number (105 µs) reflects all 57 contributors running; the detection-code share of a typical gateway request is ~0.1% of total latency (remainder is network + Kestrel).

---

## Real-time dashboard

Mount at `/_stylobot` (or configure `BasePath`). Features: live signature feed, session timeline with Markov drill-in, behavioural radar, world threat map, cluster visualisation, UA breakdown, Threats tab, policy editor (7.5).

---

## YARP / gateway integration

Use [`Stylobot.Gateway`](https://hub.docker.com/r/scottgal/stylobot-gateway) or [`stylobot` CLI](https://github.com/scottgal/stylobot/releases) for edge deployments. Edge-injected client signals (X-JA3-Hash, X-Client-HTTP-Version, X-Client-TLS-*) are forwarded by the gateway and read by the contributors — gated by `TransportTrust` config.

---

## Requirements

- .NET 10.0
- LlamaSharp or Ollama for optional LLM escalation

## License

[GNU AGPL-3.0-only](https://www.gnu.org/licenses/agpl-3.0) — free for open-source and internal use; public-facing SaaS deployments must share source or obtain a commercial licence.

## Links

- [**StyloBot**](https://stylo.bot) — hosted platform and live demo powered by this engine
- [GitHub](https://github.com/scottgal/stylobot)
- [NuGet](https://www.nuget.org/packages/mostlylucid.botdetection/)
- [Documentation](https://github.com/scottgal/stylobot/tree/main/src/Mostlylucid.BotDetection/docs)
- [Changelog](https://github.com/scottgal/stylobot/blob/main/CHANGELOG.md)