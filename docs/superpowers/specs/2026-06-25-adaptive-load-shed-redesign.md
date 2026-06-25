# Adaptive load-shed redesign

**Date:** 2026-06-25
**Status:** Design approved, ready for implementation plan
**Project:** Mostlylucid.BotDetection (FOSS)

## Goal

Replace the global-latency-baseline load shed that incorrectly trips on
mixed-workload hosts (the bug that caused intermittent staging 503s on
`/dashboard/entity/{id}` while `/img/*` stayed healthy) with a
per-endpoint perf centroid model that learns each endpoint's own normal
and only sheds when the system is genuinely under duress. Pair that with
a visitor-class-aware shed decision: never shed verified humans by
default, shed verified bots first, treat unknowns at an operator-tunable
fraction.

## Background

The current `PipelineLoadSensor` learns a single global baseline for
upstream RTT (the post-detection portion of total request time). When a
host serves both `/img/stylowall.svg` (about 10 ms total) and
`/dashboard/entity/{id}` (about 110 ms total because of database lookups
and view-component fan-out), the baseline locks near the fast paths and
the dashboard URL reads as 11x baseline. With `CriticalRatio = 5.0` the
band escalates to Critical and `LoadShedDecision` refuses 50 percent of
requests with 503 + Retry-After. Verified humans are caught in that 50
percent because the existing per-request shed hint is dropped at
Critical "to save the cache lookup cost", a tradeoff that was never
actually expensive.

Two things are wrong:

1. One global latency baseline cannot fit a host with multiple endpoints
   whose intrinsic latencies differ by an order of magnitude. The
   sensor was originally designed for a YARP gateway proxying to one
   backend, where every request shares roughly the same latency
   profile.

2. The shed decision is class-blind under pressure. Human visitors see
   503s for a reason that has nothing to do with their fingerprint.

This redesign fixes both at the same time.

## Architecture

Three new pieces plus one rewrite, all in `Mostlylucid.BotDetection`:

1. **`IEndpointPerfBaseline`** in
   `src/Mostlylucid.BotDetection/Services/`. One method,
   `double GetExpectedMs(string method, string normalizedPath)`,
   returning the p95 the system has observed for the template, or `0`
   when no trustworthy baseline exists yet.

2. **`DashboardEventStoreBackedEndpointPerfBaseline`** in the same
   folder. On each `IScheduleCoordinator.Tick1m`, calls
   `IDashboardEventStore.GetEndpointStatsAsync`, groups the raw-path
   rows by `(method, PathNormalizer.Normalize(path))`, computes a
   per-template p95 (weighted by per-row count) and a per-template
   total sample count, then atomically swaps the in-memory dictionary.
   `GetExpectedMs` is a lock-free read against the snapshot. Returns
   `0` when the template has fewer than
   `MinSamplesForTrustedBaseline` aggregated samples (strict
   less-than, so the threshold is the floor). Optional DI registration:
   absent on hosts without `IDashboardEventStore`, in which case
   consumers degrade to "no baseline, ratio 1.0, no shed contribution"
   per the remote-mode-optional-DI pattern.

3. **`PipelineLoadSensor.RecordUpstreamDeviation(double ratio)`**
   replaces `RecordUpstreamRtt(double ms)`. The EWMA is now over a
   dimensionless ratio. Band fires on the ratio crossing `HighRatio`
   (default 2.0) or `CriticalRatio` (default 5.0). Same math shape as
   today, semantically clearer.

4. **`LoadShedDecision` rewrite**. Old signature took
   `(LoadBand, LoadShedOptions, seed, shedHint)`. New signature takes
   `(LoadBand, VisitorClass, LoadShedOptions, seed)`. The visitor
   class is resolved upstream from the cached fingerprint verdict
   against the policy's `HumanGate` and `BotGate` thresholds. The
   "shed hint dropped at Critical" branch goes away; the gate is
   respected at every band.

What does not change: detection-latency axis (already correct after the
2026-06-25 percentile-baseline fix), threadpool starvation axis,
Gen2 GC axis, the seed mechanism for fairness across requests.

## Data flow at request time

