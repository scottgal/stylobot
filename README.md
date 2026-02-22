# StyloBot
by ***mostly*lucid**

Enterprise bot detection framework for ASP.NET Core. 29 detectors, wave-based orchestration, adaptive AI learning, real-time dashboard with world map, and reverse-proxy integration — all in two lines of code.

<img src="https://raw.githubusercontent.com/scottgal/stylobot/refs/heads/main/mostlylucid.stylobot.website/src/Stylobot.Website/wwwroot/img/stylowall.svg?raw=true" alt="StyloBot" style="max-width:200px; height:auto;" />

## What This Repo Contains

This repository is a monorepo for the StyloBot ecosystem:

- `Mostlylucid.BotDetection`: Core detection library and middleware
- `Mostlylucid.BotDetection.Demo`: End-to-end demo app with test pages, API endpoints, and live signatures
- `Stylobot.Gateway`: Docker-first YARP gateway with built-in detection
- `Mostlylucid.BotDetection.UI`: Dashboard/tag helpers/view components
- `Mostlylucid.BotDetection.UI.PostgreSQL`: PostgreSQL + TimescaleDB + pgvector persistence
- `mostlylucid.stylobot.website`: Marketing/demo website using the detection stack

## Requirements

- .NET SDK `10.0` (repo projects target `net10.0`)
- Docker + Docker Compose (optional, for containerized flows)
- Optional for advanced scenarios:
  - PostgreSQL/TimescaleDB
  - Ollama (for LLM provider mode)

## Quick Start (Local Demo)

```bash
dotnet run --project Mostlylucid.BotDetection.Demo
```

Open:

- `http://localhost:5080/bot-test`
- `http://localhost:5080/SignatureDemo`
- `http://localhost:5080/bot-detection/check`

If you run the HTTPS launch profile, HTTPS is also available at `https://localhost:5001`.

## Quick Start (Gateway)

Run gateway with one upstream (zero-config mode):

```bash
docker run --rm -p 8080:8080 -e DEFAULT_UPSTREAM=http://host.docker.internal:3000 scottgal/stylobot-gateway:latest
```

Health endpoint:

```bash
curl http://localhost:8080/admin/health
```

If `ADMIN_SECRET` is configured, include header `X-Admin-Secret` for `/admin/*` endpoints.

## Detection Surface — 29 Detectors

All detectors run in a wave-based pipeline. Fast-path detectors execute in parallel in <1ms; advanced detectors fire only when triggered by upstream signals.

| Wave | Detectors | Latency |
|------|-----------|---------|
| **Wave 0 — Fast Path** | UserAgent, Header, IP, SecurityTool, TransportProtocol, VersionAge, AiScraper, FastPathReputation, ReputationBias, VerifiedBot | <1ms |
| **Wave 1 — Behavioral** | Behavioral, AdvancedBehavioral, BehavioralWaveform, CacheBehavior, ClientSide, GeoChange, ResponseBehavior, StreamAbuse | 1-5ms |
| **Wave 2 — Fingerprinting** | TLS (JA3/JA4), TCP/IP (p0f), HTTP/2 (AKAMAI), HTTP/3 (QUIC), MultiLayerCorrelation | <1ms |
| **Wave 3 — AI + Learning** | Heuristic, HeuristicLate, Similarity, Cluster (Leiden), TimescaleReputation, LLM (optional) | 1-500ms |
| **Slow Path** | ProjectHoneypot (DNS lookup) | ~100ms |

Real contributor lists are controlled by `BotDetection:Policies` in each app config.

### Key Capabilities

