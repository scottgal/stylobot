# Stylobot Stabilisation Campaign

The feature-frenzy phase is done. This is the distillation: bring all the drift back to the `Mostlylucid.Atoms` / `Mostlylucid.Ephemeral` signals-and-atoms ideal that the codebase is actually built on.

Three sweeps, sequenced. Each is a coherent set of commits with a single architectural goal. No interleaving.

## The architectural truth (compressed)

- **Atoms hold state.** They have query accessors. They're the source of truth.
- **Signals announce that the atom changed.** Listeners receive a signal, then query the atom for truth. The Blackboard (`BlackboardState.Signals`) is a SignalSink specialization for the orchestrator's wave/quorum semantics. `HttpContext.Items` is the post-orchestrator request-scoped atom-equivalent.
- **LFU sliding cache** is the lookback infrastructure. Useful signals stay warm because consumers query them; unused signals evict. Ephemeral by default.
- **StyloFlow adapts on signals.** Without signals being emitted, the adaptive layer (eventually the workflow view) has nothing to read.
- **Logs are NOT inspection.** `ILogger` is for genuine error/exception emission and lifecycle banners — not per-request observability.
- **The Ephemeral atom catalogue is the building set.** `SlidingCacheAtom<TKey, TResult>` (LFU read-through with dedup + signal emission), `KeyedSequentialAtom<TReq, TKey>` (per-key ordered writes + fair global scheduling), `EphemeralLruCache` (hot-key bias for `SqliteSingleWriter`), and the rest. Always check first; don't reimplement.

Read `mostlylucid.atoms/mostlylucid.ephemeral/SIGNALS_PATTERN.md` if uncertain. Read `mostlylucid.atoms/mostlylucid.ephemeral/src/mostlylucid.ephemeral.atoms.slidingcache/README.md` for the atom shape.

## The drift catalogue (smear alarms)

What the LLM-built code does when reaching for asp.net reflexes instead of the atom pattern:

| Smear | Right answer |
|---|---|
| `_logger.LogInformation("middleware saw X")` | Signal: `_sink.Raise("middleware.X")` |
| `new SqliteConnection(...)` in a middleware | Atom: cached read/write-through wrapper, only the wrapper is DI'd |
| `await store.X(...)` from a hot-path consumer | Atom-backed accessor (`SlidingCacheAtom.GetOrComputeAsync`) |
| Hand-rolled `ConcurrentDictionary` + `LinkedList` LRU + `Task.Run` TTL loop | `SlidingCacheAtom<TKey, TResult>` from Ephemeral |
| Hand-rolled `PriorityQueue` + workers + circuit breaker | `KeyedSequentialAtom` + Ephemeral admission/shedding |
| Signal-as-state: writing a value via `WriteSignal` then reading it as truth elsewhere | One owner writes the atom; others raise notifications and query the atom |
| Multiple contributors writing the same `SignalKeys.X` in the same wave | Race. One owner writes the atom; others read it |
| Per-request `try/catch` around DB calls because the connection might fail | The atom handles transient errors; surface failures as signals if useful |

When a section of this plan is about to add any of those left-column patterns, that section is wrong and needs to be re-shaped to the right-column answer.

## Campaign 1 — Edge-headers cleanup + visitor-detail radar (in flight)

See `2026-05-28-edge-headers-cleanup.md`. Ten sections. Already drafted. The immediate fires from this session:

1. **A** — `CachedFingerprintReader` (SlidingCacheAtom-backed, registered as `IFingerprintReader`, replaces every raw `store.X` call).
2. **B+C+D** — kill matcher's synthetic-signal-broadcast; populate `cachedEvidence.Signals` on the skip path; Signals↔Items mirror as the orchestrator-boundary atom hand-off.
3. **E** — hydrator hydrates all 10 emitted headers into Items, builds a stub `AggregatedEvidence`. **Delete `LogInformation` + `ILogger` field entirely.**
4. **F** — `SignatureDetailModel.FingerprintShape` populated server-side; `_FingerprintShape.cshtml` partial replaces `_BehavioralEvolution.cshtml`; same projection as the home card.
5. **G–J** — header-name consistency (`X-Bot-Detection-*` everywhere), `EmitOnResponseToClient` toggle + `HeaderPrefix` option, ctor-inject the cached reader, env var the staging API key, drop the `ContainsKey` guard, extract `TryGetEvidence` helper.

Exit criteria: home card AND `/dashboard/signature/{id}` render the fingerprint radar in Playwright. Zero raw `SqliteConnection` in any middleware or view component. Zero per-request `LogInformation`. `FingerprintMatcherConvergenceTests` 5/5 green.

## Campaign 2 — Identity subsystem atomification (next)

Once Campaign 1 lands. Goal: every identity read/write goes through the atom pattern.

### Architectural correction discovered during exploration

The original plan was to build `CachedFingerprintReader` as a wrapper around `SqliteFingerprintStore`. That's the wrong shape. The right primitive already exists in the Ephemeral catalogue: `Mostlylucid.Ephemeral.Sqlite.SingleWriter` (`SqliteSingleWriter`).

`SqliteSingleWriter` is the canonical SQLite-backing atom. It provides:
- Serialized writes via `EphemeralWorkCoordinator(MaxConcurrency=1)` (correct for SQLite).
- Cached reads via `EphemeralLruCache` with hot-key extension (`HotAccessThreshold` configurable, default 3 hits = hot, `HotKeyExtension` default 30 min).
- `WriteAndInvalidateAsync(sql, cacheKeys)` — atomic write + key invalidation in one call.
- Cross-process invalidation via a shared SignalSink — other components can raise `cache.invalidate:*` and the writer clears matching keys.
- Per-operation signal emission for free (`cache.hit/miss/hot/evict`, `write.start/done/error`, batch begin/commit/rollback).