```
Request enters middleware
  |
  v
[Step 1] Resolve visitor class from cached verdict
   cachedVerdict = _fingerprintCache?.Peek(fingerprint)
   class =
     if (prob <= HumanGate.MaxBotProb AND conf >= HumanGate.MinConfidence) Human
     else if (prob >= BotGate.MinBotProb AND conf >= BotGate.MinConfidence) Bot
     else Unknown
  |
  v
[Step 2] Read current band
   band = _loadSensor.CurrentBand  // Low / Normal / High / Critical
  |
  v
[Step 3] LoadShedDecision.ShouldShed(band, class, policy.LoadShed, seed)
   // Normal and Low bands always pass (return false). No per-class
   // fraction lookup at those bands; shed only engages when the
   // sensor flags real pressure.
   if (band is Low or Normal) return false;
   fraction = class switch {
     Human   => policy.LoadShed.HumanShedAt{band}     // default 0.0 always
     Unknown => policy.LoadShed.UnknownShedAt{band}   // default High=0.3, Critical=0.7
     Bot     => policy.LoadShed.BotShedAt{band}       // default High=1.0, Critical=1.0
   }
   return hash(seed) < fraction * uint.MaxValue
  |
  v
[Step 4a if shed]
   write 503 + Retry-After + X-StyloBot-Shed
   skip _next; skip sensor recording (existing BotDetectionShedKey check preserved)
[Step 4b if pass]
   run detection + downstream as today
  |
  v
[Step 5, Response.OnCompleted, non-shed path only]
   detectionMs = AggregatedEvidence.TotalProcessingTimeMs
   _loadSensor.RecordDetectionLatency(detectionMs)

   upstreamMs = totalLatencyMs - detectionMs
   normalizedPath = PathNormalizer.Normalize(path)
   expectedMs = _endpointPerfBaseline?.GetExpectedMs(method, normalizedPath) ?? 0
   ratio = expectedMs > 0 ? upstreamMs / expectedMs : 1.0
   _loadSensor.RecordUpstreamDeviation(ratio)
```

### Invariants

- Shed runs before detection. Visitor class comes from cached verdict
  only; fresh fingerprints are `Unknown`.
- Sheds do not feed the EMAs. The existing `BotDetectionShedKey` check
  in the OnCompleted hook is preserved unchanged.
- Cache miss or no baseline means ratio 1.0. A first-ever request to a
  new endpoint cannot trip pressure on its own. The baseline
  materializes once
  `DashboardEventStoreBackedEndpointPerfBaseline` has aggregated at
  least `MinSamplesForTrustedBaseline` (default 30) requests for that
  template.
- Detection-latency axis trips independently. A stylobot pipeline
  overload (slow detection itself) fires Critical regardless of
  endpoint perf. That is the right "stylobot is the bottleneck"
  signal.

## Configuration

### `LoadShedOptions` (per-policy, extend in place)

```csharp
public sealed record LoadShedOptions
{
    // Existing fields. Semantics now: the UNKNOWN-class default fractions.
    // Existing operator config keeps its numeric meaning because Unknown is
    // the modal class today (most requests have no warm fingerprint).
    public double DropFractionAtHigh     { get; init; } = 0.2;
    public double DropFractionAtCritical { get; init; } = 0.5;

    // New: visitor-class gates against the cached fingerprint verdict.
    public ClassGate HumanGate { get; init; } = new(MaxBotProb: 0.3, MinConfidence: 0.7);
    public ClassGate BotGate   { get; init; } = new(MinBotProb: 0.5, MinConfidence: 0.7);

    // New: per-class per-band shed fractions.
    // Defaults express the contract: humans never shed by default, bots always.
    // Normal and Low bands never shed any class (no per-band fields needed at
    // those bands; the data-flow lookup short-circuits to 0 for them).
    public double HumanShedAtHigh       { get; init; } = 0.0;
    public double HumanShedAtCritical   { get; init; } = 0.0;
    public double UnknownShedAtHigh     { get; init; } = 0.3;
    public double UnknownShedAtCritical { get; init; } = 0.7;
    public double BotShedAtHigh         { get; init; } = 1.0;
    public double BotShedAtCritical     { get; init; } = 1.0;
}

// Gate boundaries are INCLUSIVE on both sides: prob <= MaxBotProb (human side),
// prob >= MinBotProb (bot side), conf >= MinConfidence (both sides). A verdict
// exactly at the boundary qualifies.
public sealed record ClassGate(
    double MaxBotProb   = 1.0,
    double MinBotProb   = 0.0,
    double MinConfidence = 0.0);
```

