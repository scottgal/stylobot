# Adaptive learning loop — design spec

**Date:** 2026-06-21
**Status:** Design draft.
**Author:** Scott + Claude.
**Scope:** Replace the fixed `Tick1m + 30-min gate` calibration trigger with an adaptive policy; hook UA-anchored drift into the calibration pass; use that drift to shrink over-claiming umbrella archetypes. Same trigger pattern reused for other tick-driven services. No new ML algorithms — better signal handling around existing math.

---

## 1. Why this exists

Three symptoms, one root cause:

| Symptom | Where it bites |
|---|---|
| Demo runs look dead for 30 minutes — `identity_archetypes` and `identity_dimension_weights` stay empty | `src/Mostlylucid.BotDetection.Demo/bin/.../fingerprints.db` after a 20-min demo: 2 obs, 0 archetypes, 0 weights. Calibration tick has not fired even once. |
| `chrome-desktop` wins `FindNearest` against `chrome-privacy`'s own centroid | `CentroidLearningLoopTests.Distinct_visitor_shapes_resolve_to_distinct_archetypes` surfaced this on 2026-06-21. Self-resolution count is currently small because one umbrella dominates a whole UA family. |
| No way to tell whether learning is actually running, or whether an archetype is mis-sized | The calibration service has zero observable surface beyond a log line per tick. Operators have to dump SQLite to find out anything. |

Root cause: **the learning loop is wall-clock driven, blind to its own quality, and never narrows the territory it claimed at seed time.** Calibration fires on a wall clock regardless of whether there's anything to learn from. Archetype centroids refine from descendants' mean but their *catchment* (mask + variance + asserted UA) stays at seed-time width forever. So an over-broad seed eats neighbours and the system can't self-correct.

---

## 2. Three coupled mechanisms

Each section below is a discrete piece of work. They compose, but each lands on its own.

### A. Adaptive trigger policy — *when* to calibrate

Replace `if (now - lastRun >= CalibrationIntervalMinutes) RunOnceAsync()` with a composable policy:

```csharp
public sealed record AdaptiveTriggerPolicy
{
    /// <summary>Below this elapsed time, we never run. Prevents thrashing under bursty pressure.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Above this elapsed time, we always run (safety net). Null = no safety net.</summary>
    public TimeSpan? MaxInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Highest band we'll still fire at. Critical-load = defer.</summary>
    public LoadBand MaxBand { get; init; } = LoadBand.Medium;

    /// <summary>OR-of conditions on caller-supplied signals (obs.unabsorbed ≥ N, drift.l2 ≥ X, etc.).</summary>
    public IReadOnlyList<SignalCondition> AnyOfSignals { get; init; } = Array.Empty<SignalCondition>();
}

public readonly record struct SignalCondition(string Key, double Threshold);

public readonly record struct TriggerContext(
    DateTimeOffset Now,
    DateTimeOffset? LastSuccessfulRunUtc,
    LoadBand CurrentBand,
    IReadOnlyDictionary<string, double> Signals);

public readonly record struct TriggerDecision(bool ShouldRun, string Reason);
```

