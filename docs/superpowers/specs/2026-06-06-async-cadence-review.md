# Async Cadence Review

> Self-contained spec for a fresh investigation session. No prior conversation context assumed.

## Background

StyloBot's request path uses the StyloFlow blackboard pattern: detectors and contributors react to signals on an in-memory `SignalSink`, ephemeral per-request. The matcher (`FingerprintMatchContributor`) is correctly signal-driven — it short-circuits on L1 hit and emits identity signals that downstream contributors react to.

**The async / background side is not.** Background services (`IHostedService` implementations, batched drainers, calibration ticks, naming pipelines) appear to operate on independent timers rather than coordinating via the blackboard signal stream. Recent example: `FingerprintDriftService` ticked every 5 seconds to bump `cached_score_updated_at`, but the verdict-cache lookup gated on that timestamp — so same-visitor bursts within the tick window missed the cache and re-ran the full pipeline. Fixed in `2026-06-06-verdict-cache-undrift.md` by moving the write into the request path; the drift service still ticks but is no longer load-bearing.

The hypothesis to test: **other async services have similar drift** — they should be reacting to signals (`signature.observation_count.crossed_N`, `ambiguity.persistence.rising`, `centroid.maturity.threshold_reached`, `signature.first_seen`) rather than ticking on their own clocks. Where they don't, latency is higher than needed and the system is doing redundant work.

Architecture reference: `docs/architecture/fingerprint-match.md`, `docs/architecture/signal-contracts.md`, the StyloFlow blackboard summary in `CLAUDE.md`.

## What this spec asks for

A **review and inventory**, not implementation. Surface the problem clearly enough that a follow-up plan can address it.

### 1. Inventory every async service

Find every `IHostedService` / `BackgroundService` / `IAsyncDisposable`-with-internal-loop in the `src/` tree. Search heuristics:

```bash
grep -rn "class.*BackgroundService\|: IHostedService\|ExecuteAsync\|StartAsync.*stoppingToken" src/ --include="*.cs" | grep -v Test
```

For each service found, capture:
- **File path** (absolute) and class name
- **Purpose** (one sentence)
- **Trigger model** — pick one:
  - **Time-driven**: fires on a `Timer` / `PeriodicTimer` / `await Task.Delay(interval)` loop. Note the interval (default + config key).
  - **Queue-driven**: drains a `Channel<T>` / bus / queue. Note the producer.
  - **Signal-driven**: subscribes to a `SignalSink` or reacts to `IDetectionEventPublisher` / `ILearningEventBus` / similar. Note the signal name or event type.
  - **Hybrid**: combination, describe.
- **What durable state it produces** (writes to which SQLite tables / dashboard cache / fingerprint state, etc.)
- **What request-path features gate on its output** — e.g., "L1 verdict lookup gates on `cached_score_updated_at`", "Dashboard `/api/v1/summary` reads `DashboardAggregateCache.Current.Summary`"

### 2. Known candidates to look for explicitly

These are services the architecture references; confirm each one exists, where it lives, and its trigger model:

- `FingerprintDriftService` (5s tick today, drift verification)
- LLM naming pipeline driven by `ILearningEventBus` (whatever subscribes to it for signature description)
- `BackgroundEnrichmentService`
- `RequestPersistenceService` (batched dashboard rows)
- `IdentityWeightCalibrationService` (centroid refinement / archetype recalibration)
- `SignatureCoordinator` (per-signature sliding window — note: this may not be a hosted service, may be request-path)
- Centroid compression / session snapshot compaction (CLAUDE.md mentions "Snapshot compaction" under Session Vector Architecture — find the implementation)
- `DashboardSummaryBroadcaster` (refreshes `DashboardAggregateCache`)
- `BotListUpdateService` (daily 2 AM)
- `LicenseStateRefreshService`
- `HoneypotReporter`
- `AuditProcessor`
- `ConfigurationWatcher`
- `SqliteSessionPersistenceService` (or whatever bridges the in-memory `SessionStore` events to SQLite)
- Browser-mode absorption drainer (referenced in `feat(identity): append-only mode observations + batched drainer (race fix)` — commit `6adda676`)
- Anything that drains write-behind LFU stores (`WriteBehindLfuStore` subclasses each have a drainer)

