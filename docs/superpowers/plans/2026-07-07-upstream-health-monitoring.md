# Active Upstream Health Monitoring (Parts 2+3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** The gateway discovers each upstream's health endpoint and actively probes it on a cadence, feeding the existing `UpstreamHealthy` EWMA + SiteHealth pipeline so "upstream up but idle" and cold-start are caught (passive EWMA can't see them).

**Architecture:** Two gateway-scoped services. `UpstreamHealthEndpointDiscovery` probes candidate paths per YARP cluster destination (first-200 wins), caches the winner (in-memory + persisted to `ClusterEntity.MetadataJson`). `UpstreamHealthProbeService` runs on a `ScheduleCoordinator` tick, probes the discovered endpoint per cluster, feeds `DegradationAtom.RecordResponse` and persists the last result to `DestinationEntity.Health`. Opt-in.

**Tech Stack:** .NET 10, YARP (`IProxyConfigProvider`), EF Core (`GatewayDbContext`, SQLite), `ScheduleCoordinator`, `DegradationAtom`, xUnit (`Stylobot.Gateway.Tests`), stub `HttpMessageHandler`.

**Spec:** `docs/superpowers/specs/2026-07-07-upstream-health-monitoring-design.md`.

## Global Constraints
- No `BackgroundService` — use `IScheduleCoordinator.Subscribe(TickCadence, name, CostHint, Func<DateTimeOffset, CancellationToken, Task>)`.
- `ConcurrentDictionary` hot cache is fine (derived, re-discoverable), but persist the discovered path to `ClusterEntity.MetadataJson` (survives restart). No em dashes. No hardcoded lists in C# (candidate paths in config).
- Gateway-scoped (`Stylobot.Gateway`); uses core's `ScheduleCoordinator` + `DegradationAtom`. Tests in `Stylobot.Gateway.Tests`.
- **Design correction vs spec §Component-2/4:** do NOT raise a `SignalKeys.UpstreamActiveProbeHealthy` (a tick-service has no request `SignalSink`). Feed `DegradationAtom.RecordResponse(status, latencyMs, path)` (flows to `UpstreamHealthy` + the existing `DegradationStoreSampler`/`DegradationSnapshot` SiteHealth pipeline) AND persist the last active-probe result to `DestinationEntity.Health` (existing field) for overview's SbSiteHealth row. No new `SignalKeys` constant.
- Opt-in: `BotDetection:UpstreamHealth:Enabled` default false → the probe service does not subscribe.

## Confirmed seams (verbatim)
- `IProxyConfigProvider.GetConfig().Clusters : IReadOnlyList<ClusterConfig>`; `ClusterConfig.ClusterId`, `.Destinations : IReadOnlyDictionary<string, DestinationConfig>`; `DestinationConfig.Address` (`Stylobot.Gateway/Configuration/YarpConfigProviders.cs`).
- `DegradationAtom.RecordResponse(int statusCode, long latencyMs, string path)` (`src/Mostlylucid.BotDetection/RateLimit/DegradationAtom.cs:101`). Registered in core DI (`BotDetectionModule`); resolvable in the gateway host.
- `ClusterEntity.MetadataJson : string?`, `DestinationEntity { ClusterId, DestinationId, Address, Health : string? }` (`Stylobot.Gateway/Data/GatewayDbContext.cs:105-129`).
- `IScheduleCoordinator.Subscribe(...)` returns `IDisposable` (`Mostlylucid.Common/Scheduling/IScheduleCoordinator.cs:94`); cadences in `TickCadence`.

---

### Task 1: Options + DI scaffolding
**Files:** Create `src/Stylobot.Gateway/Health/UpstreamHealthMonitorOptions.cs`; Modify `src/Stylobot.Gateway/Configuration/ServiceCollectionExtensions.cs` (bind options + register the two services, following the existing `AddOptions<T>().BindConfiguration(SectionName)` pattern used elsewhere in that file); Test `src/Stylobot.Gateway.Tests/Health/UpstreamHealthMonitorOptionsTests.cs`.
**Interfaces — Produces:** `UpstreamHealthMonitorOptions { const string SectionName = "BotDetection:UpstreamHealth"; bool Enabled = false; List<string> CandidatePaths = [".../health", "/healthz", "/livez", "/readyz", "/ready", "/live", "/ping", "/status", "/alive"]; int ProbeIntervalSeconds = 60; int ProbeTimeoutMs = 2000; }`.
- [ ] Step 1: Failing test — bind a config with `BotDetection:UpstreamHealth:Enabled=true` + custom `CandidatePaths:0=/hc`, assert `IOptions<UpstreamHealthMonitorOptions>.Value` reflects it. Run → FAIL. Implement the options class + `AddOptions().BindConfiguration(SectionName)`. Run → PASS. Commit.