Evaluation (the entire rule, on purpose — there's nothing else to it):

```csharp
TriggerDecision Evaluate(AdaptiveTriggerPolicy p, TriggerContext c)
{
    var elapsed = c.LastSuccessfulRunUtc is { } last
        ? c.Now - last
        : TimeSpan.MaxValue;                        // never ran → behave as "infinitely overdue"

    if (elapsed < p.MinInterval)
        return new(false, $"min interval {p.MinInterval} not elapsed (have {elapsed})");

    if (p.MaxInterval is { } max && elapsed >= max)
        return new(true,  $"safety net: elapsed {elapsed} ≥ max {max}");

    if (c.CurrentBand > p.MaxBand)
        return new(false, $"band {c.CurrentBand} > ceiling {p.MaxBand}");

    foreach (var cond in p.AnyOfSignals)
    {
        if (!c.Signals.TryGetValue(cond.Key, out var v)) continue;
        if (v >= cond.Threshold)
            return new(true, $"signal {cond.Key}={v:F2} ≥ {cond.Threshold:F2}");
    }
    return new(false, "no signal condition met");
}
```

The trigger needs a heartbeat to be evaluated. Cheapest option: services subscribe to `TickCadence.Tick5s` (a NEW fast cadence — current shortest is `Tick1m`), evaluate internally, run if and only if the policy says yes. `Tick5s` carries no work itself; it's just a heartbeat.

**Signal source.** Each trigger reads signals from its own `IAdaptiveTriggerSignalSource`. For calibration:

```csharp
public sealed class CalibrationSignalSource
{
    private long _observationsSinceLastRun;
    private double _accumulatedDriftL2;

    // Hooked from SqliteFingerprintStore.RecordObservationAsync (one extra increment)
    public void OnObservation() => Interlocked.Increment(ref _observationsSinceLastRun);

    // Hooked from FingerprintAbsorptionService.AbsorbAsync (one extra Add)
    public void OnAbsorption(double centroidDeltaL2)
    {
        double current, updated;
        do { current = Interlocked.CompareExchange(ref _accumulatedDriftL2, 0, 0);
             updated = current + centroidDeltaL2; }
        while (Interlocked.CompareExchange(ref _accumulatedDriftL2, updated, current) != current);
    }

    public IReadOnlyDictionary<string, double> Snapshot() => new Dictionary<string, double>
    {
        ["obs.unabsorbed"] = Interlocked.Read(ref _observationsSinceLastRun),
        ["drift.l2"]       = Interlocked.CompareExchange(ref _accumulatedDriftL2, 0, 0),
    };

    public void Reset()
    {
        Interlocked.Exchange(ref _observationsSinceLastRun, 0);
        Interlocked.Exchange(ref _accumulatedDriftL2, 0);
    }
}
```

**Reset semantics: BEFORE the run.** If we reset after, observations that arrive during a long calibration pass get counted in the same cycle that's already in flight, and the next cycle thinks nothing happened. Resetting before means the next cycle's counters cover exactly the work-since-this-cycle-started.

### B. UA-anchored drift — *what* the observations tell us about archetype correctness

Insight (from Scott): **every observation already carries a UA family. When we see humans (`hdr.ua_family = "Chrome"`) drifting *far* from an archetype that claims to cover Chrome, the umbrella was wrong at seed time.** UA is a ground-truth witness we already have and don't use for verification.

Add a calibration-pass output: per-archetype, per-asserted-UA drift distribution.

```sql
CREATE TABLE archetype_drift_metrics (
    archetype_id           TEXT NOT NULL,
    ua_family              TEXT NOT NULL,           -- observed UA on the descendant fingerprint
    descendant_count       INTEGER NOT NULL,
    mean_l2_to_centroid    REAL NOT NULL,
    variance_l2_to_centroid REAL NOT NULL,
    p90_l2_to_centroid     REAL NOT NULL,           -- tails matter more than means for umbrella detection
    matches_asserted_ua    INTEGER NOT NULL,        -- 1 = ua_family equals archetype.AssertedUaFamily
    calibrated_at          TEXT NOT NULL,
    PRIMARY KEY (archetype_id, ua_family, calibrated_at)
);
```

`matches_asserted_ua` is the load-bearing column. Two cases:

1. **`matches_asserted_ua = 1` with high mean drift.** The archetype claims Chrome and is catching Chrome users, but they're drifting far. The umbrella is the right shape but the wrong *size* — its centroid sits in a region that doesn't actually represent the cluster's centre. **→ refine centroid (existing code) AND shrink mask** (see C).
2. **`matches_asserted_ua = 0` with non-trivial descendant count.** The archetype is catching observations whose UA disagrees with what the archetype asserts. The umbrella is over-claiming territory it doesn't own. **→ shrink mask / increase variance until those observations resolve elsewhere.**

Computed in one extra SQL pass at the end of `RunOnceAsync`. No new pipeline hooks needed beyond the existing per-observation UA capture.

### D. Archetype centroid mobility — *whether* the centroid can actually move

Distinct concern from C. C narrows *catchment*; D unsticks the centroid *position*.

Current refinement (`IdentityCalibrationOptions.ArchetypeRefinementCap = 0.7`):

```
new_centroid = α × old_centroid + (1−α) × descendants_mean,  α ≤ 0.7
```

For an umbrella with N descendants where N is large, `descendants_mean` is statistically very stable — but the descendants themselves were assigned to this archetype *because they were closest to its (possibly wrong) seed centroid*. The mean is therefore biased toward the seed, the refinement preserves 70% of the seed, and the centroid never escapes the seed-time position. Combined with C's catchment width, the system has positive feedback toward whatever the seed was, with no signal that says "you may be pinned in the wrong place".

Three rules to add:

1. **Adaptive α from descendant variance.** When descendants agree (low variance), keep α high — stability is desirable. When descendants disagree (high variance), drop α — the centroid is in a contested region and we want responsiveness. Concretely: `α_effective = clamp(α_cap × exp(−variance / scale), α_min, α_cap)` with `α_min = 0.2`. High-variance archetypes refine 4× faster than low-variance ones. Removes the lock-in for archetypes whose descendants are arguing.

2. **Neighbour-aware repulsion.** Compute pairwise centroid distances. For any pair `(A, B)` with `L2(A.centroid, B.centroid) < repulsion_radius` AND `B.descendantCount < A.descendantCount` (so B is the weaker one being eaten), push B's refinement target *away* from A by a small repulsion vector. Concretely:
   ```
   if dist(A, B) < r_rep AND |B.descendants| < |A.descendants|:
       repulsion = normalize(B.centroid - A.centroid) × repulsion_strength
       B.new_centroid = α × B.old_centroid + (1−α) × (B.descendants_mean + repulsion)
   ```
   Tiny `repulsion_strength` (e.g. 0.05). Effect: when a small archetype is being swallowed by a large neighbour, the calibration nudges it away on every pass instead of letting it collapse. Without this, the only way two archetypes diverge is if the dataset itself splits them — but the assignment bias means the dataset is the *result* of the centroids, not the input to them.

3. **Convergence detection (signal, not action).** If `dist(A, B) < merge_threshold` over `N` consecutive calibration cycles, log a `centroid.merge-candidate` signal with both ids. v1 takes no automatic action — emitting the signal is the win. Operators (or, post-v1, an auto-merge rule) decide whether the two archetypes have converged because they were redundant from the start, or whether one is eating the other.

The "pinning" the maintainer fears is rule 1's responsibility: if descendants disagree (high variance), the centroid moves fast and can escape the seed. Rule 2 stops a small archetype from being pulled silently into a neighbour. Rule 3 makes the failure mode visible even when 1 and 2 are off.

**Per-archetype mobility state to track** (extends `archetype_drift_metrics` from B):

| Field | Meaning |
|---|---|
| `centroid_delta_l2_this_cycle` | L2 between this cycle's pre/post centroid. The basic "did I move?" |
| `descendants_variance` | Per-dim variance of descendants' position around the centroid. The signal for adaptive α. |
| `nearest_neighbour_id` | Closest other archetype this cycle. |
| `nearest_neighbour_dist` | Distance to that neighbour. |
| `pin_cycles` | Consecutive cycles where `delta < ε`. Resets on movement. |

The killer dashboard query becomes: *list archetypes where `pin_cycles ≥ 5` AND `descendants_variance > τ`* — those are pinned-in-the-wrong-place. Operator sees them, can manually adjust the seed, or wait for rule 1 to kick in next cycle.

### C. Umbrella shrinkage — *how* the catchment narrows

The matcher already supports tight catchment (per-dim `DimensionMask`, per-dim `VarianceVector`, hard `AssertedUaFamily` gate). What's missing is the feedback loop that adjusts them.

Bloat metric, per archetype:

```
bloat = p90_l2_to_centroid(matches_asserted_ua = 1) / archetype_radius_baseline
```

where `archetype_radius_baseline` is a per-`ArchetypeKind` constant (tight client identities get a small radius, broad umbrella kinds a larger one — kept in the YAML).

Action ladder, applied during calibration after centroid refinement:

| Condition | Action |
|---|---|
| `bloat < 1.0` AND `matches_asserted_ua = 0` count is 0 | No change. Umbrella is correctly sized. |
| `bloat ≥ 1.0` AND `matches_asserted_ua = 1` count is high | **Shrink mask.** Identify the dims with the highest descendant variance among matching-UA descendants; multiply their mask entries by `(1 - shrinkRate)`. Tightens which dims the archetype "claims". |
| `matches_asserted_ua = 0` count is non-trivial | **Lower asserted-UA confidence ceiling** AND apply a mild global mask shrink. The archetype is leaking — make it more selective. |
| `bloat ≥ 2.0` | **Split candidate.** Log a `umbrella.split-candidate` signal with the archetype id; out-of-scope for v1 mechanism but we want the signal recorded so we know which archetypes are next. |

`shrinkRate` defaults to `0.05` (5% per calibration cycle). Floored — masks can't drop below `0.05 * original` so a confused archetype never zeroes out entirely. Bounded — over `N` cycles with no improvement, shrink rate decays toward 0 to prevent oscillation.

This converts the calibration pass from "average my descendants and overwrite my centroid" into "average my descendants AND narrow my catchment if my descendants are arguing with me".

---

## 3. Per-service trigger policy defaults

The trigger surface generalises beyond calibration. One column per signal/threshold.

| Service | MinInterval | MaxInterval | MaxBand | Any-of signals |
|---|---|---|---|---|
| `IdentityWeightCalibrationService`               | 30s | 6h  | Medium | `obs.unabsorbed ≥ 50`, `drift.l2 ≥ 0.5` |
| `FingerprintAbsorptionService` (backstop tick)   | 5s  | 5m  | High   | `obs.unabsorbed ≥ 1` (event-driven path is primary) |
| `WellKnownBotRefreshService`                     | 1h  | 24h | High   | — (time only) |
| `SqlitePathLifecycleStore` flush                 | 5s  | 5m  | Medium | `dirty.count ≥ 10` |
| `VectorCompactionService`                        | 1m  | 6h  | Low    | `sessions.aged ≥ 100` |

**Demo overrides.** `appsettings.json` in the Demo project drops Identity's policy to:

```json
"Calibration": {
  "Trigger": {
    "MinInterval": "00:00:01",
    "MaxInterval": "00:00:30",
    "MaxBand": "Critical",
    "AnyOfSignals": [
      { "Key": "obs.unabsorbed", "Threshold": 1 }
    ]
  }
}
```

Result: the demo calibrates within seconds of the first observation. Already-shipped `appsettings.json` change in this commit gets us close via `CalibrationIntervalMinutes: 1`, but the adaptive shape collapses the time-to-first-signal from minutes to seconds.

---

## 4. Observability — `/admin/learning/health`

The dashboard endpoint that converts this whole system from "decorative" to "auditable". Per service:

```json
{
  "calibration": {
    "lastRunUtc": "2026-06-21T13:15:00Z",
    "elapsedSec": 47,
    "lastDecision": "ShouldRun = true",
    "lastReason": "signal obs.unabsorbed=73 ≥ 50",
    "signals": { "obs.unabsorbed": 12, "drift.l2": 0.18 },
    "currentBand": "Low",
    "willFireWhen": "obs.unabsorbed ≥ 50 (currently 12) OR drift.l2 ≥ 0.5 (currently 0.18) OR safety net at 13:21:00Z"
  },
  "umbrellas": [
    { "id": "chrome-desktop",
      "descendantCount": 412,
      "p90DriftMatchingUa": 0.34,
      "bloat": 1.7,
      "leakingDescendantCount": 28,
      "lastShrunkAt": "2026-06-21T13:10:00Z",
      "currentMaskSum": 14.2 }
  ],
  "selfResolving": [
    { "id": "googlebot",      "selfResolves": true,  "topRivalId": "bingbot",        "topRivalScore": 0.81 },
    { "id": "chrome-privacy", "selfResolves": false, "topRivalId": "chrome-desktop", "topRivalScore": 0.94 }
  ]
}
```

`selfResolving` is exactly the property the new `Distinct_visitor_shapes_resolve_to_distinct_archetypes` test asserts at the catalogue level. Surfacing it per archetype gives operators a list to triage.

---

## 5. Migration

Three phases, each independently mergeable.

### Phase 1 — Adaptive trigger only

- Add `AdaptiveTriggerPolicy`, `TriggerContext`, `TriggerDecision`, `IAdaptiveTriggerSignalSource` to `Mostlylucid.BotDetection/Identity/Triggers/`.
- Convert `IdentityWeightCalibrationService.OnTickAsync` to evaluate the policy. Default policy mirrors `30 min` interval so prod semantics are unchanged.
- Wire `CalibrationSignalSource` to `SqliteFingerprintStore.RecordObservationAsync` and `FingerprintAbsorptionService.AbsorbAsync`. One `Interlocked.Increment` and one `Interlocked.Add`-equivalent. Zero hot-path cost.
- Demo `appsettings.json` gets the adaptive policy with low thresholds. Demos visibly learn.
- New cadence: `TickCadence.Tick5s` heartbeat.

**Test coverage:**
- Unit tests on `Evaluate` per gate.
- Integration test: signal counter increments → trigger fires → counter resets.
- Existing `CentroidLearningLoopTests` continues to drive `RunOnceAsync` directly, agnostic to trigger.

### Phase 2 — UA-anchored drift metrics + observability endpoint

- New table `archetype_drift_metrics`.
- Calibration pass populates it after centroid refinement.
- `/admin/learning/health` endpoint reads from it.
- No behavioural change — pure observability.

**Test coverage:**
- Feed observations with UA matching an archetype → assert `matches_asserted_ua = 1` rows accumulate.
- Feed observations with UA NOT matching → assert `matches_asserted_ua = 0` rows accumulate.
- Endpoint test: counts visible.

### Phase 3 — Umbrella shrinkage

- Shrinkage rules from §2.C applied during calibration.
- New per-archetype state: `MaskShrinkFactor` (multiplicative; persisted; bounded `[0.05, 1.0]`).
- New per-archetype state: `LastSplitCandidateAt` for the `bloat ≥ 2.0` signal.

**Test coverage:** the killer test —
- Seed `chrome-desktop` with the current over-broad mask.
- Feed 30 obs that should land on `chrome-privacy`, plus 30 obs that should land on `chrome-desktop`.
- Call `RunOnceAsync` 3 times (simulated calibration cycles).
- Assert `chrome-privacy.Centroid` now self-resolves via `FindNearest`.
- Assert `chrome-desktop.DimensionMask` sum decreased between cycle 1 and cycle 3.

That test is the end-to-end answer to "we're not convinced centroids are emerging and drifting". It demonstrates emergence (chrome-privacy becomes distinct), drift (centroid moves), and self-correction (umbrella narrows).

---

## 6. Non-goals

- **Replacing `ScheduleCoordinator`.** It stays. The trigger is a per-service layer on top.
- **Splitting umbrellas automatically.** v1 emits a `umbrella.split-candidate` signal but takes no action. Splitting requires new archetype synthesis (the existing arcjet/YAML/BotPatterns synthesizer is the source of truth for archetype creation) and is its own design.
- **New ML algorithms.** Fisher weights and maturity-weighted-mean stay as they are. This spec changes *when* and *how often* they run, and adds a per-archetype catchment-narrowing pass.
- **Cross-host learning.** Per-host isolation stays per the existing fingerprint scope.

---

## 7. Open questions

1. **Heartbeat cadence.** `Tick5s` for the trigger evaluator is cheap but is it cheap enough at 100K req/s? A 5-second tick that does a dictionary lookup and a few comparisons is ~negligible, but worth measuring before adoption.
2. **Bloat thresholds.** `1.0` to shrink, `2.0` to flag for split. These are guesses. The Phase 2 observability endpoint exists partly to gather real distributions before locking these in.
3. **Shrinkage rate.** `0.05` per cycle = ~3% per minute under the Demo's 1s tick. Reasonable for demos; for prod (30-min calibration) that's 0.1% per hour — likely too slow. Should rate be per-time rather than per-cycle? Lean yes.
4. **Recovery from over-shrink.** If a Chrome upgrade legitimately drifts the Chrome cluster, an aggressively-shrunk archetype could fail to capture it. Need a "regrowth" rule: if `matches_asserted_ua = 0` count drops to zero AND mean drift is low, ratchet mask back up slowly. Out of scope for v1; phase-3 logs the regrowth-candidate signal.
5. **Trigger eval logging cost.** The decision + reason string allocates per heartbeat. Bench it; if it shows up, pool the strings or skip the reason on `ShouldRun = false`.

---

## 8. The connection back to the test suite

The three tests landed in `CentroidLearningLoopTests` on 2026-06-21 are the load-bearing contract this spec serves:

- `Centroid_drifts_toward_observed_shape_under_repeated_observations` — the basic property. Spec doesn't change this.
- `Calibration_run_populates_archetypes_and_dimension_weights_durable_tier` — proves the loop runs. Adaptive trigger means this property holds *visibly soon* in demos, not just in tests.
- `Distinct_visitor_shapes_resolve_to_distinct_archetypes` — currently passes only on the weak property (≥2 self-resolving archetypes). Phase 3 strengthens it: ALL non-mode archetypes should self-resolve once umbrella shrinkage converges. The strengthened test is the regression guard for the chrome-privacy → chrome-desktop bleed.

When phase 3 lands, the strengthened test becomes the canonical "is the learning loop actually working" assertion, and the live demo's `/admin/learning/health` endpoint becomes the human-facing answer to "I can't see it learning".
