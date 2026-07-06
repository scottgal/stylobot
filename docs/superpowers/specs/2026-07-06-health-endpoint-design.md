# Source-aware health-endpoint handling — design

**Date:** 2026-07-06
**Status:** approved; Part 1 to implement now, Parts 2 & 3 specced-and-deferred
**Origin:** operator decision relayed via the feature agent (`inbox/foss-spec-health-endpoints-discover-monitor-policy.md`), resolving the `/health`→429 thread (`project_health_429_regression`). Unblocks the gateway health-check so it can be deployed for the memory-pressure tests (the point of the atom refactor).

## Problem

The gateway runs bot detection on its own liveness endpoints. A bare-`curl` probe to `/health` (or `/admin/alive`) classifies as `Tool` → `throttle-tools` → **HTTP 429**. Every shipped image `HEALTHCHECK`s with `curl -f .../health` (Gateway/All/Ui/Sidecar/Demo), and external LB/k8s liveness probes use non-browser UAs too, so containers report **unhealthy** and orchestrators restart/evict them. Verified locally (`verify-aot.sh` check #7) and independently on staging (`/admin/alive` → 429).

## Constraints

- **No path bypass.** "Never skip detection" — detection always runs. We change *classification + action*, source-aware, not *whether* detection runs.
- **Correctly classified + named**, per the operator: real probes should read as `Internal` / "Health Probe", not "curl".
- **Source-aware**: expected sources pass; external enumeration of health endpoints stays suspicious.
- Operator-tunable (config), commercial per-domain override via `IEndpointPolicyResolver`.

## Approach

Recognize health-endpoint requests, then branch on caller source using signals that already exist. Expected-source probes are classified `Internal` and allowed; unexpected-source probes stay in detection and are tagged for recon. Expressed as a **source-aware default `EndpointPolicy`**, so it's config-driven and overridable rather than hardcoded.

## Part 1 — source-aware health policy (IMPLEMENT NOW; the unblocker)

### 1. Health-endpoint recognition
- `BotDetection:HealthEndpoints` config (default list: `/health /healthz /livez /readyz /ready /live /ping /status /alive /admin/alive`), a `HealthEndpointCatalog` (path-set matcher).
- A cheap Wave-0 recognizer raises `request.health_endpoint` when the request path matches. New `SignalKeys.HealthEndpoint`.

### 2. Source-aware classification (the core fix)
When `request.health_endpoint` is set, branch on existing source signals:
- **Expected source** — loopback / RFC1918 (`NetworkHelper.IsLocalIp`, already surfaced as `IpIsLocal` by `IpAtom` at `Orchestration/Atoms/IpAtom.cs:129`), configured `TransportTrust:TrustedProxyIps` (`Proxy/TransportHeaderTrust.cs`), and the gateway's own Part-3 probe → classify **`BotType.Internal`**, action **allow/pass** (reuse the existing Internal→`logonly` lane).
  - **Root-cause step (do first):** a loopback `curl /health` currently resolves as `Tool`→throttle, *not* Internal. Before adding anything, trace why the existing `IpIsLocal ? BotType.Internal` promotion (`Orchestration/DetectionLedgerExtensions.cs:244`) doesn't win: either `IpIsLocal` isn't set for the connection in the verify/container path, or the `Tool`-type action in `Enforcement/PostDetectionActionGate.cs` overrides the Internal→`logonly` mapping. Make the health-endpoint+expected-source case authoritative in the action precedence (`PostDetectionActionGate` / `BlockResponseGate.ShouldBlock`).
- **Unexpected/external source** → do **not** exempt: stay in detection, raise `health.endpoint_recon` (new `SignalKeys`), rate-limit, surface on the dashboard. External enumeration stays suspicious.

### 3. Naming
`Services/FingerprintNameComposer.Compose` short-circuits to **"Health Probe"** when `request.health_endpoint` + expected-source (before the Priority-1 claim extraction), so the dashboard names it instead of "curl". Survives name hysteresis.

### 4. Home as a default policy
- Extend `EndpointPolicyRule` (`EndpointPolicies/EndpointPolicyOptions.cs`) with a **`Source` matcher** (`internal` | `external` | `any`, default `any`) + wire it through `ConfigEndpointPolicyResolver.Match` (`EndpointPolicies/IEndpointPolicyResolver.cs`). This lets the source-awareness be expressed *as policy* (the "source-aware default EndpointPolicy" the spec asks for), operator-overridable.
- Seed a built-in default rule set for the health paths (internal→allow, external→recon/rate-limit). Commercial per-domain override rides the existing `IEndpointPolicyResolver`.

### Testing (Part 1)
- Unit: catalog path-match; source branch (loopback/RFC1918/trusted-proxy → Internal/allow; public → recon+detect); naming → "Health Probe"; `EndpointPolicyRule.Source` matcher.
- Integration: loopback `curl /health` → 200, classified `Internal`/"Health Probe"; external `curl /health` → stays detected + `health_endpoint_recon`.
- Regression: re-run `src/Mostlylucid.BotDetection.Console/verify-aot.sh` → check #7 (`/health` healthy) green; full test suite green.

## Part 2 — upstream health-endpoint discovery (DEFERRED; tracked)

Per upstream/site, probe the candidate list, learn which path 200s + response shape, cache per-cluster gateway-local. Re-discover on a slow tick / on miss. Seams: YARP `IProxyConfig.Clusters[].Destinations[].Address` (`Stylobot.Gateway/Configuration/YarpConfigProviders.cs`) for the base URL; per-cluster state via `GatewayDbContext` `ClusterEntity.MetadataJson` / `DestinationEntity.Health`. New `IUpstreamHealthEndpointDiscovery`.

## Part 3 — active monitoring (DEFERRED; tracked)

A `ScheduleCoordinator.Subscribe(Tick1m, "UpstreamHealthProber", …)` (`Scheduling/ScheduleCoordinator.cs`; NOT a BackgroundService) hits the discovered endpoint from the gateway, records latency/status, feeds the **existing** `SignalKeys.UpstreamHealthy` (`RateLimit/UpstreamHealthGate.cs`, `Models/DetectionContext.cs:1074`) + `DegradationStoreSampler`/SiteHealth pipeline (`UI/Services/DegradationStoreSampler.cs`, `Api/Endpoints/SiteHealthHistoryEndpoint.cs`, `UI/ViewComponents/SbSiteHealthViewComponent.cs`). Active complements passive (catches "up but idle" + cold-start). Surface the discovered endpoint + live status on `SbSiteHealth`. The gateway's own probe is the archetypal Part-1 "expected source", so the three parts self-reinforce.

## Division of labour

FOSS core (mine): the `EndpointPolicy` `Source` matcher + default, health recognizer, `Internal`/"Health Probe" classification+name, Part-2 discovery, Part-3 active-probe tick feeding `UpstreamHealthy`/SiteHealth. Commercial (feature agent): per-domain policy override via `IEndpointPolicyResolver`, `SbSiteHealth` surfacing polish; owns end-to-end verification.

## Phasing

1. **Part 1 now** — unblocks the memory-pressure run. Ships as its own commit(s) + tests.
2. **Parts 2 & 3 later** — tracked as follow-up tasks; not on the memory-pressure critical path.
