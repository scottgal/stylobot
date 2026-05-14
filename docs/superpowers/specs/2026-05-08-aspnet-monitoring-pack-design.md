# ASP.NET MonitoringPack Design

**Date:** 2026-05-08 (revised 2026-05-14)
**Status:** Approved
**Scope:** FOSS product (`Mostlylucid.BotDetection` + `Mostlylucid.BotDetection.UI`)

> **Note (2026-05-14):** brief detour considered moving the whole pack to commercial; reversed. The pack itself is FOSS (collection + store + dashboard tab + read endpoints). Live editing of pack configuration without restart is the commercial add-on, gated by `stylobot.monitoring.aspnet.hot-reload` and priced at $5/mo. Companion docs: `stylobot-commercial/docs/superpowers/specs/2026-05-14-aspnet-monitoring-pack-commercial-design.md` and the matching plan.

---

## Overview

A second pack category alongside simulation packs. A monitoring pack declares which
meters and instruments to collect, at what interval. `AspNetMonitoringPack` is the
reference implementation: it collects StyloBot's own operational meters plus optional
ASP.NET host meters. Future packs (Magento API, etc.) follow the same `IMonitoringPack`
contract but collect from external HTTP APIs instead of process meters.

The pack concept exists to make StyloBot extensible without growing the core library.
Each pack is independently registered, independently configurable, and independently
displayable in the dashboard.

---

## Architecture

```
[Local mode -middleware/all-in-one deployment]
Single process: detection + dashboard
  MeterListenerService (BackgroundService)
    └─ attaches System.Diagnostics.Metrics.MeterListener to in-process meters
    └─ accumulates counter deltas, gauge values, histogram percentiles per bucket
    └─ writes to IMetricSnapshotStore every CollectionInterval (default 60s)

[Remote mode -YARP gateway + separate dashboard]
Gateway process:
  GatewayMeterAccumulator (BackgroundService)
    └─ MeterListener accumulates values in-memory ring buffer
    └─ MetricsSnapshotController serves GET /_sb/metrics/snapshot (internal only)

Dashboard process:
  RemoteMetricCollector (BackgroundService)
    └─ polls gateway /_sb/metrics/snapshot on schedule
    └─ writes to IMetricSnapshotStore

Both modes:
  IMetricSnapshotStore ─► SqliteMetricSnapshotStore (same DB as dashboard events)
    └─ queried by Metrics tab API endpoints
    └─ invalidated via SignalR "metrics" broadcast from DashboardSummaryBroadcaster
    └─ pruned on 7-day schedule alongside detections
```

Mode is a deployment concern, not a pack concern. The pack only declares what to collect.

---

## New Files

### `Mostlylucid.BotDetection/MonitoringPacks/`

| File | Purpose |
|------|---------|
| `IMonitoringPack.cs` | Interface + `MeterCollectionGroup` + `InstrumentCollectionSpec` + `CollectedValueType` enum |
| `AspNetMonitoringPack.cs` | Reference implementation, StyloBot meters + optional ASP.NET host meters |
| `MeterListenerService.cs` | BackgroundService -local mode, attaches MeterListener, writes to IMetricSnapshotStore |
| `GatewayMeterAccumulator.cs` | BackgroundService -remote mode, accumulates in-memory, served via HTTP |

### `Mostlylucid.BotDetection.UI/Services/`

| File | Purpose |
|------|---------|
| `IMetricSnapshotStore.cs` | Read/write interface for metric snapshots |
| `SqliteMetricSnapshotStore.cs` | SQLite implementation, same connection string as SqliteDashboardEventStore |
| `RemoteMetricCollector.cs` | BackgroundService -polls gateway metrics endpoint, writes to store |

### `Mostlylucid.BotDetection.Api/Controllers/`

| File | Purpose |
|------|---------|
| `MetricsSnapshotController.cs` | GET `/_sb/metrics/snapshot` -internal endpoint for remote mode, not publicly routed |

### Dashboard (in `Mostlylucid.BotDetection.UI`)

