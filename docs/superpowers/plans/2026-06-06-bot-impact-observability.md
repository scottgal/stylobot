# Bot-Impact Observability: Pivots, Anomalies, Policy Application

> Vision + phased plan for turning the new observability pipeline into a commercial product surface: pivot views by domain / endpoint / fingerprint / bot type, anomaly highlighting driven by bot pressure, one-click policy application.

**Status:** vision-level. Each phase below ships a working slice; the plan is for the whole arc.

---

## The thesis

StyloBot already detects bots. Customers already have observability (HTTP error rates, latency, throughput, DB pressure). The product gap nobody fills: **showing that today's observability anomaly is driven by bot pressure, and offering the policy lever to stop it in one click.**

> "Your 5xx rate spiked at 14:32. 87% of the affected requests came from a single Chrome-XHR fingerprint, currently making 1200 req/s against `/api/search`. Rate-limit it?"

This is anomaly-hunting, the same shape as the rest of the product. Bot detection is the lens; OTel is the substrate. The dashboard is the loop.

## What we already have

- **DetectionEvent log line** per request (FOSS, just shipped). Every verdict lands as a structured log entry with `StyloBot_*` properties.
- **Signal stream** via `BlackboardSignalLogBridge` (FOSS, just shipped). Global blackboard signals on `ILogger<StyloBotSignalCategory>`.
- **Host log enrichment** via `StyloBotLogEnricher` (FOSS, just shipped). Every host log line tagged with `StyloBot_Signature`, `StyloBot_BotType`, etc. So a customer's HTTP-error log already says "this was a curl Scanner bot."
- **Metrics**: two existing meters - `Mostlylucid.BotDetection` (48+ instruments) and `Mostlylucid.BotDetection.Signals`. Already export to OTLP via the new `AddStyloBotObservability` extension.
- **Traces**: `Mostlylucid.BotDetection.Detect` activity per request, with `mostlylucid.botdetection.is_bot`, `bot_type`, `bot_name` tags.

The customer's OTel collector now sees stylobot detection data on every log, metric, and trace. **What it does with that is the customer's call.** The product gap is the dashboard that does the correlation for them.

## What we need to add

### Architectural commitments (binding)

- **Ephemeral, sliding-window**. The dashboard does not become a log warehouse. The bot-impact pivot views operate over a small, time-bounded, in-memory window per pivot dimension. The customer's OTel collector remains the historical record.
- **Existing write-through patterns only**. No new `IMemoryCache`. The pivot caches reuse `DashboardAggregateCache` shape - broadcaster ticks refresh a snapshot, endpoints read from it. ([[feedback_no_unbacked_imemorycache]])
- **Signal-driven where possible**. The orchestrator's `SubscribeToSignals` seam (just added) is the right primitive. The cadence review's drift candidates and the pivot aggregators should both subscribe rather than poll. ([[feedback_no_inmemory_persistence]])
- **FOSS detection sensitivity unchanged**. ([[feedback_foss_never_degraded]])

### FOSS vs commercial split

| Surface | FOSS | Commercial |
|---|---|---|
| `DetectionEvent` log line | yes | yes |
| Signal stream → ILogger | yes | yes |
| Host log enrichment | yes | yes |
| OTLP export (logs/metrics/traces) | yes | yes |
| Prometheus `/metrics` endpoint | yes | yes |
| Pivot aggregator (in-memory window) | no | yes |
| Bot-impact dashboard views | no | yes |
| Anomaly correlation engine | no | yes |
| One-click policy application from anomaly | no | yes |
| Cross-host federation (multiple gateways) | no | yes |

FOSS customers can wire their own dashboards (Grafana over the OTLP export). Commercial buys the curated, bot-detection-correlated pivot UI plus the policy lever.

---

## Phase 1: Pivot aggregator over the live signal + detection stream

