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

### 2a. Root-cause fix (do FIRST — confirmed by overview, broader than /health)
A loopback `curl /health` currently resolves as `Tool`→throttle, not Internal, because the `IpIsLocal ? BotType.Internal` promotion (`Orchestration/DetectionLedgerExtensions.cs:244`) never fires. **Confirmed root cause:** `ledger.MergedSignals` (Ephemeral 2.8.1 `DetectionLedger.cs:136-150`) is built exclusively from `contribution.Signals`; `IpAtom.cs:131` only calls `sink.Raise($"{IpIsLocal}:true")` and never populates `contribution.Signals`, so the signal lives only in the `SignalSink`. `preSignals.TryGetValue(IpIsLocal,…)` fails on **missing key**, `is true` never runs, promotion is dead. **Five sibling checks are dead the same way** (all sink-only, read via `is true` on `preSignals`): `UserAgentIsBot`/declaredBot (`:114`), `IpIsLocal` (`:154`, `:490`), `ReputationFastAbortActive` (`:835`), `SecurityToolDetected` (`:933`). Tests miss it because test callers pass `premergedSignals` with hand-built boxed bools.

**Fix (overview-blessed, atomic for all six):** thread the request `SignalSink` into `ToAggregatedEvidence` (add optional `SignalSink? sink = null`; `BotDetectionOrchestrator.cs:111/174` has it) and replace each `preSignals.TryGetValue(k,…) && v is true` with `sink?.ReadBoolHint(k, fallback:false) ?? false` (`Orchestration/Atoms/SignalHintExtensions.cs:23`). Do NOT mirror the signal into `contribution.Signals` per-atom (workaround; five other signals read the same broken way — fix the reader). `premergedSignals`-passing test callers keep working. **Rollout note:** the five siblings start firing in prod for the first time (e.g. the `DeclaredBot` verdict-honest override at `:114`); eyeball staging after landing — guarded rollout nice-to-have, not blocking.

### 2b. Shape-AND-source classification (the core fix)
When `request.health_endpoint` is set, classify **positively on probe shape AND expected source** (per overview/feature: source gates, shape confirms — a trusted-source IP alone is not sufficient, or an on-network attacker hitting `/health` gets a free Allow):
- **Expected source** — loopback / RFC1918 read from the **sink** via `ReadBoolHint(IpIsLocal)` (NOT `MergedSignals`, or we rebuild the bug above), configured `TransportTrust:TrustedProxyIps` (`Proxy/TransportHeaderTrust.cs`), and the gateway's own Part-3 probe. **AND**
- **Probe shape** — a positive match on probe UA family (`kube-probe`, `Go-http-client`, `curl`, `wget`, `docker`) + minimal HTTP semantics (no browser `Sec-Fetch-*`/`Accept: text/html` navigation shape). A browser-shaped request from a trusted IP does NOT qualify.
- Both hold → classify **`BotType.Internal`**, name **"Health Probe"**, action **allow/pass** (reuse the existing Internal→`logonly` lane, now live after 2a).
- **Otherwise** (external source, or trusted source but non-probe shape) → do **not** exempt: stay in detection, raise `health.endpoint_recon` (new `SignalKeys`), and **feed `intent.threat_score`** — a small nudge into the intent pool that `ProjectHoneypotAtom`/`HoneypotLinkAtom`/`EndpointHistoryAtom` feed (mirror their magnitude), so co-occurring recon on the same source amplifies the verdict, not just per-path rate-limit.

### 3. Naming
`Services/FingerprintNameComposer.Compose` short-circuits to **"Health Probe"** when `request.health_endpoint` + expected-source (before the Priority-1 claim extraction), so the dashboard names it instead of "curl". Survives name hysteresis.

### 4. Home as a default policy
- Extend `EndpointPolicyRule` (`EndpointPolicies/EndpointPolicyOptions.cs`) with a **`Source` matcher** (`internal` | `external` | `any`, default `any`) + wire it through `ConfigEndpointPolicyResolver.Match` (`EndpointPolicies/IEndpointPolicyResolver.cs`). `Source` is part of the **public** `EndpointPolicyRule` shape the resolver returns, so commercial per-domain health policies can be source-aware too (feature ask). Options-driven per `feedback_all_settings_configurable`: expected-source = loopback + a config list of CIDRs / UA-family matchers, so cluster/k8s topologies extend without a rebuild.
- Seed a built-in default rule set for the health paths (internal+probe-shape→allow, else→recon+threat_score+rate-limit), but keep it **overridable** (last-writer-per-domain wins; not hard-coded ahead of resolution — feature ask). Commercial per-domain override rides the existing `IEndpointPolicyResolver`. Ping feature to update the topology doc's EndpointPolicy references when the rule shape changes.