### `PipelineLoadSensorOptions` (system-wide, extend in place)

```csharp
public sealed class PipelineLoadSensorOptions
{
    // ...all existing fields unchanged...

    // New: below this sample count for a template, GetExpectedMs returns 0
    // (treated as unknown so the request contributes a neutral 1.0 ratio).
    public int MinSamplesForTrustedBaseline { get; set; } = 30;

    // New: how often DashboardEventStoreBackedEndpointPerfBaseline refreshes
    // its in-memory snapshot from the dashboard store. Piggybacks on the
    // ScheduleCoordinator Tick1m signal.
    public TimeSpan BaselineRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
}
```

### Configurable settings summary

Every threshold, fraction, sample count, and interval lives on an
Options class per the all-settings-configurable rule. No magic numbers
in the implementation files. Operators tune by binding under
`BotDetection:PipelineLoadSensor:*` (system-wide) or
`BotDetection:DetectionPolicies:<name>:LoadShed:*` (per-policy YAML or
appsettings).

## Migration and backward compatibility

`DropFractionAtHigh` and `DropFractionAtCritical` keep their existing
field names and numeric semantics, but now apply specifically to the
unknown-class population. Operator config that customized those values
continues to behave as expected for the modal request shape (no warm
fingerprint). The semantic of those two fields is documented in their
XML comments.

Humans go from "sometimes shed at the existing fraction" to "never
shed by default". That is an improvement, not a regression, and
satisfies the prioritise-humans guarantee already documented in
`AdaptiveScalingOptions`.

Bots go from "sometimes shed at the existing fraction" to "always shed
when the band escalates". An operator who wants the previous
permissive behavior must explicitly set `BotShedAtHigh` and
`BotShedAtCritical` to lower values. The default expresses the named
intent of adaptive shed: protect the system by dropping the traffic
class that does not need to be served.

The `LoadShedDecision.ShouldShed(...)` signature changes. Two call
sites today (`BotDetectionMiddleware`, one test fixture); both update
in lockstep with the new visitor-class resolution.

`RecordUpstreamRtt(double ms)` is removed from `PipelineLoadSensor`'s
public surface. One caller in the middleware updates to
`RecordUpstreamDeviation(double ratio)`. The detection-latency caller
is unchanged.

## Architectural constraint compliance

This design lands inside a system with hard architectural rules. Each
constraint and how the design satisfies it:

### Parasitic-aggregation bound

`DashboardEventStoreBackedEndpointPerfBaseline` groups raw-path rows
into normalized-template buckets and computes a per-template
weighted p95. That is a NEW aggregation projection that does not
exist on `IDashboardEventStore` today. To avoid the
"two p95s with different keying" failure mode (where future code
reads from this cache and gets a different number than dashboard
rendering or policy decisions get from the store):

- **This cache is for the load-shed hot path only.** Dashboard
  rendering, policy decisions, ops surfaces, exports, and every other
  read site continue to read raw-path stats from
  `IDashboardEventStore` directly.
- The cache exposes `IEndpointPerfBaseline` only. No
  `GetTemplateStats`, no `GetAllTemplates`, no "convenient" extra
  surface that would let other code start consuming it. A consumer
  that wants per-template aggregation must depend on a future spec
  that pushes template aggregation into `IDashboardEventStore`
  itself, not on this cache.
- Class is `internal` in `Mostlylucid.BotDetection`; only the
  middleware OnCompleted hook consumes it.

### Why not LFU on this cache

The standing rule is "every in-memory store: hot dictionary + bounded
channel + background drainer", and "LFU sliding cache is lookback".
That rule is for stores keyed by visitor / signature / fingerprint
where the working set is bounded by request volume and we must evict
cold entries.

This cache is keyed by `(method, normalizedTemplate)` where the
working set is bounded by the route table, typically a few hundred
templates per host. Full dictionary is correct; LFU would add
complexity without solving anything. The cache is also read-only on
the hot path (refresh is the only writer, and it runs on the tick
coordinator thread), so no bounded channel or drainer is required.