### Task 2: UpstreamHealthEndpointDiscovery
**Files:** Create `src/Stylobot.Gateway/Health/UpstreamHealthEndpointDiscovery.cs`, `.../DiscoveredHealthEndpoint.cs`; Test `src/Stylobot.Gateway.Tests/Health/UpstreamHealthEndpointDiscoveryTests.cs`.
**Interfaces — Produces:** `record DiscoveredHealthEndpoint(string Path, string? ContentType, DateTimeOffset DiscoveredAtUtc)`; `interface IUpstreamHealthEndpointDiscovery { Task<DiscoveredHealthEndpoint?> DiscoverAsync(string clusterId, string destinationAddress, CancellationToken ct); DiscoveredHealthEndpoint? GetCached(string clusterId); void Invalidate(string clusterId); }`. Ctor takes `HttpClient` (or `IHttpClientFactory` named client with `ProbeTimeoutMs`), `IOptions<UpstreamHealthMonitorOptions>`, `GatewayDbContext` factory (`IDbContextFactory<GatewayDbContext>` — confirm the gateway's DbContext registration pattern), `ILogger`.
- [ ] Step 1: Failing test — stub `HttpMessageHandler` returns 404 for `/health,/healthz` and 200 for `/livez`; `DiscoverAsync("c","http://up")` returns `DiscoveredHealthEndpoint` with `Path=="/livez"`. Second: all-404 returns null. Third: `GetCached` returns the discovered value after a discover; `Invalidate` clears it. Fourth: discover persists `{path,...}` into `ClusterEntity.MetadataJson` (merge, not clobber) and a fresh instance warms `GetCached` from it.
- [ ] Step 2-5: Run FAIL → implement (probe candidates in order, first-200 wins, short timeout per probe via the client, cache in `ConcurrentDictionary<string,DiscoveredHealthEndpoint>`, persist/merge into `ClusterEntity.MetadataJson` under a `healthEndpoint` key, warm cache lazily on first `GetCached` miss) → Run PASS → Commit.

### Task 3: UpstreamHealthProbeService
**Files:** Create `src/Stylobot.Gateway/Health/UpstreamHealthProbeService.cs`; Test `src/Stylobot.Gateway.Tests/Health/UpstreamHealthProbeServiceTests.cs`.
**Interfaces — Consumes:** Task 2's `IUpstreamHealthEndpointDiscovery`, Task 1's options, `IProxyConfigProvider`, `DegradationAtom`, `IScheduleCoordinator`, the same `HttpClient`, `GatewayDbContext` factory. **Produces:** `class UpstreamHealthProbeService : IDisposable` (ctor subscribes to `TickCadence.Tick1m` when `Enabled`; `OnTickAsync` is internal-visible for tests).
- [ ] Step 1: Failing tests — (a) `OnTickAsync` with one cluster whose discovered endpoint returns 200 calls `DegradationAtom.RecordResponse(200, latency, path)` and writes `DestinationEntity.Health="healthy"`; (b) a timeout/500 records the failure via `RecordResponse` AND calls `discovery.Invalidate(clusterId)` so the next tick re-discovers; (c) uncached cluster triggers `DiscoverAsync`; (d) one cluster throwing does not stop a sibling cluster's probe (per-cluster try/catch); (e) `Enabled=false` → ctor does not `Subscribe` (assert `IScheduleCoordinator.Subscribe` not called via a mock). Use a mock `DegradationAtom` (or the real one + assert its EMA moved) and a stub `IProxyConfigProvider` with two clusters.
- [ ] Step 2-5: Run FAIL → implement (`OnTickAsync`: for each cluster, first destination address, resolve/trigger discovery, `GET {address}{path}` with `ProbeTimeoutMs`, `RecordResponse`, persist `DestinationEntity.Health`, `Invalidate` on non-200, per-cluster try/catch/log; ctor: subscribe only when `Enabled`, store the `IDisposable`, dispose it in `Dispose`) → Run PASS → Commit.

### Task 4: Wire into the gateway host + integration
**Files:** Modify `src/Stylobot.Gateway/Program.cs` (register the two services + the named `HttpClient`; instantiate `UpstreamHealthProbeService` eagerly so its ctor subscribes — mirror how other `ScheduleCoordinator` subscribers are booted, e.g. `BotDetectionHostedSingletonsBootstrap`); Test `src/Stylobot.Gateway.Tests/Health/UpstreamHealthIntegrationTests.cs`.
- [ ] Step 1: Failing test — with `Enabled=true`, a stub upstream (via the injected handler) answering 200 on `/healthz`: run one `OnTickAsync`, assert `UpstreamHealthGate.IsUpstreamHealthy()` is true with NO passive traffic recorded (proves active complements passive). Also assert `Enabled=false` → the service is registered but never subscribes/probes.
- [ ] Step 2-5: Run FAIL → wire the registration/bootstrap → Run PASS → build the gateway (`dotnet build src/Stylobot.Gateway -c Release`) → run `Stylobot.Gateway.Tests` full → Commit.
- [ ] Step 6: Ping overview — the active-probe result now lands in `DestinationEntity.Health` + the SiteHealth EWMA; they can add the SbSiteHealth active-probe row (their standing item).

## Self-Review
- **Spec coverage:** discovery→T2; probe+RecordResponse+persist→T3; config→T1; host wiring→T4. The spec's `UpstreamActiveProbeHealthy` signal is intentionally replaced (documented in Global Constraints) by `RecordResponse` + `DestinationEntity.Health` — update the spec's Component-2/4 wording during T3.
- **Placeholders:** the `IDbContextFactory` / `IHttpClientFactory` registration patterns are named "confirm against the gateway's existing pattern" (a locate-direction, not a vague TODO); resolved at implementation by reading `ServiceCollectionExtensions.cs` + `Program.cs`.
- **Type consistency:** `IUpstreamHealthEndpointDiscovery` (DiscoverAsync/GetCached/Invalidate), `DiscoveredHealthEndpoint(Path,ContentType,DiscoveredAtUtc)`, `UpstreamHealthMonitorOptions`, `DegradationAtom.RecordResponse(int,long,string)` used consistently across T2-T4.
