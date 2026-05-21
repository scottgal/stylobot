# Documentation Index

## Start Here

- [`README.md`](../README.md) - Repo overview, install, pricing, detection surface
- [`QUICKSTART.md`](QUICKSTART.md) - Local runbook for demo and gateway
- [`DOCKER_SETUP.md`](DOCKER_SETUP.md) - Container deployment
- [`ARCHITECTURE.md`](ARCHITECTURE.md) - Component and flow overview
- [`OPERATIONS.md`](OPERATIONS.md) - Day-2 operations and rollout pattern
- [`TESTING.md`](TESTING.md) - Test strategy
- [`RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md) - Pre/post release checks

## CLI (StyloBot Console)

- [`Mostlylucid.BotDetection.Console/README.md`](../Mostlylucid.BotDetection.Console/README.md) - Full CLI reference, all flags, daemon mode, LLM providers
- Run `stylobot man` for the built-in manual page

## Configuration

- [`configuration.md`](../Mostlylucid.BotDetection/docs/configuration.md) - Configuration guide
- [`configuration-reference.md`](../Mostlylucid.BotDetection/docs/configuration-reference.md) - Full options reference
- [`appsettings.typical.json`](../Mostlylucid.BotDetection/docs/appsettings.typical.json) - Typical config
- [`appsettings.full.json`](../Mostlylucid.BotDetection/docs/appsettings.full.json) - All options
- [`action-policies.md`](../Mostlylucid.BotDetection/docs/action-policies.md) - Block, throttle, challenge, logonly

## Detection

- [`detection-strategies.md`](../Mostlylucid.BotDetection/docs/detection-strategies.md) - How detection works
- [`learning-and-reputation.md`](../Mostlylucid.BotDetection/docs/learning-and-reputation.md) - Adaptive learning
- [`ai-detection.md`](../Mostlylucid.BotDetection/docs/ai-detection.md) - Heuristic + LLM detection
- [`bdf-system-guide.md`](../Mostlylucid.BotDetection/docs/bdf-system-guide.md) - Bot Detection Format scenarios and replay

## Detector Reference

- [`user-agent-detection.md`](../Mostlylucid.BotDetection/docs/user-agent-detection.md)
- [`header-detection.md`](../Mostlylucid.BotDetection/docs/header-detection.md)
- [`ip-detection.md`](../Mostlylucid.BotDetection/docs/ip-detection.md)
- [`behavioral-analysis.md`](../Mostlylucid.BotDetection/docs/behavioral-analysis.md)
- [`advanced-behavioral-detection.md`](../Mostlylucid.BotDetection/docs/advanced-behavioral-detection.md)
- [`cache-behavior-detection.md`](../Mostlylucid.BotDetection/docs/cache-behavior-detection.md)
- [`security-tools-detection.md`](../Mostlylucid.BotDetection/docs/security-tools-detection.md)
- [`client-side-fingerprinting.md`](../Mostlylucid.BotDetection/docs/client-side-fingerprinting.md)
- [`version-age-detection.md`](../Mostlylucid.BotDetection/docs/version-age-detection.md)
- [`AdvancedFingerprintingDetectors.md`](../Mostlylucid.BotDetection/docs/AdvancedFingerprintingDetectors.md) - TLS/TCP/HTTP2/QUIC
- [`proof-of-work-challenge.md`](../Mostlylucid.BotDetection/docs/proof-of-work-challenge.md) - PoW challenge system

## Integration

- [`Mostlylucid.BotDetection/README.md`](../Mostlylucid.BotDetection/README.md) - NuGet middleware
- [`yarp-integration.md`](../Mostlylucid.BotDetection/docs/yarp-integration.md) - YARP reverse proxy
- [`deployment-guide.md`](../Mostlylucid.BotDetection/docs/deployment-guide.md) - Production deployment

## Gateway & Dashboard

- [`Stylobot.Gateway/README.md`](../src/Stylobot.Gateway/README.md) - Docker YARP gateway
- [`Stylobot.Gateway/docs/DOCKERHUB.md`](../src/Stylobot.Gateway/docs/DOCKERHUB.md) - Docker Hub
- [`Mostlylucid.BotDetection.UI/README.md`](../src/Mostlylucid.BotDetection.UI/README.md) - Dashboard + SignalR
- [`admin-endpoints.md`](admin-endpoints.md) - Bearer-token `/admin/reload` + `/admin/restart` (off by default)
- [`REVERSE_PROXY_SIGNALS.md`](REVERSE_PROXY_SIGNALS.md) - Inject client TLS / HTTP version / ASN from Cloudflare / Caddy / nginx

## Other Components

- [`Mostlylucid.BotDetection.Demo/README.md`](../Mostlylucid.BotDetection.Demo/README.md) - Interactive demo
- [`Mostlylucid.Common/README.md`](../Mostlylucid.Common/README.md) - Shared utilities
- [`Mostlylucid.GeoDetection/README.md`](../Mostlylucid.GeoDetection/README.md) - Geographic routing
- [`bot-signatures/README.md`](../bot-signatures/README.md) - BDF test scenarios

## Notes

- For runtime behavior, trust `Program.cs`, appsettings files, and compose files first, then docs.
- Website and portal docs are in the [`stylobot-commercial`](https://github.com/scottgal/stylobot-commercial) repo.