| File | Purpose |
|------|---------|
| `TagHelpers/Dashboard/SbMetricsTabTagHelper.cs` | Tag helper for Metrics tab |
| `ViewComponents/Dashboard/SbMetricsTabViewComponent.cs` | View component |
| `Views/Shared/Components/SbMetricsTab/Default.cshtml` | Razor partial, two sections |

---

## Modified Files

| File | Change |
|------|--------|
| `Extensions/ServiceCollectionExtensions.cs` | Register IMonitoringPack, MeterListenerService, IMetricSnapshotStore |
| `UI/Configuration/StyloBotDashboardOptions.cs` | Add MonitoringPackOptions (Mode, IncludeAspNetHostMeters, DashboardUrl for remote) |
| `UI/Services/SqliteDashboardEventStore.cs` | Add `metric_snapshots` table creation to schema init |
| `UI/Services/DashboardSummaryBroadcaster.cs` | Add `BroadcastInvalidation("metrics")` to broadcast loop |
| `UI/Services/DetectionNarrativeBuilder.cs` | No change needed (metrics are not detectors) |
| Dashboard layout | Add Metrics tab alongside Sessions/Countries/etc. |

---

## Pack Interface

```csharp
public interface IMonitoringPack
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    TimeSpan CollectionInterval { get; }
    IReadOnlyList<MeterCollectionGroup> MeterGroups { get; }
}

public sealed record MeterCollectionGroup(
    string MeterName,
    IReadOnlyList<InstrumentCollectionSpec> Instruments);

public sealed record InstrumentCollectionSpec(
    string InstrumentName,
    CollectedValueType ValueType,
    IReadOnlyList<KeyValuePair<string, string>>? TagFilter = null);

public enum CollectedValueType
{
    Counter,          // delta per bucket → stored as rate/min
    Gauge,            // current value at snapshot time
    Histogram_P50,
    Histogram_P95,
    Histogram_P99
}
```

`MeterListenerService` maps each `InstrumentCollectionSpec` to a `MeterListener` subscription.
Counter instruments accumulate deltas between ticks, then divide by bucket width to produce rate.
Histogram instruments maintain a sorted list of observations per bucket and compute percentiles on flush.

---

## AspNetMonitoringPack Meter Inventory

### StyloBot meters (always enabled)

Meter name: `Mostlylucid.BotDetection`

| Instrument | Collected As | Dashboard label |
|---|---|---|
| `botdetection.requests.total` | Counter | Requests/min |
| `botdetection.bots.detected` | Counter | Bots/min |
| `botdetection.humans.detected` | Counter | Humans/min |
| `botdetection.errors.total` | Counter | Errors/min |
| `botdetection.detection.duration` | Histogram_P50, Histogram_P95 | Detection latency |
| `botdetection.confidence.average` | Gauge | Avg confidence |
| `botdetection.weightstore.cache.hits` | Counter | Cache hits/min |
| `botdetection.weightstore.cache.misses` | Counter | Cache misses/min |

### ASP.NET host meters (opt-in via `IncludeAspNetHostMeters = true`)

Meter names: `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Http.Connections`, `System.Runtime`

| Instrument | Collected As | Dashboard label |
|---|---|---|
| `http.server.request.duration` | Histogram_P50, Histogram_P95 | HTTP latency |
| `http.server.active_requests` | Gauge | Active requests |
| `dotnet.gc.heap.total_allocated` | Counter | GC allocated/min |
| `dotnet.process.cpu.time` | Counter | CPU time/min |
| `dotnet.thread_pool.thread.count` | Gauge | Thread pool threads |

Inspired by the ASP.NET Grafana community dashboard (grafana.com/grafana/dashboards/19924).

---

## SQLite Schema

Added to the dashboard SQLite DB (same file as `detections`, `signatures`):

