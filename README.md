# StyloBot
by ***mostly*lucid**

Bot detection framework for ASP.NET Core with wave-based detection, adaptive learning, and reverse-proxy integration.

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

## Detection Surface

The default policy runs fast contributors first and can escalate based on policy config. In demo and gateway configs, the active policies include contributors such as:

- User-Agent, Header, IP, SecurityTool
- Behavioral, AdvancedBehavioral, CacheBehavior
- ClientSide, Inconsistency, VersionAge, ReputationBias
- FastPathReputation, ProjectHoneypot, HoneypotLink
- TLS/TCP/HTTP2 fingerprinting and cross-layer correlation
- Cluster detection (label propagation + FFT spectral analysis)
- Country reputation tracking (time-decayed bot rates)
- Heuristic scoring (and optional LLM path when configured)

Real contributor lists are controlled by `BotDetection:Policies` in each app config.

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

## Core Product Differentiators

- Speed with intelligence: low-latency request handling with explainable detector evidence.
- Temporal behavior resolution: cross-request, windowed signal correlation for stronger bot/human discrimination.
- Powered by `mostlylucid.ephemeral`: efficient ephemeral state and coordinator patterns that enable across-time analysis without heavy per-request latency.
- Operator-first control: you decide policy actions and rollout strategy.

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

Detector docs:

- [`Mostlylucid.BotDetection/docs/user-agent-detection.md`](Mostlylucid.BotDetection/docs/user-agent-detection.md)
- [`Mostlylucid.BotDetection/docs/header-detection.md`](Mostlylucid.BotDetection/docs/header-detection.md)
- [`Mostlylucid.BotDetection/docs/ip-detection.md`](Mostlylucid.BotDetection/docs/ip-detection.md)
- [`Mostlylucid.BotDetection/docs/behavioral-analysis.md`](Mostlylucid.BotDetection/docs/behavioral-analysis.md)
- [`Mostlylucid.BotDetection/docs/advanced-behavioral-detection.md`](Mostlylucid.BotDetection/docs/advanced-behavioral-detection.md)
- [`Mostlylucid.BotDetection/docs/client-side-fingerprinting.md`](Mostlylucid.BotDetection/docs/client-side-fingerprinting.md)
- [`Mostlylucid.BotDetection/docs/version-age-detection.md`](Mostlylucid.BotDetection/docs/version-age-detection.md)
- [`Mostlylucid.BotDetection/docs/security-tools-detection.md`](Mostlylucid.BotDetection/docs/security-tools-detection.md)
- [`Mostlylucid.BotDetection/docs/ai-detection.md`](Mostlylucid.BotDetection/docs/ai-detection.md)
- [`Mostlylucid.BotDetection/docs/cluster-detection.md`](Mostlylucid.BotDetection/docs/cluster-detection.md)
- [`Mostlylucid.BotDetection/docs/training-data-api.md`](Mostlylucid.BotDetection/docs/training-data-api.md)

## Notes on Existing Docs

This repo has many historical architecture/experiment docs. Prefer the files listed above when you need current setup and operational behavior.

## License

[The Unlicense](https://unlicense.org/)