So Campaign 2 replaces "build a wrapper" with "refactor `SqliteFingerprintStore` to use `SqliteSingleWriter` internally." Cleaner, drops 100+ lines of hand-rolled `new SqliteConnection`/`OpenAsync` boilerplate, every existing call site of `IFingerprintReader` automatically benefits.

### 2.1 Refactor `SqliteFingerprintStore` to use `SqliteSingleWriter`

Every `await using var conn = new SqliteConnection(_connectionString)` (15+ sites in the file) goes away. Each becomes:
- Reads: `await _writer.ReadAsync("fingerprint:{id}", async (conn, ct) => { /* existing SQL */ }, ct);`
- Writes: `await _writer.WriteAndInvalidateAsync(sql, parameters, cacheKeys: ["fingerprint:{id}", "key:{primarySig}"]);`

Cache-key convention:
- `fingerprint:{fingerprintId}` — single fingerprint row.
- `fingerprint_key:{primarySignature}` — primarySig → fpId map.
- `obs_count:{fingerprintId}` — unabsorbed observation count for one fp.
- `obs_counts:all` — full unabsorbed map (used by dashboard's Identities tab).
- `fingerprints:all` — full list (used by calibration + brute-force index).

Write invalidation matrix:
- `InsertFingerprintAsync(fp, primarySig)` → invalidate `fingerprint:{fp.Id}`, `fingerprint_key:{primarySig}`, `fingerprints:all`, `obs_count:{fp.Id}`, `obs_counts:all`.
- `UpsertKeyAsync(primarySig, fpId)` → invalidate `fingerprint_key:{primarySig}`.
- `RecordObservationAsync(fpId, vector)` → invalidate `obs_count:{fpId}`, `obs_counts:all`, `fingerprint:{fpId}`.
- `RecordCorrectionAsync(...)` → invalidate the relevant fingerprint rows.
- `UpdateDisplayNameAsync(fpId, ...)` → invalidate `fingerprint:{fpId}`.

### 2.1.1 Connection prep is its own atom — capability composition, not a hook

`SqliteFingerprintStore.OpenConnectionWithVecAsync()` exists because **sqlite-vec must be loaded per-connection** (loading on one connection doesn't propagate to others). vec0 KNN queries depend on this.

The wrong shape is bolting an `OnConnectionOpened` callback onto `SqliteSingleWriter` — a hook is the asp.net reflex of "let me bolt this onto your API without thinking about what the API actually wants." The right shape is composition: connection prep is its own atom, and the writer composes it.

**Canonical design (we own the stack, ship it properly in `mostlylucid.atoms`):**

```csharp
// New package: Mostlylucid.Ephemeral.Sqlite (or sibling to .Atoms.SlidingCache)
public interface ISqliteConnectionCapability
{
    Task ApplyAsync(SqliteConnection connection, CancellationToken ct);
}

public sealed class SqliteConnectionFactory : IAsyncDisposable
{
    public SqliteConnectionFactory(
        string connectionString,
        IReadOnlyList<ISqliteConnectionCapability>? capabilities = null,
        SignalSink? signals = null);

    public ValueTask<SqliteConnection> AcquireAsync(CancellationToken ct);
    // Emits: connection.acquired / capability.applied:{name} / capability.failed:{name}:{reason} / connection.released
}

public sealed class SqliteSingleWriter
{
    public SqliteSingleWriter(SqliteConnectionFactory connections, SqliteSingleWriterOptions options);
    // Knows about: serialized writes + cached reads + invalidation.
    // Does NOT know about: extensions, pragmas, FTS tokenizers, or any per-connection setup.
}
```

**stylobot's vec0 capability ships in `Mostlylucid.BotDetection`:**

```csharp
public sealed class SqliteVecCapability : ISqliteConnectionCapability
{
    public Task ApplyAsync(SqliteConnection conn, CancellationToken ct)
    {
        conn.EnableExtensions(true);
        conn.LoadExtension("vec0");
        return Task.CompletedTask;
    }
}
```

**Wiring (one line in DI):**

```csharp
var connections = new SqliteConnectionFactory(connStr, [new SqliteVecCapability()]);
var writer = new SqliteSingleWriter(connections, options);
services.AddSingleton(writer);
```

**Why this is the right shape:**
- Capabilities compose. Need vec0 + WAL pragma + FTS5 tokenizer? Pass three capabilities. No combinatorial explosion of hook signatures.
- Each capability is a tiny atom that itself raises signals (`capability.applied:vec`, `capability.failed:wal:permission`) — observability falls out for free, no logging needed.
- The factory is independently useful — a future read-only consumer (e.g. a pure `SqliteReader` for the dashboard's analytics path) takes the same factory.
- Stylobot's vec0 setup ships as a stylobot-side capability class. The framework never has to know SQL-vector exists.
- Splits the "what's a ready connection?" concern from the "how do I coordinate writes?" concern — two atoms instead of one bloated class with a hook surface.

Of the 12 stylobot SQLite stores, only `SqliteFingerprintStore` needs the vec0 capability. The other 11 stores instantiate `SqliteConnectionFactory(connStr, capabilities: null)` and inherit all the goodness with zero per-store custom prep.

### 2.2 Write invalidation via signals (FREE with SqliteSingleWriter)

`SqliteSingleWriter` emits write/invalidation signals automatically when `WriteAndInvalidateAsync` is used. Subscribers (the in-process cache, future cross-process consumers) listen on the shared SignalSink. No manual signal wiring needed in `SqliteFingerprintStore`.

### 2.3 `IdentityProcessingCoordinator` → `EphemeralKeyedWorkCoordinator`

The 429-line `IdentityProcessingCoordinator` is the textbook smear: `: BackgroundService` + `SemaphoreSlim _newItemSignal = new(0)` + `Task.Run(() => WorkerLoopAsync(stoppingToken))` per worker, plus hand-rolled PriorityQueue + per-fp coalesce + circuit breaker on top. Ephemeral's `EphemeralKeyedWorkCoordinator<IdentitySlowPathRequest, string>` does the queue+coalesce+per-key-sequential parts with ~30 lines + automatic signal emission for queue depth, shed events, throughput.

The canonical template is `SignatureCoordinator` (Orchestration/SignatureCoordinator.cs):
- `public class SignatureCoordinator : IAsyncDisposable` — NOT `BackgroundService`.
- Composes `SlidingCacheAtom<string, SignatureTrackingAtom>` + `KeyedSequentialAtom<SignatureUpdateRequest, string>` internally.
- Internal `SignalSink _signals` for cross-atom coordination.
- DI: `services.TryAddSingleton<SignatureCoordinator>()` (pure singleton, lifecycle via `IAsyncDisposable`).
- Separately: `services.AddHostedService<SignatureCoordinatorWarmupService>()` — tiny warmup that pulls the singleton.

`IdentityProcessingCoordinator` follows this template after refactor.

Breaker/shedding semantics — `EphemeralOptions.CancelOnSignals` / `DeferOnSignals` cover circuit-breaker and backpressure declaratively (per ephemeral README §"Signal-based control flow"). `FairSchedulingThreshold` covers per-fp coalesce. The remaining "operator-triggered must run" carve-out is a thin wrapper on `EnqueueAsync` that bypasses the cancel-on-signals check for the privileged path.

Tests: `IdentityProcessingCoordinatorTests` covers all six layered defences today (queue cap, per-fp cap, coalesce, breaker, drop-oldest priority, fair scheduling). All six must remain green after the refactor.

### 2.4 The four other identity `BackgroundService` classes

The identity-subsystem smear is broader than just the coordinator. Five files in `src/Mostlylucid.BotDetection/Identity/` inherit `BackgroundService`:

| File | Lines | Role | Atom shape |
|---|---|---|---|
| `IdentityProcessingCoordinator.cs` | 429 | Per-fp request queue with worker pool, coalesce, breaker | `EphemeralKeyedWorkCoordinator` (Section 2.3) |
| `FingerprintAbsorptionService.cs` | 154 | Periodic absorption of pending observations into fingerprint centroids | `ScheduledTasksAtom` cron entry → `DurableTaskAtom` |
| `FingerprintDriftService.cs` | 175 | Periodic drift recompute / decay | `ScheduledTasksAtom` cron entry → `DurableTaskAtom` |
| `IdentityGlobalWeightsCache.cs` | 104 | Cached global weight matrix with periodic reload | `SlidingCacheAtom<>` (single key, long sliding TTL) + `ScheduledTasksAtom` refresh entry |
| `IdentityWeightCalibrationService.cs` | 163 | Periodic weight recalibration from observation corpus | `ScheduledTasksAtom` cron entry → `DurableTaskAtom` |

The four periodic-timer services (Absorption / Drift / WeightsCache / Calibration) are all the same shape underneath: "every N seconds, do work against the DB." `ScheduledTasksAtom` + `DurableTaskAtom` (Ephemeral core, see core README §"Scheduled tasks") is the canonical replacement — cron schedule, emits its own signals, lives in the coordinator window with the same pinning/signal-logging semantics.

`IdentityGlobalWeightsCache` is special: it also holds state (the global weight matrix). The pure cron shape doesn't hold state. Two atoms compose here — `SlidingCacheAtom<string, GlobalWeights>` for the state (single key `"global"`, very long sliding TTL so it never evicts) + `ScheduledTasksAtom` cron that calls `cache.Invalidate("global")` on schedule, forcing the next read to refetch. The signal `cache.miss:global` then fires automatically when consumers read the cache.

Together with 2.3, this is five files removed-and-rebuilt to the atom template. Net: ~1000 lines of hand-rolled hosted-service + timer + lock + worker-loop boilerplate become ~250 lines of atom composition. Plus, every one of these refactors gains free signal observability for the future workflow view.

### 2.5 `SqliteFingerprintStore._initLock` (small but illustrative)

`SqliteFingerprintStore.cs:23` has `private readonly SemaphoreSlim _initLock = new(1, 1);` for schema initialization. Once Section 2.1 lands (store composes `SqliteSingleWriter`), this goes away — the writer's `EphemeralWorkCoordinator(MaxConcurrency=1)` serializes the schema init alongside every other write. One fewer hand-rolled lock.

### 2.6 Audit raw-TPL fingerprint-store call sites

Eight production sites today call `store.GetFingerprintAsync` / `LookupFingerprintIdAsync` directly:

| Site | After Campaign 1 |
|---|---|
| `FingerprintMatchContributor.cs:155, 157, 242, 312` | Switch to `IFingerprintReader` (cached) — already injected. |
| `FingerprintDriftService.cs:98` | Switch to `IFingerprintReader`. |
| `IdentityAiOpinionService.cs:96` | Switch to `IFingerprintReader`. |
| `IdentityWeightCalibrationService.cs:77` | `ListFingerprintsAsync` — cache likely wrong here (whole-table read). Decide per-case: either no-cache (direct store) or a list-cache atom with short TTL. |
| `BruteForceIdentityAnchorIndex.cs:23` | Same as above — `ListFingerprintsAsync` for vec0 fallback. No-cache or short-TTL list atom. |
| `IdentityEndpoints.cs` (4 sites in `Mostlylucid.BotDetection.Api`) | `IFingerprintReader` injection. |
| `SbIdentitiesListViewComponent.cs:39-40` | Already uses `IFingerprintReader` interface — auto-benefits from cache. |
| `StyloBotDashboardMiddleware.cs:3238, 3239, 3371, 3377, 4528` | Already uses `IFingerprintReader` — auto-benefits. |

The matcher (`FingerprintMatchContributor`) is the only contributor doing per-request `store.X` calls. Change its constructor to inject `IFingerprintReader` instead of `SqliteFingerprintStore`. Writes (which the matcher also does) still hit the concrete store; reads go via cache.

## Campaign 3 — Signal vs log audit + bare-service-to-atom sweep (broader)

Once Campaigns 1 and 2 land. Goal: align the rest of the codebase to the same principles.

### 3.0 The `ScheduleCoordinator` — one singleton owns all background work

**The bigger architectural correction discovered this round.** ~39 classes in FOSS inherit `BackgroundService` / `IHostedService`. Each is the asp.net reflex: own `ExecuteAsync` loop, own `ILogger`, own state, own timer. The user has flagged this as drift. The right shape is ONE singleton coordinator that owns all background work.

**`ScheduleCoordinator` design:**

```csharp
// One singleton, IAsyncDisposable, atom-shaped, composes Ephemeral primitives,
// internal SignalSink. Lives next to SignatureCoordinator.
public sealed class ScheduleCoordinator : IAsyncDisposable
{
    // Units register their cadence + dependencies + body. The coordinator
    // owns timer cadence, signal emission, observable state, shutdown.
    void Register(
        string unitName,
        TickCadence on,                      // fixed tick: 10s / 30s / 1m / 5m / 15m / 1h / 24h
        IReadOnlyList<string>? after,        // chain off other units' completion signals
        Func<CancellationToken, Task> body,
        ScheduleUnitOptions? options = null);

    // Custom interval escape hatch — for cadences that don't fit the fixed enum
    // (audited: 9 of 26 Group A units use configurable seconds-based cadence).
    void Register(
        string unitName,
        TimeSpan interval,
        IReadOnlyList<string>? after,
        Func<CancellationToken, Task> body,
        ScheduleUnitOptions? options = null);

    // Observable state for the future StyloFlow workflow view.
    IReadOnlyList<ScheduledUnitState> GetUnits();
}

public sealed record ScheduleUnitOptions
{
    // Random offset on first run, prevents thundering-herd on startup.
    // Required by ThreatIntelRefresh (line 128) and similar.
    public TimeSpan? StartupJitter { get; init; }

    // Exponential backoff on failure, with cap. Required by BotListUpdate
    // (line 205, 2^attempt * 5s) and ThreatIntelRefresh (line 162-179, 30s
    // → 1h cap, reset on success). Composable from
    // mostlylucid.ephemeral.atoms.retry.
    public BackoffPolicy? BackoffOnError { get; init; }
}

public enum TickCadence
{
    TenSeconds, ThirtySeconds, OneMinute, FiveMinutes, FifteenMinutes, OneHour, TwentyFourHours
}
```

**Cadence-precision audit findings (this round):**

- **No sub-second cadences in Group A.** Smallest is `EntityResolutionService` at 60s. The fixed-tick enum + custom-interval escape hatch covers everything.
- **`StartupJitter` is required.** `ThreatIntelRefreshService:128` already implements it manually (`TimeSpan.FromSeconds(random.Next(0, window))` offset on startup) to prevent thundering-herd across N providers. Other startup-delay patterns (10s in BotCluster, 10s in CommonUserAgent, 20s in SignatureConvergence) are different concern: warmup-after-app-start, not jitter. Both can be expressed: `StartupJitter` + a global `AppStartedDelay` config.
- **`BackoffOnError` is required.** `BotListUpdateService:205` and `ThreatIntelRefreshService:162-179` both implement exponential backoff with caps manually. Each ~20-line block becomes `BackoffOnError = BackoffPolicy.Exponential(base: 5s, cap: 1h, resetOnSuccess: true)`.
- **Three Group A files use `PeriodicTimer` directly** (`AnomalySaverService:130`, `LicenseStateRefreshService:49`, and the FOSS `BrowserVersionService:100`) — cleanest pilot targets, their cadence call-site swaps to a one-line registration.
- **Caveat for the next sweep:** `AnomalySaverService` and `DeploymentNormCalibrationService` both contain `Task.Delay(TimeSpan.FromSeconds(1))` inside tight inner loops. These look like cadence in grep but are channel-consumer polling patterns. Group A vs B reclassification needed once the bodies are read.

**Three primary jobs:**

1. **Central tick cadence.** Coordinator emits tick signals on configurable cadences: `tick.10s`, `tick.30s`, `tick.1m`, `tick.5m`, `tick.1h`. Units hook to the cadence they care about instead of each spinning their own `PeriodicTimer`. One place to tune, one place to observe. The cadences themselves come from `ScheduleCoordinatorOptions` so operators can re-pace background work in low-spec deployments without code changes.

2. **Completion-signal escalation.** When a unit's body returns, the coordinator emits `unit.completed:{name}` (plus a domain signal the unit may have raised internally, e.g. `fingerprints.absorbed`). Other units chain via `after: ["fingerprints.absorbed"]` — drift recompute now runs *because absorption finished*, not "every 5 minutes hoping absorption already ran." The whole app listens for domain signals (queries the relevant atom on signal arrival); it never polls.

3. **Resource-aware pipelining.** On low-resource deployments (Pi, edge box, small VPS) the coordinator runs units sequentially within a tick — pipeline mode. On bigger boxes, fires them in parallel. Mode is config + signal-driven: `EphemeralOptions.CancelOnSignals` / `DeferOnSignals` (from the Ephemeral catalogue) are the underlying primitives. Same code, different deployment posture.

**The 39 BackgroundService classes become registrations.** Example:

```csharp
// Before — FingerprintAbsorptionService.cs (154 lines + own logger + own timer):
public sealed class FingerprintAbsorptionService : BackgroundService { ... }

// After — registered as a unit in DI bootstrap (3 lines):
schedule.Register(
    "fingerprints.absorb",
    on: TickCadence.OneMinute,
    body: ct => absorber.RunAsync(ct));

// Drift then registers off the absorption completion signal:
schedule.Register(
    "fingerprints.drift",
    on: TickCadence.FiveMinutes,
    after: ["fingerprints.absorbed"],
    body: ct => drift.RunAsync(ct));
```

The absorption / drift / calibration classes shrink to small functions or stateful atoms; the lifecycle/scheduling concerns lift entirely into the coordinator.

**Exit:** ~24 of the 39 hosted-services collapse into cadence registrations + 1 becomes a pure signal-subscriber (Group D, see below) + 2 stay as event-driven hosts (Group C MonitoringPacks). Net delta is ~5,000-8,000 lines down to ~1,500-2,500 lines + the coordinator. Free observability for the workflow view. Central cadence tunable in one place.

**Signal-name convention** (crystallised from the real dependency graph below):
- Form: `{subject_plural}.{verb_past_tense}` — `fingerprints.absorbed`, `weights.recalibrated`, `entities.resolved`, `bots.clustered`, `reputation.swept`, `botlist.updated`, `threat_intel.refreshed`.
- Verb is past tense because the signal fires AFTER the work completed.
- Subject is plural — the unit acted on a collection.
- Subdomain prefix groups related signals: `fingerprints.*`, `weights.*`, `botlist.*`, `threat_intel.*`.

**Real Group A dependency graph (derived from actual store-method reads/writes, not guessed):**

```
tick.1m ─► fingerprints.absorb        ─► fingerprints.absorbed
                                                │
                                       after ──┴─► fingerprints.drift_check ─► fingerprints.drift_checked
                                                                                          │
                                                                                 after ──┴─► identity.weight_recalibrate ─► weights.recalibrated
                                                                                                                                   │
                                                                                                                          subscribed by Group D:
                                                                                                                          identity.global_weights_cache.invalidate

tick.1m  ─► entities.resolve         ─► entities.resolved          (independent track)
tick.5m  ─► bots.cluster             ─► bots.clustered
tick.1h  ─► reputation.maintenance   ─► reputation.swept
tick.24h ─► botlist.update           ─► botlist.updated            (BackoffOnError)
tick.5m  ─► threat_intel.refresh     ─► threat_intel.refreshed     (StartupJitter, per-provider)
```

Provenance — each chain edge is traceable to actual code, not architecture-speak:
- `fingerprints.absorbed → drift_check`: `FingerprintDriftService:101` calls `_store.GetLatestObservationVectorAsync(fingerprintId)` — needs the centroid that absorption produced.
- `fingerprints.drift_checked → weight_recalibrate`: `IdentityWeightCalibrationService:77` calls `_store.ListFingerprintsAsync(ct)` — wants stable post-absorption + post-drift state of the whole corpus.
- `weights.recalibrated → global_weights_cache.invalidate`: `IdentityGlobalWeightsCache` exists to cache the global weight matrix; the only reason to invalidate is when calibration finishes.

This is the StyloFlow workflow graph. The coordinator produces it from registrations — no separate "workflow definition" file needed.

**Fourth category — Group D: pure signal-subscribers (1+ classes):**

These don't need their own cadence and don't have their own queue. They subscribe to signals from elsewhere and react. The coordinator may *host* them so they participate in shutdown gracefully, but the cadence model doesn't apply.

| File | Subscribes to | Reaction |
|---|---|---|
| `IdentityGlobalWeightsCache` | `weights.recalibrated` | Invalidate cache; next read refetches |

Expect more Group D candidates to emerge during migration — any class that exists *only* to invalidate state when something upstream changed should move to Group D rather than carry its own tick.

### 3.0.2 The missing `SignalSink → SignalR` bridge

For the StyloFlow workflow view to light up from the same signals that the `ScheduleCoordinator` and detection contributors emit, a small subscriber needs to exist that doesn't today. Audited 2026-05-28:

**What exists:**
- `Hubs/StyloBotDashboardHub` — the dashboard SignalR hub
- `Services/DashboardSummaryBroadcaster` — periodic dashboard summary broadcaster (BackgroundService, will be a Group A registration after migration)
- `Services/SignalRBroadcastConstrainer` — rate-limiting/throttling primitive for broadcasts
- `Services/LlmResultSignalRCallback` — bridges LLM result events to SignalR (domain-specific)
- `Services/ClusterDescriptionSignalRCallback` — bridges cluster-description events to SignalR (domain-specific)
- `Middleware/DetectionBroadcastMiddleware` — per-request detection-event publisher → SignalR

**What's missing:** a generic subscriber `SignalSinkBroadcaster` that forwards `SignalSink` events to `StyloBotDashboardHub`. The user's own architectural framing ("think of signals raising events on the signal sink as like the signalr model in the front end") points at this; the wiring just hasn't been built.

**Shape (Group D — pure signal-subscriber):**

```csharp
public sealed class SignalSinkBroadcaster : IAsyncDisposable
{
    public SignalSinkBroadcaster(
        SignalSink sink,
        IHubContext<StyloBotDashboardHub> hub,
        SignalRBroadcastConstrainer constrainer,
        IOptions<DashboardSignalForwardingOptions> options,
        ILogger<SignalSinkBroadcaster> logger)
    {
        // Subscribe to sink via OnSignalAsync (or sink.Sense() snapshots,
        // depending on which fits the existing ephemeral SignalSink API).
        // For each signal raised:
        //   - filter by name-prefix from options (e.g. forward only "fingerprints.*",
        //     "weights.*", "bot_detection.*", "schedule.*")
        //   - rate-limit through constrainer (existing primitive)
        //   - broadcast to "WorkflowView" or "Dashboard" SignalR group
    }
}
```

**Collapsing the existing domain-specific bridges:** `LlmResultSignalRCallback` and `ClusterDescriptionSignalRCallback` become two prefix entries in `DashboardSignalForwardingOptions.ForwardedPrefixes` (`["llm.result", "cluster.description"]`) and delete. One generic bridge handles all signals; per-domain logic stays inside the units that emit, not on the wire.

**Exit criterion:** after Campaign 3 §3.0 ships, the workflow-view dashboard widget can subscribe to "schedule.*" signals on the SignalR hub and render the live state of every background unit — which one is running right now, when each last completed, what signals each emitted — with zero new backend wiring per unit.

This is the connecting tissue between the backend distillation (Campaign 2 + 3) and the frontend dashboard observability the user has been describing. Worth a single PR on its own once the `SignalSink` API surface is stable.

**Classification of the 39 FOSS hosted-services (audited 2026-05-28):**

*Group A — Cadence-tick candidates (collapse to `ScheduleCoordinator.Register(...)`, ~25 classes):*

| File | Path | Cadence shape |
|---|---|---|
| `FingerprintAbsorptionService` | Identity/ | periodic absorption (Campaign 2.4 pilot) |
| `FingerprintDriftService` | Identity/ | periodic drift recompute (Campaign 2.4 pilot) |
| `IdentityWeightCalibrationService` | Identity/ | periodic recalibration (Campaign 2.4 pilot); emits `weights.recalibrated`; chains `after: ["fingerprints.drift_checked"]` |
| `SessionPersistenceService` | Data/ | periodic flush to DB |
| `PopulationMarkovService` | Markov/ | periodic Markov model recompute |
| `AnomalySaverService` | Persistence/ | periodic batch save |
| `BotClusterService` | Services/ | periodic clustering |
| `BotListUpdateService` | Services/ | periodic external feed pull |
| `BrowserVersionService` | Services/ | periodic UA-DB refresh |
| `CentroidSequenceRebuildHostedService` | Services/ | periodic rebuild |
| `CommonUserAgentService` | Services/ | periodic reload |
| `DeploymentNormCalibrationService` | Services/ | periodic deployment-norm recompute |
| `EntityResolutionService` | Services/ | periodic resolution sweep |
| `ReputationMaintenanceService` | Services/ | periodic reputation cleanup |
| `SignatureConvergenceService` | Services/ | periodic convergence check |
| `SignatureDescriptionService` | Services/ | periodic description warm-up |
| `VectorCompactionService` | Services/ | periodic vector-table compaction |
| `VerifiedBotRegistry` | Services/ | periodic registry refresh |
| `ThreatIntelRefreshService` | ThreatIntel/ | periodic feed refresh |
| `DashboardSummaryBroadcaster` | UI/Services/ | periodic SignalR broadcast |
| `RemoteMetricCollector` | UI/Services/ | periodic remote-mode pull |
| `LicenseStateRefreshService` | Licensing/ | periodic license recheck — note: jitter + signed-timing matters |
| `ConfigurationWatcher` | Orchestration/Manifests/ | watcher: hybrid (FS event + cadence fallback) |
| `SessionAtomizerService` | Services/ | inverse naming irony: name says "Atomizer" but it's `while(!ct) { AtomizePass(); await Task.Delay(RunInterval); }` — name refers to what it does to sessions, not the architectural atom pattern. Periodic flush, cadence-tick candidate. |

*Group B — Queue-driven coordinators (become standalone `EphemeralKeyedWorkCoordinator` / `EphemeralWorkCoordinator` atoms + tiny warmup wrappers, ~8 classes):*

| File | Path | Atom shape |
|---|---|---|
| `IdentityProcessingCoordinator` | Identity/ | `EphemeralKeyedWorkCoordinator` (Campaign 2.3) |
| `IntentClassificationCoordinator` | Services/ | `EphemeralKeyedWorkCoordinator` |
| `LlmClassificationCoordinator` | Services/ | `EphemeralKeyedWorkCoordinator` |
| `ThreatIntelEnrichmentQueue` | ThreatIntel/ | `EphemeralWorkCoordinator` |
| `BoundedChannelLearningBus` | Services/ | `EphemeralWorkCoordinator` (already channel-based) |
| `LearningBackgroundService` | Services/ | verified Channel-driven (`await foreach _eventBus.Reader.ReadAllAsync(ct)`) — `EphemeralWorkCoordinator` consuming the bus |
| `BackgroundEnrichmentService` | Services/ | verified Channel-driven with DropOldest backpressure — `EphemeralWorkCoordinator` with `DeferOnSignals` for backpressure |

*Group C — Legitimately hosted-service-shaped (stay as `IHostedService`, just shrink, ~6 classes):*

| File | Why it stays |
|---|---|
| `RouteNameStoreInitializer` | One-shot init before traffic accepted |
| `SignatureCoordinatorWarmupService` | Tiny warmup wrapper — pulls singleton, calls one init method |
| `SessionVectorWarmupService` | Same shape |
| `SignatureAggregateCacheWarmupService` | Same shape |
| `VisitorCacheWarmupService` | Same shape |
| `MeterListenerService` | `MeterListener` consumer (System.Diagnostics.Metrics event-driven), not cadence — but emits domain signals from listener callbacks |
| `GatewayMeterAccumulator` | Same shape as `MeterListenerService` — `ExecuteAsync` returns immediately after listener setup, work happens in callbacks. Has `IReadOnlyList<MetricSnapshotDto> GetCurrentSnapshot()` query accessor — actually atom-shaped in spirit, `BackgroundService` is lifecycle plumbing only. |

The Group A → ScheduleCoordinator migration is the biggest single win. Group B is per-file atom refactors (each one its own small campaign). Group C stays as-is.

### 3.0.0 Commercial-side hosted-services (~12, same pattern)

The commercial codebase has 12 hosted-services. Same lens applies — most are cadence-tick candidates. The interesting finding is `Compliance/Guardians/ComplianceGuardianService.cs`, which is **already a mini-coordinator**: maintains `nextRun[guardian.Name] = DateTime.UtcNow.Add(guardian.Interval)`, polls every 1 minute, runs due guardians. Locally implements what `ScheduleCoordinator` does globally. **Proof the wider pattern is right.** Once the global coordinator ships, each guardian registers directly with its own `Interval`; `ComplianceGuardianService` deletes (~80 lines gone).

| File | Path | Group | Shape |
|---|---|---|---|
| `ComplianceGuardianService` | Compliance/Guardians/ | A | Mini-coordinator, dissolves: guardians register individually |
| `GatewayRegistrationService` | GatewayPlugin/ | A | Heartbeat loop |
| `GoodBotIpRangeRefreshService` | GatewayPlugin/IpRangeVerifier/ | A | Periodic IP-range refresh |
| `GuardianLicenseGate` | Guardian/ | A | Periodic license recheck (30s + 24h delays — verify if two units or jitter+cadence) |
| `DatabaseCleanupService` | Persistence.Postgres/Services/ | A | 24h cleanup |
| `FeedPollingService` | ThreatIntel/Feeds/ | A | Periodic threat-intel pull |
| `ReportScheduler` | Reporting/Scheduling/ | A | Already-correct-named; still registers with global coordinator |
| Inline loop in `Persistence.Postgres/ServiceCollectionExtensions.cs:283` | DI extension | A | 24h cleanup registered from DI helper |
| `PolicyTemplateSeeder` | ControlPlane/Policies/ | C | One-shot seeder |
| `LicenseValidatorStartupService` | GatewayPlugin/Licensing/ | C | One-shot startup |
| `DatabaseInitializationService` | Persistence.Postgres/Services/ | C | Schema init |
| `Guardian/Extensions/ServiceCollectionExtensions.cs` | DI extension | verify | Likely DI helper not a class; needs read |

Commercial Group A migrates to the same `ScheduleCoordinator` (singleton lives in `Mostlylucid.BotDetection`, commercial services register against it via the FOSS extension interface). The licensing-paid features still gate via `IStyloBotLicenseGate` — coordinator doesn't care which units are commercial. Same one coordinator owns all background work across FOSS + commercial.

**Sequencing relative to other campaigns:**
- Campaign 2 §2.4 (the four periodic identity services) is the **pilot** for this pattern — they're the first registrations.
- Campaign 2 §2.3 (`IdentityProcessingCoordinator`) is **independent** — that's queue-driven, not scheduled, so it remains its own atom (`EphemeralKeyedWorkCoordinator`) and *uses* `ScheduleCoordinator` only for any cleanup ticks.
- Campaign 3 §3.0.1 (below) is the orchestrator migration — separate concern, same campaign.

### 3.0.1 Complete the partial Ephemeral-orchestrator migration

The DI comment in `ServiceCollectionExtensions.cs:603` says "EphemeralDetectionOrchestrator is the active orchestrator; BlackboardOrchestrator kept for direct injection in tests." Misleading. The audit (this exploration round) shows that 5 production sites still inject the concrete `BlackboardOrchestrator` type instead of `IDetectionOrchestrator`:

| Site | Reason for pin |
|---|---|
| `Mostlylucid.BotDetection.Api/Endpoints/DetectEndpoints.cs:29, 39` | Two endpoint handlers parameter-typed as `BlackboardOrchestrator`. Mechanical migration. |
| `Mostlylucid.BotDetection/Endpoints/PolicyEndpoints.cs:141` | Same pattern. Mechanical. |
| `Stylobot.Gateway/Services/ProfileAnalysisWorker.cs:102` | `services.GetService<BlackboardOrchestrator>()` — service-location anti-pattern. Switch to `IDetectionOrchestrator` ctor injection. |
| `Mostlylucid.BotDetection.Sidecar/Services/DetectionGrpcService.cs:12, 17` | Field + ctor typed concrete. Mechanical. |
| `Mostlylucid.BotDetection.Benchmarks/DetectionPipelineBenchmarks.cs` + `Harness/PipelineBenchmarkRunner.cs` | Benchmark harnesses. Migrate so benchmarks measure the active orchestrator (currently measuring the dead one — benchmark results are meaningless). |

The 2 unit-test sites (`BotDetectionMiddlewareTests`, `BotDetectionMiddlewarePiiMaskingTests`) also inject the concrete class — migrate those last; they're proving behaviour the production code never sees.

Verification before deletion: each migrated site must keep its existing behaviour (the two implementations should be API-equivalent on `IDetectionOrchestrator`). If a site uses a method that only exists on the concrete class, that's a sign the interface needs widening before the migration — or that the new orchestrator is missing a feature.

**Exit:** `BlackboardOrchestrator.cs` (1587 lines) + the dead `OrchestratorOptions`/`BlackboardState` types defined there + `services.TryAddSingleton<BlackboardOrchestrator>()` registration all delete. The 33 `_logger.Log*` calls in `BotDetectionMiddleware.cs` shrink as the "Uses the BlackboardOrchestrator for..." doc-comments rot out with the type.

### 3.1 Per-request log audit

The hot-path log smear (counted this exploration round):

| File | LogInformation/Debug/Trace calls |
|---|---|
| `BotDetectionMiddleware.cs` | 33 |
| `BlackboardOrchestrator.cs` | 15 (deletes with 3.0) |
| `EphemeralDetectionOrchestrator.cs` | 10 |
| `ContributingDetectors/SessionVectorContributor.cs` | 7 |
| `ContributingDetectors/ContentSequenceContributor.cs` | 7 |
| `SignatureEscalatorAtom.cs` | 5 |
| `ResponseDetectionOrchestrator.cs` | 5 |
| `ResponseCoordinator.cs` | 5 |
| `FastPathSignatureMatcher.cs` | 5 |

For each result:
- **Genuine error/exception emission**: keep.
- **One-shot lifecycle event** (startup banner, config-loaded, "service initialised"): keep.
- **Per-request observability** ("middleware processed X", "got header Y", "saw signature Z"): convert to signal on the existing SignalSink. The signal name encodes the context; the value (if any) is a Model-2 hint.

`BotDetectionMiddleware.cs` is the biggest smear (33 calls). Detailed breakdown:

| Sites | Count | Category | Action |
|---|---|---|---|
| Lines 155, 193, 238, 1158 | 4 | Skip-decision (per-request `LogDebug`) | → signals: `bot_detection.skipped:attribute`, `bot_detection.skipped:api_key_disable`, `bot_detection.skipped:legacy_api_key_bypass`, `bot_detection.skipped:excluded_path` |
| Lines 201, 214, 1145 | 3 | API-key overlay events (per-request `LogDebug`) | → signals: `bot_detection.api_key_overlay_applied`, `bot_detection.api_key_rejection_cleared`, `bot_detection.api_key_overlay_merged` |
| Lines 1045, 1061, 1073, 1083, 1096, 1122, 1325 | 7 | Policy-resolution from various sources (`LogDebug`) | → ONE consolidated signal `bot_detection.policy_resolved:{source}:{policy_name}` where source ∈ `query`, `header`, `force_slow`, `force_fast`, `attribute`, `sandbox`, `api_key_overlay` |
| **Lines 459, 1628** | **2** | **Load-shed (per-request `LogInformation`, prod-active)** | → signal `bot_detection.load_shed:{path}` — HIGH VALUE: dashboard widget for live shedding rate |
| Lines 634, 702, 958, 966 | 4 | Detection-result emissions (`LogInformation`) | → signals: needs case-by-case read; likely `detection.completed:{verdict}` or per-tier emissions |
| Lines 1403, 1676, 1796, 1851 | 4 | Mid-pipeline lifecycle (`LogInformation`) | → likely signals: detection-stage transitions |
| Lines 2317, 2490, 2607 | 3 | Late-pipeline lifecycle (`LogInformation`) | → signals candidate; needs context read |
| Lines 1531, 1574 | 2 | Test/admin mode triggers (`LogInformation`) | → signals (audit trail): `bot_detection.test_mode_triggered:{mode}`, `bot_detection.custom_ua_test_triggered` |
| Lines 2086, 2110, 2142 | 3 | JSON parse-exception `LogDebug(ex, ...)` | **Keep** — legitimate exception emission with context |
| Line 2205 | 1 | Single uncategorized `LogDebug` | Needs context read |

Net: **~28 of the 33 become signals; ~5 stay as logs**. The load-shed conversion is the biggest user-visible win — operators get live shedding-rate visibility on the dashboard the moment Campaign 3 §3.1 ships for this file. The 7 policy-resolution sites collapsing to one consolidated signal is the biggest line-count win.

### 3.2 Hand-rolled-state-to-atom audit

`grep -rn "private.*ConcurrentDictionary\|private.*LinkedList\|new ConcurrentDictionary" src/` and judge each:
- **Truly request-scoped or ephemeral** (e.g. one-off per-request collection): keep.
- **Stateful coordinator with TTL/LRU semantics**: candidate for `SlidingCacheAtom` or `EphemeralLruCache`. List as separate campaign item.
- **Per-key ordering / sequential update**: candidate for `KeyedSequentialAtom`.

Each finding becomes its own sub-sweep with the `SignatureCoordinator` Ephemeral refactor as the template.

### 3.3 Cross-cutting tests

After each sub-sweep:
- Unit tests on the atom-backed component (use the existing `SignatureCoordinator` and `IdentityProcessingCoordinatorTests` as the test pattern reference).
- Integration test: the existing dashboard / detection pipeline runs unchanged from the outside; only the inside is now atom-based.

## Sequencing across the campaigns

```
Now ──────────────────────────────────────────────────────────────────►

Campaign 1 (edge-headers + radar)     ████████████
                                                │
Campaign 2.1 (CachedFingerprintReader)          ████  (started in Section A)
Campaign 2.2 (write invalidation)                    ████
Campaign 2.3 (IdentityProcessingCoordinator)              ██████
Campaign 2.4 (raw-call audit)                                   ████

Campaign 3.1 (log audit)                                              ████████
Campaign 3.2 (hand-rolled state audit)                                        ████████
Campaign 3.3 (per-finding sub-sweeps)                                                 ──────►
```

Each campaign is paused between sub-items for Playwright verification, full test run, and a second look at the diff. No long-running branches; each commit lands on `main`. Builds clean before each push.

## What is explicitly NOT in scope

- New product features. Feature frenzy is over.
- Frontend rewrites. The dashboard's SSR + SignalR invalidation pattern is correct; only data-source layers refactor.
- The marketing site's membership DB. Independent concern.
- Touching `.89` (prod) without explicit approval, per existing memory.
- Phase D (centroid snapshots / behavioural evolution session-overlay). Deferred; the visitor-detail page in Campaign 1.F renders the current shape only.

## Exit criteria for the whole campaign

- Every `IFingerprintReader` consumer goes through `CachedFingerprintReader`. No raw `SqliteFingerprintStore` reads in middleware / view components / contributors.
- `IdentityProcessingCoordinator` is replaced by `KeyedSequentialAtom`-backed code.
- Per-request `LogInformation` / `LogDebug` calls converted to signals or removed.
- Hand-rolled LRU/TTL caches converted to Ephemeral atoms.
- Home card + signature detail radar both render in Playwright.
- Test suites all green: `FingerprintMatcherConvergenceTests`, `IdentityProcessingCoordinatorTests` (or its replacement), `SignatureCoordinator` tests, `CachedFingerprintReader` tests, `BdfReplayTests`.
- No production log spam; SignalSink-based observability is the inspection surface.

## The discipline

After each commit:
1. Read the diff. Did I introduce any smear (left column of the catalogue)?
2. Did I add an `ILogger.Log{Info,Debug}` that isn't a genuine error or lifecycle event?
3. Did I add a `Concurrent*` collection that's an atom in disguise?
4. Did I write a signal whose payload IS the state?

If yes to any: revise before push. The campaign is the discipline of catching the smear before it becomes the next session's "wait you did what".