```sql
CREATE TABLE IF NOT EXISTS metric_snapshots (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    bucket_time TEXT    NOT NULL,   -- ISO-8601, truncated to 1-minute boundary
    pack_id     TEXT    NOT NULL,   -- e.g. "aspnet-monitoring"
    meter_name  TEXT    NOT NULL,   -- e.g. "Mostlylucid.BotDetection"
    instrument  TEXT    NOT NULL,   -- e.g. "botdetection.requests.total"
    tags        TEXT,               -- JSON {"detector":"UserAgent"} or NULL
    value       REAL    NOT NULL,
    value_type  TEXT    NOT NULL    -- 'rate', 'gauge', 'p50', 'p95', 'p99'
);
CREATE INDEX IF NOT EXISTS idx_ms_lookup
    ON metric_snapshots(bucket_time, pack_id, instrument);
```

Retention: pruned to 7 days in `DashboardSummaryBroadcaster.PruneOldDetectionsAsync`, extended to prune `metric_snapshots` with the same cutoff.

---

## Data Flow

### Local mode (steady state)

1. App starts, `MeterListenerService.ExecuteAsync` registers `MeterListener` subscriptions for each `IMonitoringPack`
2. Every `CollectionInterval`: listener fires, snapshot collected
3. `SqliteMetricSnapshotStore.WriteSnapshotAsync` inserts rows with `bucket_time = UtcNow` truncated to 1-minute
4. `DashboardSummaryBroadcaster` emits `BroadcastInvalidation("metrics")` on its normal 30s tick
5. Browser HTMX coordinator receives "metrics" signal, fetches `/api/metrics/timeseries?range=1h`
6. Dashboard renders StyloBot Performance + Host Health panels

### Remote mode

1. Gateway: `GatewayMeterAccumulator` maintains in-memory ring buffer of latest values
2. Dashboard: `RemoteMetricCollector` polls `GET /_sb/metrics/snapshot` on schedule (default 60s)
3. Deserializes `MetricSnapshotDto[]`, writes to local SQLite
4. Same invalidation and rendering path as local mode

The gateway endpoint is on a separate internal port or gated with `RequireHost` -never publicly routed.

---

## Registration

```csharp
// Default: local mode, StyloBot meters only (auto-registered with AddStyloBot)
builder.Services.AddStyloBot(dashboard => {
    dashboard.AllowUnauthenticatedAccess = true;
});

// Opt-in: ASP.NET host meters
builder.Services.AddStyloBot(dashboard => {
    dashboard.MonitoringPack.IncludeAspNetHostMeters = true;
});

// Remote mode (gateway process -serves metrics, does not host dashboard)
builder.Services.AddBotDetection(options => {
    options.MonitoringPack.Mode = MonitoringMode.GatewayServer;
});

// Remote mode (dashboard process -polls gateway)
builder.Services.AddStyloBot(dashboard => {
    dashboard.MonitoringPack.Mode = MonitoringMode.RemoteClient;
    dashboard.MonitoringPack.GatewayMetricsUrl = "http://gateway:8080/_sb/metrics/snapshot";
});
```

`services.TryAddSingleton<IMetricSnapshotStore, SqliteMetricSnapshotStore>()` -commercial
PostgreSQL override uses the same `TryAdd` pattern as `IDashboardEventStore`.

Custom packs register the same way as simulation packs:
```csharp
services.AddSingleton<IMonitoringPack, MyCustomPack>();
```

---

## Dashboard Panel

New **Metrics** tab in `/_stylobot`, between Endpoints and User Agents in tab order.

**StyloBot Performance** (always rendered when pack registered):
- Request rate sparkline -last 1h, 1-min buckets
- Bot ratio donut -bots vs humans, current hour
- Detection latency line chart -P50 + P95 overlaid, last 1h
- Cache hit rate percentage gauge

**Host Health** (rendered only when `IncludeAspNetHostMeters = true`):
- HTTP request duration -P50 + P95, last 1h
- Active requests current gauge
- GC heap allocation rate
- Thread pool depth gauge

All charts use ApexCharts (already loaded in dashboard). Data source:
`GET /api/metrics/timeseries?packId=aspnet-monitoring&range=1h`

SignalR invalidation key: `"metrics"` -same HTMX coordinator that handles all other tabs.

---

## Phase 2: Endpoint Perf Correlation + User Identity Enrichment