### Atom and signal positioning

`PipelineLoadSensor` is the atom for adaptive pressure state; it
already exists and this design preserves its shape. The atom holds
the band, the per-axis EMAs, and the sample windows.

`PressureSignalContributor` already projects sensor state into the
per-request signal dictionary (`pressure.band`,
`pressure.detection_latency_ratio`, and friends). That projection
continues unchanged; the meaning of the upstream-RTT projection
shifts from "absolute ms over baseline" to "deviation over endpoint
p95" but the signal key set is the same.

What this design does NOT add (deliberately): a band-transition
signal that fires when `CurrentBand` crosses Normal -> High ->
Critical or back. Today the sensor's state is read per-request via
property; nothing announces a band change as an event. Operators
that want to react have to poll. That is an existing pattern gap, not
introduced by this work; see Out of scope below.

### Centralised change detection

Refresh runs on `IScheduleCoordinator.Tick1m`, the same cadence the
dashboard already uses for aggregate refresh. No parallel
change-detection mechanism is introduced. The cache's snapshot swap
is the single update event; consumers (just the middleware OnCompleted
hook) read whatever is current at the moment of the call.

### Gateway data locality

The baseline lives on the gateway because `IDashboardEventStore`
lives on the gateway. Hosts that proxy or display dashboard data
remotely (the website host in remote mode) do not register
`IEndpointPerfBaseline`; the middleware degrades to "ratio 1.0, no
shed contribution" on those hosts, which is correct because those
hosts also do not run the gateway-side detection pipeline.

## Error handling

- `IEndpointPerfBaseline.GetExpectedMs` throws: caught at the
  OnCompleted hook, treated as "no baseline" (ratio 1.0), logged once
  at warn level (sampled, no log flood under sustained errors).
  Detection still proceeds, shed still works on the other axes.

- Fingerprint cache lookup fails or returns null: visitor class falls
  through to `Unknown`. Matches the existing optional-DI pattern for
  cached-verdict consumers on hosts where the cache is not registered.

- Policy carries no `LoadShedOptions`: defaults apply (humans never
  shed, bots always shed when band escalates, unknowns at the default
  fractions). Same shape as today's "missing options means defaults"
  behavior.

- `DashboardEventStoreBackedEndpointPerfBaseline` refresh fails (store
  unavailable, query timeout): the in-memory snapshot continues to
  serve the previous values; the failure is logged once per refresh
  interval at warn level. The baseline never becomes "stale and
  unannounced" because the operator sees the warn log; downstream
  shed continues to work on the last-good snapshot.

- `PathNormalizer.Normalize` throws on a pathological input: caught at
  the OnCompleted hook, treated as no normalization (use raw path,
  which most likely misses the baseline lookup, contributes neutral
  1.0). Logged once at warn level.

## Testing strategy

### Unit tests (FOSS test project)

`LoadShedDecisionTests`:
- Matrix of (band x visitor class) producing the expected fraction.
- Pin the "humans never shed by default" contract via assertion on
  the default `LoadShedOptions`.
- Verify an operator-overridden `HumanShedAtCritical > 0` does in
  fact shed humans at Critical (the gate is configurable, not
  hardcoded).
- Edge: prob exactly at the gate threshold (boundary inclusive vs
  exclusive must be explicit and tested).

`ClassGateResolverTests`:
- Cached verdict tuples mapping to the expected class.
- Missing cache returns `Unknown`.
- NaN / infinite prob or conf returns `Unknown` (defensive).
- Low confidence (below `MinConfidence`) returns `Unknown` regardless
  of prob.

`PipelineLoadSensorTests` (extend existing):
- New `RecordUpstreamDeviation` feeds an EWMA of the ratio.
- Band escalates at `HighRatio` and `CriticalRatio` against the
  EWMA-of-ratio.
- The existing `Baseline_RecoversFromAnomalouslyFastWarmupSample`
  test is removed. The bug it pinned cannot exist in the new model
  because there is no global latency baseline to lock to an outlier.