- **Protocol-level fingerprinting**: JA3/JA4 TLS, p0f TCP/IP, AKAMAI HTTP/2, QUIC HTTP/3 — detect bots even when they spoof headers perfectly
- **Bot network discovery**: Leiden clustering finds coordinated bot campaigns across thousands of signatures
- **Adaptive AI**: Heuristic model extracts ~50 features per request and learns from feedback — detection improves over time
- **Geo intelligence**: Country reputation tracking, geographic drift detection, VPN/proxy/Tor/datacenter identification
- **Verified bot authentication**: DNS-verified identification of Googlebot, Bingbot, and 30+ legitimate crawlers
- **AI scraper detection**: GPTBot, ClaudeBot, PerplexityBot, Google-Extended and Cloudflare AI signals
- **Zero PII**: All persistence uses HMAC-SHA256 hashed signatures — no raw IPs or user agents stored

### Training Data API

Export detection data for ML training:

```bash
# JSONL streaming export with labels
curl http://localhost:5080/bot-detection/training/export > training-data.jsonl

# Cluster data
curl http://localhost:5080/bot-detection/training/clusters

# Country reputation
curl http://localhost:5080/bot-detection/training/countries
```

Register with `app.MapBotTrainingEndpoints()`. See [Training Data API docs](Mostlylucid.BotDetection/docs/training-data-api.md).

## Real-Time Dashboard

The built-in dashboard (`Mostlylucid.BotDetection.UI`) provides live monitoring via SignalR:

- **Overview**: Total/bot/human request counts, bot rate, unique signatures, top bots
- **World Map**: jsvectormap with countries colored by bot rate (green→amber→red) and markers sized by traffic volume
- **Countries Tab**: Country-level bot rates, reputation scores, request volumes
- **Clusters Tab**: Leiden-detected bot networks with similarity scores and campaign analysis
- **User Agents Tab**: UA family breakdown with category badges, version distribution, country per UA
- **Visitors Tab**: Live signature feed with risk bands, sparkline histories, drill-down details
- **Detections Tab**: Full detection event log with per-detector contributions and signal breakdown

All data updates in real-time via SignalR. JSON API endpoints available for programmatic access.

```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotDashboard();
app.UseStyloBotDashboard();  // Dashboard at /_stylobot/
```

## Core Product Differentiators

- **Speed with intelligence**: <1ms fast path across 29 detectors with explainable evidence per decision
- **Protocol-deep fingerprinting**: TLS, TCP/IP, HTTP/2, HTTP/3 fingerprints catch bots that spoof everything else
- **Temporal behavior resolution**: cross-request, windowed signal correlation for stronger bot/human discrimination
- **Adaptive learning**: Heuristic weights evolve based on detection outcomes — gets smarter over time
- **Powered by `mostlylucid.ephemeral`**: efficient ephemeral state and coordinator patterns that enable across-time analysis without heavy per-request latency
- **Operator-first control**: composable action policies — you decide how to respond (block, throttle, challenge, honeypot, log)

## Common Dev Commands

```bash
# Build all projects
dotnet build mostlylucid.stylobot.sln

# Run the demo app
dotnet run --project Mostlylucid.BotDetection.Demo

# Run the gateway app
dotnet run --project Stylobot.Gateway

# Run tests
dotnet test mostlylucid.stylobot.sln
```

## Docker Compose Stacks

- `docker-compose.yml`: TimescaleDB + demo app
- `docker-compose.demo.yml`: full stack (Caddy + gateway + website + DB + extras)

Start minimal compose stack:

```bash
cp .env.example .env
# set POSTGRES_PASSWORD in .env
docker compose up -d
```

## Documentation Map

Start here for canonical docs:

- [`docs/README.md`](docs/README.md) (entry index)
- [`QUICKSTART.md`](QUICKSTART.md) (hands-on local runbook)
- [`DOCKER_SETUP.md`](DOCKER_SETUP.md) (compose and deployment workflows)

Library and component docs:

- [`Mostlylucid.BotDetection/README.md`](Mostlylucid.BotDetection/README.md)
- [`Mostlylucid.BotDetection/docs/`](Mostlylucid.BotDetection/docs/)
- [`Stylobot.Gateway/README.md`](Stylobot.Gateway/README.md)
- [`Mostlylucid.BotDetection.UI/README.md`](Mostlylucid.BotDetection.UI/README.md)
- [`Mostlylucid.BotDetection.UI.PostgreSQL/README.md`](Mostlylucid.BotDetection.UI.PostgreSQL/README.md)
- [`Mostlylucid.BotDetection/docs/detection-strategies.md`](Mostlylucid.BotDetection/docs/detection-strategies.md)
- [`Mostlylucid.BotDetection/docs/action-policies.md`](Mostlylucid.BotDetection/docs/action-policies.md)

Detector docs (29 detectors):

- [`user-agent-detection.md`](Mostlylucid.BotDetection/docs/user-agent-detection.md) — UA parsing, bot pattern matching
- [`header-detection.md`](Mostlylucid.BotDetection/docs/header-detection.md) — HTTP header anomalies
- [`ip-detection.md`](Mostlylucid.BotDetection/docs/ip-detection.md) — Datacenter, botnet, proxy IP ranges
- [`behavioral-analysis.md`](Mostlylucid.BotDetection/docs/behavioral-analysis.md) — Request pattern analysis
- [`advanced-behavioral-detection.md`](Mostlylucid.BotDetection/docs/advanced-behavioral-detection.md) — Entropy, Markov chains, anomaly detection
- [`behavioral-waveform.md`](Mostlylucid.BotDetection/docs/behavioral-waveform.md) — FFT spectral timing fingerprinting
- [`client-side-fingerprinting.md`](Mostlylucid.BotDetection/docs/client-side-fingerprinting.md) — Headless browser detection via JS
- [`version-age-detection.md`](Mostlylucid.BotDetection/docs/version-age-detection.md) — Browser/OS version freshness
- [`security-tools-detection.md`](Mostlylucid.BotDetection/docs/security-tools-detection.md) — Burp, Metasploit, sqlmap, etc.
- [`cache-behavior-detection.md`](Mostlylucid.BotDetection/docs/cache-behavior-detection.md) — ETag, gzip, cache header analysis
- [`response-behavior.md`](Mostlylucid.BotDetection/docs/response-behavior.md) — Honeypot and response-side patterns
- [`ai-detection.md`](Mostlylucid.BotDetection/docs/ai-detection.md) — Heuristic model + LLM escalation
- [`ai-scraper-detection.md`](Mostlylucid.BotDetection/docs/ai-scraper-detection.md) — GPTBot, ClaudeBot, PerplexityBot
- [`cluster-detection.md`](Mostlylucid.BotDetection/docs/cluster-detection.md) — Leiden clustering for bot networks
- [`AdvancedFingerprintingDetectors.md`](Mostlylucid.BotDetection/docs/AdvancedFingerprintingDetectors.md) — TLS (JA3/JA4), TCP/IP (p0f), HTTP/2 (AKAMAI)
- [`http3-fingerprinting.md`](Mostlylucid.BotDetection/docs/http3-fingerprinting.md) — QUIC transport fingerprinting
- [`multi-layer-correlation.md`](Mostlylucid.BotDetection/docs/multi-layer-correlation.md) — Cross-layer consistency
- [`transport-protocol-detection.md`](Mostlylucid.BotDetection/docs/transport-protocol-detection.md) — WebSocket, gRPC, GraphQL, SSE protocol validation
- [`stream-transport-detection.md`](Mostlylucid.BotDetection/docs/stream-transport-detection.md) — Stream-aware detection, SignalR classification, stream abuse
- [`learning-and-reputation.md`](Mostlylucid.BotDetection/docs/learning-and-reputation.md) — Adaptive learning system
- [`timescale-reputation.md`](Mostlylucid.BotDetection/docs/timescale-reputation.md) — TimescaleDB reputation tracking
- [`training-data-api.md`](Mostlylucid.BotDetection/docs/training-data-api.md) — ML training data export

## Notes on Existing Docs

This repo has many historical architecture/experiment docs. Prefer the files listed above when you need current setup and operational behavior.

## License

[The Unlicense](https://unlicense.org/)