Phase 1 collects metrics as an independent time-series. Phase 2 joins that data with
detections and optionally enriches the blackboard with authenticated user identity signals.
Phase 2 is planned but not part of the initial implementation.

### Endpoint Performance Correlation

The Endpoint Detail page (already in backlog) will show a bot/human performance split:
ASP.NET's `http.server.request.duration` is tagged with `http.route` when using minimal
APIs or MVC. `metric_snapshots` rows for those route tags can be joined with `detections`
rows for the same path to produce a side-by-side "bot latency vs human latency" chart.

No new infrastructure required for Phase 2 endpoint correlation -it is a query change in
`SqliteDashboardEventStore.GetEndpointDetailAsync` that left-joins `metric_snapshots` on
`instrument = 'http.server.request.duration'` and `tags LIKE '%route%'`. The Endpoint
Detail Razor partial gains two new chart series.

### User Identity Enrichment

When an endpoint is behind ASP.NET authentication, `HttpContext.User` has claims available
at detection time. StyloBot can use this as a behavioral anchor without storing PII.

**What gets captured (opt-in, off by default):**

- `user.is_authenticated` -boolean signal, no identity stored
- `user.subject_hash` -HMAC-SHA256 of the sub/nameidentifier claim using the same key
  as `PrimarySignature`, making it linkable across sessions without being reversible
- `user.role_flags` -bitmask of role membership for known high-value roles (admin, editor,
  api-user), configured via `BotDetectionOptions.MonitoringPack.TrackedRoles`

**What this enables:**

- Entity resolution: if a user's behavioral vector diverges sharply mid-session, that is a
  credential-stuffing or session-hijack signal. The `subject_hash` links sessions to detect
  when a legitimate account suddenly starts behaving like a bot.
- Rate-limit bypass detection: same `subject_hash` appearing across multiple `PrimarySignature`
  values in a short window indicates credential sharing or token theft.
- Dashboard: Visitors tab gains an "Authenticated" badge and a "user.subject_hash" row in
  the signals panel. No raw username is ever displayed.

**Implementation:**

New `UserIdentityContributor` detector (Priority 8, Wave 0):

```csharp
// Reads HttpContext.User, writes signals only if opt-in enabled
state.WriteSignals([
    new(SignalKeys.UserIsAuthenticated, true),
    new(SignalKeys.UserSubjectHash, ComputeSubjectHash(sub, hmacKey)),
    new(SignalKeys.UserRoleFlags, ComputeRoleFlags(user, options.TrackedRoles))
]);
```

No bot/human contribution -this is a signal-only contributor. Downstream detectors
(`BehavioralContributor`, `SessionVectorContributor`) consume these signals to anchor
session chains and detect cross-session velocity for authenticated identities.

**Zero-PII guarantee maintained:** `user.subject_hash` uses the same HMAC key as
`PrimarySignature`, so it is non-reversible without the key. Raw claims never touch
the blackboard, never persist to SQLite.

**Config:**
```json
{
  "BotDetection": {
    "MonitoringPack": {
      "EnableUserIdentityEnrichment": true,
      "TrackedRoles": ["admin", "editor"]
    }
  }
}
```

---

## Implementation Sequence

1. `IMonitoringPack`, `MeterCollectionGroup`, `InstrumentCollectionSpec`, `CollectedValueType` (interfaces only)
2. `IMetricSnapshotStore` + `MetricSnapshot` model
3. `SqliteMetricSnapshotStore` + schema migration in `SqliteDashboardEventStore`
4. `MeterListenerService` (local mode)
5. `AspNetMonitoringPack` (StyloBot meters only first, host meters second)
6. DI wiring in `ServiceCollectionExtensions`
7. `DashboardSummaryBroadcaster` metrics broadcast + prune
8. API endpoint `GET /api/metrics/timeseries`
9. Dashboard Metrics tab (tag helper + view component + Razor partial)
10. `MonitoringPackOptions` + config binding
11. `GatewayMeterAccumulator` + `MetricsSnapshotController` (remote gateway side)
12. `RemoteMetricCollector` (remote dashboard side)