`DashboardEventStoreBackedEndpointPerfBaselineTests`:
- Refresh from a faked `IDashboardEventStore` populates the cache.
- `MinSamplesForTrustedBaseline` is honored (below threshold returns 0).
- Cache miss returns 0.
- PathNormalizer correctly groups `/entity/aaa` and `/entity/bbb` to
  the same template lookup.
- Failed refresh keeps the prior snapshot.

### Regression test (the staging bug)

`StagingMixedWorkloadShedTests`:
- Simulate 100 static-asset requests at 5 ms + 50 dashboard requests
  at 110 ms over a 60-second window.
- With per-endpoint baseline + EWMA-of-ratio, band stays Low.
- Run the same scenario through the old global-baseline model
  (preserved as `LegacyGlobalBaselineSimulator` test helper) and
  show it trips Critical. The contrast pins exactly what changed.

### Contract / integration test (the human-protection guarantee)

`HumansNeverShedUnderCriticalTests`:
- Drive `PipelineLoadSensor` to Critical via simulated
  detection-latency overload (independent axis, unaffected by the
  perf-baseline work).
- Verify a request with cached verdict (prob 0.2, conf 0.9) is
  admitted regardless of band.
- Verify a request with cached verdict (prob 0.8, conf 0.9) is shed.
- Verify an unknown request is shed at exactly the configured
  fraction (deterministic via seeded hash).

### Skipped

EWMA math is already pinned by existing `DegradationAtom` and
`PipelineLoadSensor` unit tests. The new code only changes the input
meaning (ratio vs absolute ms), not the math itself.

## Out of scope

- Route-template induction from observed traffic (separate spec in
  flight). This redesign uses the existing `PathNormalizer` for
  template grouping; the smarter induction-based templates will plug
  into the same `IEndpointPerfBaseline` lookup when they ship.

- Cross-host coordination of shed decisions (no fleet-wide shed
  protocol). Each gateway instance sheds locally based on its own
  observed pressure. Multi-host coordination would need a separate
  control-plane signal; not needed for the current failure mode.

- Dashboard surface for shed state (band, recent shed counts per
  class, baseline freshness). Worth a separate UI spec once this
  ships and operators want visibility into what is actually being
  shed and why.

- Band-transition signals from `PipelineLoadSensor`. Today the
  sensor exposes `CurrentBand` as a property; consumers poll. A
  proper announce-on-transition signal (Normal -> High -> Critical
  and back) fits the signals/atoms pattern and would let dashboard
  widgets and ops tooling react without polling. Out of scope for
  this work because it touches a different surface (the sensor's
  observer side, not its measurement side); deserves its own spec
  alongside any operator-visibility UI work.

- Pushing per-template aggregation into `IDashboardEventStore`
  itself. The cache currently transforms raw-path rows into
  template-keyed p95 inside the load-shed cache. Eventually this
  should be a first-class store query so dashboard surfaces, exports,
  and policy decisions can also reason about templates. Separate spec
  because it touches storage schema (or the SQLite query layer at
  minimum) and has its own migration concerns.

## References

- `feedback_centroids_not_rules` (per-endpoint p95 IS the centroid)
- `feedback_all_settings_configurable` (every threshold on Options)
- `feedback_centralised_change_detection` (single change-detection
  mechanism for the baseline refresh)
- `project_gateway_data_locality` (baseline reads gateway-local from
  `IDashboardEventStore`)
- `feedback_remote_mode_optional_di` (baseline absent on hosts
  without the store: graceful degrade)
- `feedback_no_quick_fixes` (the previous percentile-baseline fix
  was a partial mitigation; this is the proper fix)
- 2026-06-25 staging 503 incident on
  `/dashboard/entity/de82d21ea9244a6f`
- Existing per-policy `LoadShedOptions` shape
  (`src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs`)
- Existing `PipelineLoadSensor` and `PipelineLoadSensorOptions`
  (`src/Mostlylucid.BotDetection/Services/`)
- `PathNormalizer`
  (`src/Mostlylucid.BotDetection/Markov/PathNormalizer.cs`)
- `IDashboardEventStore.GetEndpointStatsAsync`
  (`src/Mostlylucid.BotDetection.UI/Services/IDashboardEventStore.cs`)
- `IScheduleCoordinator.Tick1m` (FOSS schedule coordinator pattern)