# Active upstream health monitoring (health-endpoint Parts 2+3) — design

**Date:** 2026-07-07
**Status:** approved (Part 2+3 together); to implement
**Origin:** Parts 2 & 3 of the health-endpoint feature (`docs/superpowers/specs/2026-07-06-health-endpoint-design.md`). Part 1 (source-aware health policy) is DONE and merged to main @ `92888df1`. This spec supersedes the Part 2/3 outline sections of that doc with an implementation-ready design.

## Problem / goal

The gateway only knows its upstream's health **passively** — `UpstreamHealthGate` computes a 5xx/4xx EWMA from real proxied traffic (`DegradationAtom`), consumed by `RequestMarkovClassifier` + `DetectionContext.UpstreamHealthy`. Passive EWMA is blind to "upstream up but idle" (no traffic → no samples) and to cold-start. This feature adds **active** monitoring: the gateway discovers the upstream's health endpoint and probes it on a cadence, feeding the SAME `UpstreamHealthy` signal + the SiteHealth pipeline. Active complements passive.

**Scope: Parts 2 (discovery) + 3 (active probe) together.** Part 2 alone (discover + cache a health path) produces a value nothing consumes; Part 3 (the probe) is the consumer and the payoff. They ship as one unit.

**NOT on the memory-pressure critical path.** Part 1 (the unblocker) is merged, so the gateway is already deployable for the memory-pressure run. This is an enhancement, opt-in (`Enabled` default false).

## Constraints

- No `BackgroundService` — recurring work uses `ScheduleCoordinator` (the one canonical hosted service).
- No in-memory-only persistence for state that matters: the discovered endpoint is a derived cache (re-discoverable), so a `ConcurrentDictionary` hot cache is fine, but persist the winner to `ClusterEntity.MetadataJson` so a restart doesn't re-probe every upstream.
- No hardcoded lists in C# (candidate paths in config). No em dashes. All settings configurable.
- Gateway-scoped: this needs `IProxyConfigProvider` (upstream URLs) + `GatewayDbContext`, which live in `Stylobot.Gateway`, not the core lib. It uses core's `ScheduleCoordinator`, `DegradationAtom`, `SignalKeys`.
- Probe politely: short timeout, first-200 wins (stop probing candidates once one answers), infrequent discovery.

## Components

### 1. `UpstreamHealthEndpointDiscovery` (Part 2) — `src/Stylobot.Gateway/Health/UpstreamHealthEndpointDiscovery.cs`
- Injects `IProxyConfigProvider` (`Configuration/YarpConfigProviders.cs`) + an `HttpClient` (short timeout) + `GatewayDbContext`.
- `Task<DiscoveredHealthEndpoint?> DiscoverAsync(string clusterId, string destinationAddress, CancellationToken)`: probes `{destinationAddress}{path}` for each configured candidate path in order, returns the first that yields HTTP 200 (record path + content-type + observed-at). Null if none answer.
- `DiscoveredHealthEndpoint? GetCached(string clusterId)` / cache write: `ConcurrentDictionary<string, DiscoveredHealthEndpoint>` keyed by clusterId; on a successful discover, persist `{ path, contentType, discoveredAtUtc }` into `ClusterEntity.MetadataJson` (merge, don't clobber other metadata). On startup, warm the cache from `ClusterEntity.MetadataJson`.
- Re-discovery: caller (the probe service) invokes `DiscoverAsync` on cache miss or when the cached path stops returning 200.
- `record DiscoveredHealthEndpoint(string Path, string? ContentType, DateTimeOffset DiscoveredAtUtc)`.

### 2. `UpstreamHealthProbeService` (Part 3) — `src/Stylobot.Gateway/Health/UpstreamHealthProbeService.cs`
- Subscribes on ctor: `scheduleCoordinator.Subscribe(TickCadence.Tick1m, "UpstreamHealthProbe", CostHint.Normal, OnTickAsync)` (interval from config). Implements `IDisposable` to dispose the subscription.
- `OnTickAsync(DateTimeOffset now, CancellationToken)`: for each cluster/destination in `IProxyConfigProvider.GetConfig().Clusters`:
  - resolve the discovered endpoint (or trigger `DiscoverAsync` on miss),
  - `GET {address}{discoveredPath}` with the probe timeout, measure latency + status,
  - `degradationAtom.RecordResponse(status, latencyMs, discoveredPath)` — feeds the existing `UpstreamHealthy` EWMA the same way passive traffic does,
  - raise a new `SignalKeys.UpstreamActiveProbeHealthy` (`"upstream.active_probe_healthy"`) with the boolean result (200 within timeout) — the signal overview surfaces on `SbSiteHealth`,
  - on a non-200/timeout for the cached path, clear that cluster's cache so the next tick re-discovers.
- Fault-isolated per cluster (one cluster's probe failure does not stop the others) — `ScheduleCoordinator` already isolates subscriber throws, but also guard per-cluster inside the tick.

### 3. Config — `BotDetection:UpstreamHealth`
`UpstreamHealthMonitorOptions { bool Enabled = false; List<string> CandidatePaths = [/health, /healthz, /livez, /readyz, /ready, /live, /ping, /status, /alive]; int ProbeIntervalSeconds = 60; int ProbeTimeoutMs = 2000; }`. When `Enabled = false`, the probe service does not subscribe (no-op). Registered only in the gateway host.

### 4. Signal — `SignalKeys.UpstreamActiveProbeHealthy = "upstream.active_probe_healthy"` (core `Models/DetectionContext.cs`).

## Division of labour
- FOSS core (mine): discovery, the probe tick, the two integrations (`DegradationAtom.RecordResponse` + the new signal), config, per-cluster persistence.
- Overview: the `SbSiteHealth` dual-row surfacing of `UpstreamActiveProbeHealthy` (their standing commitment — ping when the signal starts being raised).

## Error handling
- Discovery: any probe exception/timeout → treat that candidate as not-health; continue to the next. All candidates fail → return null, cache nothing, the tick retries next cycle.
- Probe: per-cluster try/catch; a hung upstream is bounded by `ProbeTimeoutMs`; a failure records a degraded response + clears the cache.
- No upstream configured / `EmptyConfigProvider` → the tick is a no-op (empty cluster list).

## Testing
- **Discovery:** first-200-wins ordering; cache hit avoids re-probe; re-discover on miss; persistence round-trip via `ClusterEntity.MetadataJson`; all-candidates-fail returns null. Use a stub `HttpMessageHandler` returning 404 for early candidates + 200 for a later one.
- **Probe tick:** feeds `DegradationAtom.RecordResponse` with the probe status/latency; raises `UpstreamActiveProbeHealthy` true on 200 / false on timeout; triggers discovery when uncached; clears cache on cached-path failure; per-cluster fault isolation (one throwing cluster does not stop siblings).
- **Integration:** an active probe recording a healthy 200 keeps `UpstreamHealthGate.IsUpstreamHealthy()` true when passive traffic is absent (proves active complements passive).
- **Config gate:** `Enabled = false` → no subscription, no probes.