### 3. The classification table

Produce a markdown table with columns:

| Service | File | Trigger | Interval / signal | Produces | Request-path consumers gate on |
|---|---|---|---|---|---|

One row per service. Keep it scannable.

### 4. The drift analysis

For each **time-driven** service, answer:
- Could it be signal-driven instead? What signal would it wait for?
- If it stays time-driven, is the cadence chosen for a real reason (e.g., minimum-batch-size economics, throttle to an external API) or is it arbitrary?
- Does its output gate any request-path feature? If yes, that's the drift candidate — the request path is paying for slow async cadence.

### 5. Coordination opportunities

Look for services that are doing related work on independent clocks:
- e.g., calibration service refines centroids on one schedule; naming service queries the same centroids on another schedule. Could one trigger the other?
- e.g., two services each scan the full fingerprints table on their own tick. Could a shared scan publish to both?

### 6. The "blackboard already knows" gap

The blackboard has signals like:
- `signature.observation_count.crossed_*` (X observations crossed for a signature)
- `ambiguity.persistence.rising` (drift signal)
- `centroid.maturity.threshold_reached`
- `signature.first_seen`
- Any others that exist today

Confirm which of these exist in `SignalKeys` (or wherever signal names are defined). For each existing signal, check: is any background service subscribed to it? If not, is there a service whose timer-driven work would be naturally triggered by that signal?

### 7. Recommendations

For each drift candidate identified in (4) and (5), one paragraph proposing the change. Keep it specific:
- Move from Timer to subscription on signal X
- Add a new signal Y so the existing service can wake on it
- Consolidate two timers into one shared scan

Don't write implementation code. The goal is a punch list a follow-up planning session can turn into focused tasks.

## Output format

A markdown document under ~2000 words. Save to `docs/superpowers/reviews/2026-06-06-async-cadence-review.md`.

Sections:
1. **Inventory** (the table from §3)
2. **Drift candidates** (the time-driven services with request-path consumers)
3. **Coordination opportunities** (services doing related work on independent clocks)
4. **Signals already on the blackboard but no async subscriber** (the §6 finding)
5. **Recommendations** (the §7 punch list)
6. **Out of scope** (e.g., daily-cadence services like `BotListUpdateService` that legitimately need to be timer-driven)

## Out of scope

- Implementation. This is a review.
- Per-detector tuning. Focus on background / async services.
- Distributed-deploy coordination (multi-host gateway sync). Stay on single-host architecture.

## Constraints

- Read-only. No code edits.
- Don't propose adding `IMemoryCache` or any new unbacked cache layer. The architecture already has `WriteBehindLfuStore`, `DashboardAggregateCache`, `SignatureAggregateCache` — reuse those if a recommendation needs a cache.
- Don't propose new threading primitives. The existing patterns (channels, signal subscriptions, write-behind drainers) cover the design space.

## Helpful starting reads

- `CLAUDE.md` (architecture summary)
- `docs/architecture/signal-contracts.md`
- `docs/architecture/fingerprint-match.md`
- `src/Mostlylucid.BotDetection/Identity/FingerprintDriftService.cs` (a canonical time-driven service to use as a template for the analysis)
- `src/Mostlylucid.BotDetection.UI/Services/DashboardSummaryBroadcaster.cs` (a canonical broadcaster that refreshes a cache snapshot)
- `src/Mostlylucid.BotDetection/Storage/WriteBehindLfuStore.cs` (the reusable write-behind façade and its drainer loop)
- `docs/superpowers/plans/2026-06-06-verdict-cache-undrift.md` (the un-drift work this review extends)

## Definition of done

The review document exists, the inventory table is populated for every async service in `src/`, the drift candidates are named, and the recommendations are specific enough to feed a follow-up planning session.