### Testing (Part 1)
- **Root-cause fix (2a):** unit test proving the six `is true` checks now fire off sink signals through the production `ToAggregatedEvidence` path (no `premergedSignals` passed) — e.g. a sink with `IpIsLocal:true` yields `isLocalIp=true`; likewise `SecurityToolDetected`, declaredBot. This is the test the current suite lacks (it bypasses via `premergedSignals`).
- Unit: catalog path-match; shape-AND-source branch; naming → "Health Probe"; `EndpointPolicyRule.Source` matcher.
- Integration acceptance (feature owns end-to-end):
  1. Loopback/Docker `curl -f /health` (probe shape + local source) → **200, `Internal`/"Health Probe"**.
  2. Real k8s HTTP liveness probe (`kube-probe` UA) → 200 Internal (tcpSocket probes already immune; this fixes the HTTP path).
  3. **Shape guard:** a browser-shaped request (`Sec-Fetch-*`, `Accept: text/html`) to `/health` from a trusted-source IP is **NOT** auto-allowed as Health Probe — proves shape+source, not source-only.
  4. External `curl /health` → stays detected + `health.endpoint_recon`, and **nudges `intent.threat_score`** (verify co-occurring recon on the same source amplifies the bot verdict, not just per-path rate-limit).
  5. **No stat pollution:** the Health Probe (BotType `Internal`) is excluded from dashboard widget totals (ties #34) — verify it doesn't inflate traffic counts.
- Regression: re-run `src/Mostlylucid.BotDetection.Console/verify-aot.sh` → check #7 (`/health` healthy) green; full test suite green; eyeball staging for the newly-live sibling checks (2a rollout note).

## Part 2 — upstream health-endpoint discovery (DEFERRED; tracked)

Per upstream/site, probe the candidate list, learn which path 200s + response shape, cache per-cluster gateway-local. Re-discover on a slow tick / on miss. Seams: YARP `IProxyConfig.Clusters[].Destinations[].Address` (`Stylobot.Gateway/Configuration/YarpConfigProviders.cs`) for the base URL; per-cluster state via `GatewayDbContext` `ClusterEntity.MetadataJson` / `DestinationEntity.Health`. New `IUpstreamHealthEndpointDiscovery`.

## Part 3 — active monitoring (DEFERRED; tracked)

A `ScheduleCoordinator.Subscribe(Tick1m, "UpstreamHealthProber", …)` (`Scheduling/ScheduleCoordinator.cs`; NOT a BackgroundService) hits the discovered endpoint from the gateway, records latency/status, feeds the **existing** `SignalKeys.UpstreamHealthy` (`RateLimit/UpstreamHealthGate.cs`, `Models/DetectionContext.cs:1074`) + `DegradationStoreSampler`/SiteHealth pipeline (`UI/Services/DegradationStoreSampler.cs`, `Api/Endpoints/SiteHealthHistoryEndpoint.cs`, `UI/ViewComponents/SbSiteHealthViewComponent.cs`). Active complements passive (catches "up but idle" + cold-start). Surface the discovered endpoint + live status on `SbSiteHealth`. The gateway's own probe is the archetypal Part-1 "expected source", so the three parts self-reinforce.

## Division of labour

FOSS core (mine): the `EndpointPolicy` `Source` matcher + default, health recognizer, `Internal`/"Health Probe" classification+name, Part-2 discovery, Part-3 active-probe tick feeding `UpstreamHealthy`/SiteHealth. Commercial (feature agent): per-domain policy override via `IEndpointPolicyResolver`, `SbSiteHealth` surfacing polish; owns end-to-end verification.

## Phasing

1. **Part 1 now** — unblocks the memory-pressure run. Ships as its own commit(s) + tests.
2. **Parts 2 & 3 later** — tracked as follow-up tasks; not on the memory-pressure critical path.