**Goal:** an in-memory, sliding-window aggregator that buckets recent detection events by pivot dimensions, so the dashboard can answer "what bots are hitting what endpoint in the last 60 seconds." Single-host. No OTel ingestion yet.

**FOSS reach:** none. Commercial-only.

**Pivot dimensions:**
- Domain (Host header)
- Endpoint (path template, normalised)
- Fingerprint (the existing `fingerprint_id`)
- Bot type (Scanner / Tool / SearchEngine / etc.)
- Signature (the existing primary signature)
- Country code

**Architecture:**

```
src/Mostlylucid.BotDetection.UI.Commercial/   (new project, commercial license)
  PivotAggregator/
    BotImpactAggregator.cs                   # sliding-window state per pivot dimension
    BotImpactSnapshot.cs                     # immutable snapshot record
    BotImpactBroadcaster.cs                  # HostedService; subscribes to IDetectionEventPublisher
    PivotKey.cs                              # struct keying by (dimension, value)
```

**Window:** rolling 5 minutes, 5-second buckets (60 buckets per pivot key). Configurable.

**Capacity:** bounded LFU per dimension (e.g. 1000 top-traffic endpoints, 1000 top-traffic fingerprints). Drop the long tail.

**Data shape per bucket:**
- Request count
- Bot count
- Bot probability sum (for averaging)
- 5xx count (correlated via host's logs through the enricher)
- Action distribution (block / challenge / throttle / allow)
- Bot-type distribution

**Wire-up:** new `IDetectionEventPublisher` implementation chains alongside the Serilog one. Both run via DI composition: detection event fans out to Serilog *and* to the pivot aggregator. The aggregator updates its buckets in-memory.

**Endpoints:**
- `GET /api/v1/bot-impact/by-endpoint?window=5m` - pivot endpoints by recent bot traffic
- `GET /api/v1/bot-impact/by-fingerprint?window=5m`
- `GET /api/v1/bot-impact/by-bot-type?window=5m`
- `GET /api/v1/bot-impact/by-domain?window=5m`

Response: top-N entries per pivot, with current bot-pressure score + recent counts.

**Tests:**
- Aggregator folds N events under a key into one bucket; verify after window drains, bucket retires
- LFU eviction kicks at capacity (top-N preserved, tail dropped)
- Snapshot is consistent across concurrent readers

**Acceptance:** dashboard can query `/api/v1/bot-impact/by-endpoint?window=5m` and get a list of endpoints sorted by recent bot traffic. No UI yet; just the data API.

**Estimated scope:** 6-8 files, ~600 LOC, 8 unit tests, 1 SignalR-broadcast wiring change. ~1 week.

---

## Phase 2: Anomaly correlation engine

**Goal:** highlight pivot rows where bot pressure correlates with an observability anomaly. The signal "your 5xx spike is bot-driven" is the product.

**Method (start simple):**
- For each pivot bucket, compare current minute's `error rate` and `p95 latency` against the trailing 15-minute baseline for the same bucket.
- An anomaly fires when: current minute's error rate is > μ + 3σ AND bot fraction in that bucket is > 0.4. (Both thresholds configurable.)
- Score = bot pressure × deviation. Sort highest first.

We deliberately don't pretend to do Sophisticated Statistical Anomaly Detection in v1. Mean-and-stddev with a guardrail bot-fraction floor is enough to start. Customers will tell us what they want to tune.

**Data inputs:**
- From the pivot aggregator (Phase 1): bot counts, action distribution per bucket
- From the host's OTel HTTP metrics: error rate, latency. **Two paths:**
  - **In-process path** (single-host): subscribe to `Microsoft.AspNetCore.Hosting` Meter via `MeterListener` and bucket by `http.route` + `http.response.status_code`. No collector needed.
  - **Collector path** (later, Phase 5): pull from an OTLP receiver or from Prometheus query.

Phase 2 picks the in-process path. Multi-host is Phase 5.

**Files:**

```
src/Mostlylucid.BotDetection.UI.Commercial/
  AnomalyEngine/
    HttpMetricListener.cs                    # MeterListener for ASP.NET Core HTTP metrics
    HttpMetricBuckets.cs                     # sliding-window per (endpoint, status) pair
    BotImpactCorrelator.cs                   # joins HTTP buckets with bot-impact buckets, fires anomalies
    Anomaly.cs                               # record: pivot, dimension value, score, trigger, recent values
    AnomalyStore.cs                          # in-memory ring buffer of last N anomalies (capacity 100)
```

**Endpoints:**
- `GET /api/v1/bot-impact/anomalies?since=5m` - recent anomalies, sorted by score, with the correlated pivot bucket attached
- `GET /api/v1/bot-impact/anomalies/{id}` - full breakdown of one anomaly (which bot type, which endpoints, when it started)

**Tests:**
- Synthetic burst of bot traffic + 5xx spike fires an anomaly; pure 5xx spike without bot pressure does not
- Pure bot traffic without 5xx spike does not fire (it's an observability anomaly, not a bot anomaly)
- Anomaly de-dupes (same anomaly within 60s doesn't fire twice)

**Acceptance:** the `/api/v1/bot-impact/anomalies` endpoint returns realistic rows during a manual load test where curl hammers `/api/search` and the demo returns 5xx for half the responses.

**Estimated scope:** 5 files, ~400 LOC, 6 tests. ~3-4 days.

---

## Phase 3: Dashboard UI - pivot views + anomaly drill-in

**Goal:** the customer opens `/dashboard/bot-impact` and sees the anomaly story.

**UX structure:**

```
/dashboard/bot-impact
├── Anomaly feed (top)
│   └── "5xx spike on /api/search driven by 87% curl-Scanner traffic" → drill-in
├── Pivot tabs
│   ├── Endpoints       (default)
│   ├── Fingerprints
│   ├── Bot Types
│   ├── Domains
│   └── Countries
└── Each tab:
    ├── Table: rank by recent bot pressure × error rate
    ├── Sparklines: bot traffic 5min, 5xx rate, p95 latency
    └── Row click → drill-in
```

**Drill-in surface (`/dashboard/bot-impact/anomaly/{id}` or `/dashboard/bot-impact/{pivot}/{value}`):**

- Time series: bot traffic + error rate + latency over the last hour
- Top contributors: which fingerprints / bot names made up the bulk of the traffic
- Endpoint heatmap: which paths were affected
- **Policy action panel** (Phase 4): rate-limit / 429 / block / redirect, scoped to the displayed pivot

**Implementation:**

- Razor pages in `Mostlylucid.BotDetection.UI.Commercial` (assuming commercial-only UI lives in a separate package; if the boundary is enforced by feature flag in the existing UI, file location adjusts but the UX is the same)
- HTMX-driven refresh, polling `/api/v1/bot-impact/*` every 5s while the tab is visible (gated by the existing dashboard idle-skip pattern)
- SignalR push for new anomalies - extend the existing dashboard hub

**Tests:**
- Razor page renders with anomaly feed and pivot tabs
- HTMX polling endpoint serves cached snapshot in < 50ms

**Acceptance:** on a local Demo with manual load, the anomaly feed shows real rows, drill-in renders the contributors, sparklines update live.

**Estimated scope:** 4-6 Razor pages, 2-3 view components, ~800 LOC, browser smoke test.  ~1 week.

---

## Phase 4: One-click policy application

**Goal:** the customer reads an anomaly and clicks "rate-limit this fingerprint at 10 req/s" or "429 curl-Scanner on /api/search" or "block this domain". The policy goes live without a redeploy.

**Why this is the commercial lock:** the FOSS publisher gives them visibility. The commercial dashboard gives them anomaly correlation. **The action lever closes the loop.** Customers stop bouncing between Datadog and stylobot - they act inside stylobot.

**Existing policy primitives to leverage:**
- `BotPolicyAttribute` (per endpoint, code-side)
- Endpoint policies registry (per `feat/endpoint-policies`)
- `DefaultActionPolicyName` (per host)
- Signature labels (manual override per signature)
- Fingerprint approval (manual override per fingerprint, planned)

**Policy targets the dashboard exposes:**

| Pivot | Action knobs |
|---|---|
| Endpoint | rate-limit (X req/s per fingerprint), 429 with `Retry-After`, challenge, block |
| Fingerprint | rate-limit, block, challenge, mark as verified-bot |
| Bot type | rate-limit on a path glob, block on a path glob, force-throttle |
| Domain | global rate-limit, default-deny posture, observation-only |
| Country | rate-limit, block, challenge |

**Architecture:**

```
src/Mostlylucid.BotDetection.UI.Commercial/
  PolicyActions/
    PolicyActionRequest.cs                   # record: target, action, parameters, ttl, reason
    PolicyActionDispatcher.cs                # routes the request to the right policy store
    PolicyActionAuditLog.cs                  # who applied what, when
```

The dispatcher routes to existing policy stores:
- Endpoint → `IEndpointPolicyStore.AppendOverrideAsync(...)`
- Fingerprint → `IFingerprintApprovalStore.Add(...)` (when that lands) or a new `IFingerprintOverrideStore`
- Bot type / country / domain → `DefaultActionPolicyName` overrides via a new `IDynamicPolicyOverrideStore`

All overrides have a configurable TTL (default 1 hour). They expire automatically; the customer sees a banner reminding them which overrides are active.

**Audit log:** every action persists to SQLite (commercial: PostgreSQL). Includes the anomaly that motivated it. Surfaces under `/dashboard/audit`.

**Authorization:** uses the existing dashboard auth ([[project_dashboard_auth]]). Policy mutations require admin tier; read-only operators see the anomaly feed but the action buttons are disabled.

**Endpoints:**
- `POST /api/v1/bot-impact/policy` - apply a policy override; body is `PolicyActionRequest`
- `GET /api/v1/bot-impact/policy/active` - list of currently-active overrides
- `DELETE /api/v1/bot-impact/policy/{id}` - revoke

**Tests:**
- Applying an endpoint rate-limit causes subsequent requests from the same fingerprint to be throttled (integration test against Demo)
- TTL expiry restores prior behaviour
- Audit log records the action with the operator identity and the source anomaly

**Acceptance:** during a manual bot-load test, click "rate-limit fingerprint X at 10 req/s for 30 minutes", watch the next requests get 429s, watch the anomaly feed clear, watch the audit log row appear.

**Estimated scope:** 6 files, ~600 LOC, 6 tests + 1 integration test. ~1-2 weeks.

---

## Phase 5: Multi-host federation

**Goal:** the customer runs stylobot on 5 gateway hosts. The bot-impact dashboard aggregates across all of them.

**Out of scope for the first commercial release.** The Phase 1-4 dashboard is single-host. Multi-host is a v2 problem and the architecture should be sketched now to avoid painting into a corner:

- Each gateway exports its bot-impact snapshots to a shared store (Redis, Postgres, or an OTLP receiver). The dashboard reads from the shared store.
- The pivot aggregators stay in-process per gateway; only the snapshots travel.
- The orchestrator's per-host `SignalSink` doesn't synchronize across hosts (and shouldn't - too chatty); the federation operates at the bucket-snapshot level.

This is consistent with [[project_multi_yarp_design]] (header-driven detection-once, policy-at-every-hop).

---

## Cross-cutting work

### Connection to the cadence review

The cadence review (`docs/superpowers/reviews/2026-06-06-async-cadence-review.md`) names 5 drift candidates and identifies `IDetectionSignalBus.Subscribe(...)` (the seam we built in Task 2 of the observability plan) as the under-used coordination primitive. The pivot aggregator (Phase 1) will be the second consumer of that seam. **Building Phase 1 well - subscribing rather than polling - validates the architecture path for the drift fixes too.**

### Connection to the verdict-cache un-drift

The un-drift gave us EWMA-blended verdict probabilities on the fingerprint row. The pivot aggregator should consume these as the per-fingerprint "current verdict", not re-derive bot probability from per-request events. Less noisy, more correlated.

### What goes on the wire to OTel

The customer's OTel collector still sees:
- Per-detection log lines (the publisher) - raw events
- StyloBot meters - `botdetection.bots.detected`, etc. - already exported
- Trace activities

The dashboard doesn't replace these. It adds the pivot view on top, **using the same data the OTel pipeline emits**. A customer who runs their own Grafana over our Prometheus endpoint sees the same instrument values. The commercial value is the correlation + action layer.

---

## Phasing summary

| Phase | Goal | Scope | FOSS impact | Ships standalone? |
|---|---|---|---|---|
| 1 | Pivot aggregator API | ~1 week | none (commercial-only) | yes - usable as raw data |
| 2 | Anomaly correlation engine | ~3-4 days | none | yes - anomaly API queryable |
| 3 | Dashboard UI | ~1 week | none | yes - Phase 2 + UI |
| 4 | Policy application | ~1-2 weeks | none | yes - closes the loop |
| 5 | Multi-host federation | future | none | v2 |

Each phase ships independently. Phase 1 is the foundation; subsequent phases compose. Phase 4 is the commercial lock-in.

---

## Open questions to validate before kicking off Phase 1

1. **Where does the commercial code live?** Today there's `stylobot-commercial` (sibling repo per CLAUDE.md). Phase 1's `Mostlylucid.BotDetection.UI.Commercial` project lives in that repo, not the FOSS one. Confirm the build-and-link story (project reference vs NuGet package).
2. **Aggregator scope of "bot pressure" measurement.** Per-pivot bot fraction (bots / total in that bucket) vs absolute bot count vs both? Phase 1 ships both; the anomaly engine in Phase 2 picks which to use.
3. **HTTP metric source.** ASP.NET Core's built-in `Microsoft.AspNetCore.Hosting` Meter covers route + status. If the customer's app uses custom names (e.g., Mediator endpoints), do we expose a hook for them to register additional Meters?
4. **Policy override storage in FOSS.** The endpoint policy registry is already FOSS. The `IDynamicPolicyOverrideStore` for bot-type / country / domain may need a FOSS view-only surface so the FOSS dashboard can show what's active even if it can't mutate. Decide before Phase 4.

---

## Self-review

**Spec coverage of the user's brief:**
- "Small, ephemeral sliding window" - covered by the bucket model in Phase 1 (5-minute window, 5-second buckets, LFU-capped).
- "Pivot on views by domain, endpoint, fingerprint, bot type" - covered by Phase 1 endpoints and Phase 3 UI.
- "Use OTel data to show issues" - Phase 2 correlator joins host HTTP metrics with bot-impact buckets.
- "Apply policies to domains, endpoints, fingerprints, bot types" - Phase 4 action panel.
- "Bot traffic as the driving factor" - anomaly threshold requires bot fraction > 0.4 (Phase 2). Excludes pure observability anomalies (correctly).
- "Commercial only, FOSS gets logs" - explicit table at top.

**No drift on existing patterns:**
- Pivot aggregator is signal-driven (subscribes to `IDetectionEventPublisher`), not polling.
- Sliding window is bounded by time + size, no log warehouse.
- No new `IMemoryCache`. Uses the snapshot-broadcaster pattern.
- Policy application routes through existing policy stores; no new authoritative source of truth.

**Honesty:**
- "Anomaly detection" in Phase 2 is mean+stddev with a bot-fraction floor. Not ML. The plan says so; we calibrate from data.
- Multi-host is deferred. Single-host first; the architecture won't paint into a corner.
- The commercial / FOSS boundary is enforced by project location, not feature flag. Cleaner long-term.
