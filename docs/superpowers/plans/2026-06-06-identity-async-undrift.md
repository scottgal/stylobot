# Identity Async Un-Drift Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the identity subsystem's three time-driven services (`FingerprintAbsorptionService`, `IdentityGlobalWeightsCache`, `FingerprintDriftService`) from periodic polling to signal-driven coordination, using new foundation signals emitted by `FingerprintMatchContributor` and new internal events on `IFingerprintStore` and `IdentityWeightCalibrationService`. Closes the loop the verdict-cache un-drift plan started.

**Branch:** `feat/identity-async-undrift` off `origin/main`. Do not branch off `feat/observability-tasks-3-7`.

**Architecture:** The matcher and the calibration writer are the canonical *producers* of the events the absorbers and the cache need to react to. The blackboard already has `identity.ambiguity_persistence` for drift; we add three threshold signals (`identity.fingerprint_first_seen`, `identity.fingerprint_observation_count_crossed`, `identity.fingerprint_maturity_threshold`) emitted from `FingerprintMatchContributor` (foundation, runs unconditionally per `docs/architecture/signal-contracts.md`). On the store side we add two C# events (`ObservationAppended` on `IFingerprintStore`, `WeightsUpdated` on `IdentityWeightCalibrationService`) so async subscribers can wake without depending on the request-path signal stream. Each periodic loop stays as a safety-net backstop at a much longer cadence (≥ 5 min), running a single shared cursor where two services walked the table independently before.

**Tech stack:** .NET 10, existing `Mostlylucid.BotDetection.Identity` namespace, `BackgroundService`, `ConcurrentDictionary` for per-fp debounce, no new cache layer. SQLite remains the durable tier.

**Design constraints:**
- No new cache layer ([[feedback_no_unbacked_imemorycache]]).
- No in-memory persistence ([[feedback_no_inmemory_persistence]]). The fingerprint dict / SQLite remain authoritative.
- FOSS-additive: detection capability never degrades ([[feedback_foss_never_degraded]]).
- Foundation contributor changes go through the BDF rig ([`docs/architecture/signal-contracts.md`](../architecture/signal-contracts.md) Rule 4).
- One fact, one store ([`signal-contracts.md`](../architecture/signal-contracts.md) Rule 2). New signals do not duplicate state already in `fingerprints` columns; they announce row-state transitions.
- Verify behaviour with running tests before commit ([[feedback_verify_before_checkin]]).
- No em-dashes in code comments or docs ([[feedback_no_emdash]]).

---

## File Structure

```
src/Mostlylucid.BotDetection/Models/
  DetectionContext.cs                         # MODIFY: add 3 SignalKeys constants

src/Mostlylucid.BotDetection/Identity/
  IFingerprintStore.cs                        # MODIFY: add `event Action<string> ObservationAppended`
  SqliteFingerprintStore.cs                   # MODIFY: raise ObservationAppended after RecordObservationAsync commits
  IdentityWeightCalibrationService.cs         # MODIFY: add `event Action WeightsUpdated`, raise after a successful write
  IdentityGlobalWeightsCache.cs               # MODIFY: subscribe to WeightsUpdated; drop the 1s polling floor (keep startup prime)
  FingerprintAbsorptionService.cs             # MODIFY: subscribe to ObservationAppended; per-fp coalesce debounce; backstop loop at 5min
  FingerprintDriftService.cs                  # MODIFY: subscribe to global signal stream; react to identity.ambiguity_persistence + new threshold signals; backstop loop at 5min
  IdentityFingerprintScanner.cs               # NEW: shared single-cursor sweep over stale fingerprints, used by both backstop loops
  IdentityOptions.cs                          # MODIFY: rename Drift.DriftCheckIntervalSeconds (deprecate) and add BackstopSweepIntervalSeconds default 300

src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/
  FingerprintMatchContributor.cs              # MODIFY: emit FingerprintFirstSeen on new-fp allocate; emit FingerprintObservationCountCrossed when post-write count crosses configured thresholds; emit FingerprintMaturityThreshold on first crossing

src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/
  BdfReplayTests.Integration.cs               # MODIFY: probe + assert on FingerprintFirstSeen (Rule 4)

src/Mostlylucid.BotDetection.Test/Identity/
  FingerprintMatchContributorThresholdSignalsTests.cs   # NEW: pins the 3 new signals fire under DetectionPolicy.Default
  ObservationAppendedEventTests.cs                       # NEW: pins event raises after RecordObservationAsync
  IdentityGlobalWeightsCacheEventTests.cs                # NEW: pins cache refresh fires on WeightsUpdated, not on tick
  FingerprintAbsorptionServiceSubscribeTests.cs          # NEW: pins absorption fires on ObservationAppended with per-fp debounce
  FingerprintDriftServiceSignalSubscribeTests.cs         # NEW: pins drift L2 fires on ambiguity_persistence + new signals
  IdentityFingerprintScannerTests.cs                     # NEW: pins shared cursor surfaces both absorption and drift candidates in one pass

docs/architecture/
  signal-contracts.md                         # MODIFY: document the new signal taxonomy + the reference pattern (BotClusterService.ClustersUpdated → CentroidSequenceRebuildHostedService)
```

---

## Task 1: Add the three new SignalKeys constants + BDF probe assertion

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` (SignalKeys block ~line 565)
- Modify: `src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs` (`AssertSignalsFlowed` ~line 209)

- [ ] **Step 1: Add the constants to `SignalKeys`**

In `DetectionContext.cs`, just below the existing `IdentityAmbiguity*` block:

```csharp
/// <summary>
///     bool: true on the first request that allocates a brand-new fingerprint row
///     (no `fingerprint_keys` match). Written by FingerprintMatchContributor on the
///     allocate path. Async absorption / drift subscribers wake on this to warm
///     their per-fp state without polling the durable tier.
/// </summary>
public const string FingerprintFirstSeen = "identity.fingerprint_first_seen";

/// <summary>
///     int: the configured threshold the fingerprint's `observation_count` just
///     crossed on this request (one of IdentityOptions.Absorption.NotifyOnCountCrossings).
///     Written by FingerprintMatchContributor after RecordObservationAsync returns.
///     Wakes FingerprintAbsorptionService when a hot fingerprint accumulates enough
///     new observations to be worth folding into the centroid.
/// </summary>
public const string FingerprintObservationCountCrossed = "identity.fingerprint_observation_count_crossed";

/// <summary>
///     bool: true on the first request where the matched fingerprint's centroid
///     maturity has just crossed IdentityOptions.Absorption.MaturityThreshold.
///     Written by FingerprintMatchContributor. Wakes drift verification because a
///     matured fingerprint's centroid is now load-bearing for display / verdict reads.
/// </summary>
public const string FingerprintMaturityThreshold = "identity.fingerprint_maturity_threshold";
```

- [ ] **Step 2: Add the BDF probe assertion**

In `BdfReplayTests.Integration.cs`, append inside `AssertSignalsFlowed` (after the `IdentityArchetypeName` probe):

```csharp
// FingerprintFirstSeen: probed and asserted. A new-visitor scenario in the BDF rig
// must allocate a fingerprint exactly once and emit this signal; absorption /
// warmup subscribers wake on it.
Assert.True(probes.TryGetValue(SignalKeys.FingerprintFirstSeen, out var hasFirstSeen) && hasFirstSeen,
    $"{scenarioName}: {SignalKeys.FingerprintFirstSeen} missing from ev.Signals -- " +
    "FingerprintAbsorptionService and downstream warmup subscribers won't wake on new fingerprints");
```

Also add the key to whatever populates `BdfReplayActual.SignalProbes` (search the file for the existing `IdentityArchetypeName` probe registration and add `FingerprintFirstSeen` alongside it).

- [ ] **Step 3: Run the existing BDF rig to verify it fails red on the new assertion**

```bash
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests -c Release \
  --filter "FullyQualifiedName~BdfReplayTests" --no-restore
```

Expected: FAIL. The contributor doesn't emit the signal yet. The failure message names `identity.fingerprint_first_seen`.

- [ ] **Step 4: Commit the red probe**

```bash
git add src/Mostlylucid.BotDetection/Models/DetectionContext.cs \
        src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs
git commit -m "test(identity): pin BDF probe for FingerprintFirstSeen (red)"
```

---

## Task 2: Emit the three new threshold signals from `FingerprintMatchContributor`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs` (add `Absorption.NotifyOnCountCrossings`)
- New: `src/Mostlylucid.BotDetection.Test/Identity/FingerprintMatchContributorThresholdSignalsTests.cs`

- [ ] **Step 1: Add the absorption option**

In `IdentityOptions.cs`, on `AbsorptionOptions` (or the relevant nested type holding `AbsorptionMaturityThreshold`), add:

```csharp
/// <summary>
///     Observation-count thresholds the matcher announces via
///     `SignalKeys.FingerprintObservationCountCrossed`. Async absorption fires
///     on each crossing instead of polling on a fixed interval.
///     Default: 1, 3, 10, 30, 100. Empty list disables emission.
/// </summary>
public int[] NotifyOnCountCrossings { get; set; } = new[] { 1, 3, 10, 30, 100 };
```

- [ ] **Step 2: Write the failing test (pins the signal-emit contract)**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/FingerprintMatchContributorThresholdSignalsTests.cs
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Identity;
using Xunit;

public class FingerprintMatchContributorThresholdSignalsTests
{
    [Fact]
    public async Task NewFingerprint_EmitsFirstSeenSignal()
    {
        var rig = new IdentityTestRig();
        var state = await rig.RunRequestAsync(rig.FreshContext());
        Assert.True(state.Signals.ContainsKey(SignalKeys.FingerprintFirstSeen));
        Assert.True((bool)state.Signals[SignalKeys.FingerprintFirstSeen]);
    }

    [Fact]
    public async Task ObservationCount_EmitsCrossedSignal_OnConfiguredThresholds()
    {
        var rig = new IdentityTestRig(opts => opts.Absorption.NotifyOnCountCrossings = new[] { 3 });
        var ctx = rig.FreshContext();
        for (int i = 0; i < 2; i++) await rig.RunRequestAsync(ctx);
        var crossing = await rig.RunRequestAsync(ctx); // observation #3
        Assert.Equal(3, (int)crossing.Signals[SignalKeys.FingerprintObservationCountCrossed]);
    }

    [Fact]
    public async Task MaturityCrossing_EmitsMaturityThresholdSignal()
    {
        var rig = new IdentityTestRig(opts =>
        {
            opts.Absorption.MaturityThreshold = 5;
            opts.Absorption.NotifyOnCountCrossings = new[] { 5 };
        });
        var ctx = rig.FreshContext();
        for (int i = 0; i < 4; i++) await rig.RunRequestAsync(ctx);
        var matured = await rig.RunRequestAsync(ctx);
        Assert.True((bool)matured.Signals[SignalKeys.FingerprintMaturityThreshold]);
    }
}
```

(If `IdentityTestRig` doesn't exist, use the existing matcher-test harness in the same folder; see `FingerprintMatchContributorTests.cs` for the shape.)

- [ ] **Step 3: Run the tests and verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~FingerprintMatchContributorThresholdSignalsTests" --no-restore
```

Expected: FAIL on all three (signals not emitted).

- [ ] **Step 4: Implement the signal emissions in `FingerprintMatchContributor`**

In the allocate path (around line 492 in `FingerprintMatchContributor.cs`, where `IdentityIsNewFingerprint` is written):

```csharp
state.WriteSignal(SignalKeys.FingerprintFirstSeen, true);
```

After the successful `RecordObservationAsync` call on both allocate and match paths, emit the count-crossing signal:

```csharp
var newCount = await _store.GetObservationCountAsync(fingerprintId, ct);
var crossings = _options.Absorption.NotifyOnCountCrossings;
foreach (var threshold in crossings)
{
    if (newCount == threshold)
    {
        state.WriteSignal(SignalKeys.FingerprintObservationCountCrossed, threshold);
        break;
    }
}
```

On the match path, after fetching the matched fingerprint row, compare `matched.CentroidMaturity` against `_options.Absorption.MaturityThreshold`. If this is the first request where the row equals or exceeds the threshold (compare to a `wasMatureBefore` flag from the row, or use the difference between pre-fetched maturity and post-write maturity):

```csharp
if (postMaturity >= _options.Absorption.MaturityThreshold && preMaturity < _options.Absorption.MaturityThreshold)
    state.WriteSignal(SignalKeys.FingerprintMaturityThreshold, true);
```

If `IFingerprintStore` doesn't expose `GetObservationCountAsync`, add it (returns the `observation_count` column for the row by id).

- [ ] **Step 5: Run all three tests + BDF rig, verify green**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~FingerprintMatchContributorThresholdSignalsTests" --no-restore
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests -c Release \
  --filter "FullyQualifiedName~BdfReplayTests" --no-restore
```

Expected: both PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/FingerprintMatchContributor.cs \
        src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs \
        src/Mostlylucid.BotDetection/Identity/IFingerprintStore.cs \
        src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs \
        src/Mostlylucid.BotDetection.Test/Identity/FingerprintMatchContributorThresholdSignalsTests.cs
git commit -m "feat(identity): emit fingerprint first-seen / count-crossed / maturity-crossed signals"
```

---

## Task 3: Add `ObservationAppended` event on `IFingerprintStore`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IFingerprintStore.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs`
- New: `src/Mostlylucid.BotDetection.Test/Identity/ObservationAppendedEventTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/ObservationAppendedEventTests.cs
using System.Threading.Tasks;
using Xunit;
using Mostlylucid.BotDetection.Identity;

public class ObservationAppendedEventTests
{
    [Fact]
    public async Task RecordObservation_RaisesObservationAppendedWithFingerprintId()
    {
        var store = TestFingerprintStore.New();
        string? seen = null;
        store.ObservationAppended += fpId => seen = fpId;

        await store.RecordObservationAsync("fp-1", new float[129], default);

        Assert.Equal("fp-1", seen);
    }

    [Fact]
    public async Task ObservationAppended_FiresAfterDurableWriteCommits()
    {
        var store = TestFingerprintStore.New();
        var fired = new TaskCompletionSource<bool>();
        store.ObservationAppended += _ =>
        {
            // Inside the handler, the row count must already reflect the write.
            var count = store.GetObservationCountAsync("fp-1", default).GetAwaiter().GetResult();
            fired.SetResult(count == 1);
        };

        await store.RecordObservationAsync("fp-1", new float[129], default);

        Assert.True(await fired.Task);
    }
}
```

- [ ] **Step 2: Run, verify red**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~ObservationAppendedEventTests" --no-restore
```

Expected: FAIL (`IFingerprintStore` has no such event).

- [ ] **Step 3: Add the event to the interface and raise it from the store**

In `IFingerprintStore.cs`:

```csharp
/// <summary>
///     Raised after a successful RecordObservationAsync durable write. The string
///     is the fingerprint id whose observation_count just incremented. Subscribers
///     (FingerprintAbsorptionService) react to this to fold the observation into
///     the centroid without polling. Synchronous invocation on the call site's
///     thread; subscribers must not block.
/// </summary>
event Action<string>? ObservationAppended;
```

In `SqliteFingerprintStore.cs`, add a backing event and raise it at the end of `RecordObservationAsync` (after the `UPDATE ... observation_count = observation_count + 1` commits):

```csharp
public event Action<string>? ObservationAppended;

// ... inside RecordObservationAsync after transaction.CommitAsync():
ObservationAppended?.Invoke(fingerprintId);
```

- [ ] **Step 4: Run, verify green**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~ObservationAppendedEventTests" --no-restore
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IFingerprintStore.cs \
        src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs \
        src/Mostlylucid.BotDetection.Test/Identity/ObservationAppendedEventTests.cs
git commit -m "feat(identity): IFingerprintStore.ObservationAppended event"
```

---

## Task 4: Wire `FingerprintAbsorptionService` to `ObservationAppended` with per-fp debounce + 5-minute backstop

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/FingerprintAbsorptionService.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs` (add `Absorption.SubscriptionDebounceMs`, `Absorption.BackstopSweepIntervalSeconds`)
- New: `src/Mostlylucid.BotDetection.Test/Identity/FingerprintAbsorptionServiceSubscribeTests.cs`

- [ ] **Step 1: Add the options**

In `IdentityOptions.cs` on `AbsorptionOptions`:

```csharp
/// <summary>
///     Per-fingerprint debounce window for the ObservationAppended subscription.
///     A second event for the same fingerprint within this window collapses into
///     a single absorption run. Prevents storms when a hot visitor floods
///     observations faster than absorption can drain. Default 250ms.
/// </summary>
public int SubscriptionDebounceMs { get; set; } = 250;

/// <summary>
///     Safety-net backstop sweep cadence. Subscribes to ObservationAppended for the
///     hot path; this loop catches any fingerprints missed by the subscription
///     (event handler failure, crash recovery, late SQLite rows). Default 300s (5min).
///     Was 5s when the loop was the only mechanism.
/// </summary>
public int BackstopSweepIntervalSeconds { get; set; } = 300;
```

- [ ] **Step 2: Write the failing test**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/FingerprintAbsorptionServiceSubscribeTests.cs
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public class FingerprintAbsorptionServiceSubscribeTests
{
    [Fact]
    public async Task ObservationAppended_TriggersAbsorptionWithinDebounce()
    {
        var rig = new AbsorptionTestRig(opts =>
        {
            opts.SubscriptionDebounceMs = 50;
            opts.BackstopSweepIntervalSeconds = 600; // ensure subscription path is what fires
        });
        await rig.StartAsync();

        await rig.Store.RecordObservationAsync("fp-1", new float[129], default);

        // Wait debounce + buffer
        await Task.Delay(150);
        Assert.Equal(1, rig.AbsorptionRunsFor("fp-1"));
    }

    [Fact]
    public async Task RapidObservations_CollapseToSingleAbsorptionWithinDebounce()
    {
        var rig = new AbsorptionTestRig(opts => opts.SubscriptionDebounceMs = 200);
        await rig.StartAsync();

        for (int i = 0; i < 10; i++)
            await rig.Store.RecordObservationAsync("fp-1", new float[129], default);

        await Task.Delay(300);
        Assert.Equal(1, rig.AbsorptionRunsFor("fp-1"));
    }

    [Fact]
    public async Task BackstopSweep_RunsAtConfiguredCadence_NotAt5sFloor()
    {
        var rig = new AbsorptionTestRig(opts => opts.BackstopSweepIntervalSeconds = 1);
        await rig.StartAsync();
        await Task.Delay(2500);
        // Allowed: 2 sweeps in ~2.5s; not 500+ from a 5s tick on a wall-clock loop
        Assert.InRange(rig.BackstopSweepCount, 1, 4);
    }
}
```

- [ ] **Step 3: Run, verify red**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~FingerprintAbsorptionServiceSubscribeTests" --no-restore
```

Expected: FAIL on all three.

- [ ] **Step 4: Refactor `FingerprintAbsorptionService`**

In `FingerprintAbsorptionService.cs`:

- Inject `IFingerprintStore` (already injected); subscribe to `store.ObservationAppended` in `ExecuteAsync` before the backstop loop.
- Hold a `ConcurrentDictionary<string, DateTime> _pendingByFingerprint` for debounce.
- Per-event: stamp `_pendingByFingerprint[fpId] = now + debounce`; if not already running for this fp, schedule a `Task.Delay(debounce)` then run absorption for just that fingerprint (replace the existing batch loop's per-fp body with a `RunAbsorptionForAsync(fpId, ct)` method).
- Change the periodic `Task.Delay(tick)` to `Task.Delay(_options.Absorption.BackstopSweepIntervalSeconds * 1000, ct)`.
- The backstop pass continues to call the existing `ListStaleFingerprintsAsync` / `ListReadyForAbsorptionAsync` (whatever the current method is named) but now serves only crash-recovery + stragglers.

Pseudocode for the subscription handler:

```csharp
private void OnObservationAppended(string fingerprintId)
{
    var fire = DateTime.UtcNow.AddMilliseconds(_options.Absorption.SubscriptionDebounceMs);
    if (!_pendingByFingerprint.TryAdd(fingerprintId, fire))
    {
        _pendingByFingerprint[fingerprintId] = fire;
        return; // already scheduled
    }
    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(_options.Absorption.SubscriptionDebounceMs);
            _pendingByFingerprint.TryRemove(fingerprintId, out _);
            await RunAbsorptionForAsync(fingerprintId, _shutdown.Token);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { _logger.LogWarning(ex, "Absorption fired by event failed for {Id}", fingerprintId); }
    });
}
```

Unsubscribe on `StopAsync`.

- [ ] **Step 5: Run, verify green + run the existing BDF rig and identity integration tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~FingerprintAbsorptionServiceSubscribeTests" --no-restore
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests -c Release \
  --filter "FullyQualifiedName~BdfReplayTests" --no-restore
```

Expected: both PASS. No regression on BDF.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/FingerprintAbsorptionService.cs \
        src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs \
        src/Mostlylucid.BotDetection.Test/Identity/FingerprintAbsorptionServiceSubscribeTests.cs
git commit -m "feat(identity): absorb on ObservationAppended event; backstop drops to 5min"
```

---

## Task 5: Add `WeightsUpdated` event on `IdentityWeightCalibrationService`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityWeightCalibrationService.cs`
- New test extends `src/Mostlylucid.BotDetection.Test/Identity/IdentityGlobalWeightsCacheEventTests.cs` (created in Task 6)

- [ ] **Step 1: Add the event surface**

In `IdentityWeightCalibrationService.cs`:

```csharp
/// <summary>
///     Raised after a successful write to `identity_dimension_weights` or
///     `identity_archetypes`. Subscribers (IdentityGlobalWeightsCache) refresh
///     their composed-weights snapshot in response.
/// </summary>
public event Action? WeightsUpdated;
```

Raise after each successful SQLite commit inside the calibration loop:

```csharp
// after _store.SaveGlobalWeightsAsync(...) succeeds:
WeightsUpdated?.Invoke();
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/Mostlylucid.BotDetection -c Release --no-restore
```

Expected: SUCCEED. The cache test in Task 6 will cover the wiring.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityWeightCalibrationService.cs
git commit -m "feat(identity): WeightsUpdated event on calibration commit"
```

---

## Task 6: `IdentityGlobalWeightsCache` subscribes to `WeightsUpdated`; drop polling floor

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityGlobalWeightsCache.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs` (deprecate `Weights.GlobalRefreshSeconds` floor)
- New: `src/Mostlylucid.BotDetection.Test/Identity/IdentityGlobalWeightsCacheEventTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/IdentityGlobalWeightsCacheEventTests.cs
using System.Threading.Tasks;
using Xunit;

public class IdentityGlobalWeightsCacheEventTests
{
    [Fact]
    public async Task WeightsUpdated_RefreshesCacheImmediately()
    {
        var rig = new WeightsCacheTestRig();
        await rig.StartAsync();
        rig.Store.NextWeights = new float[129];   // queue a fresh value
        rig.Calibration.RaiseWeightsUpdated();    // simulate calibration commit
        await Task.Delay(50);                     // event handler runs async
        Assert.Same(rig.Store.NextWeights, rig.Cache.Current);
    }

    [Fact]
    public async Task NoEvent_DoesNotPollWithinFiveSeconds()
    {
        var rig = new WeightsCacheTestRig();
        await rig.StartAsync();
        var loadsAtStart = rig.Store.LoadCount;
        await Task.Delay(5_000);
        Assert.InRange(rig.Store.LoadCount - loadsAtStart, 0, 1); // startup prime allowed; no polling
    }
}
```

- [ ] **Step 2: Run, verify red**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~IdentityGlobalWeightsCacheEventTests" --no-restore
```

Expected: FAIL. Today's cache polls every 1s; the second test fails on poll count.

- [ ] **Step 3: Rewire the cache**

In `IdentityGlobalWeightsCache.cs`:

- Drop the `while (!ct.IsCancellationRequested) { ...; await Task.Delay(refresh, ct); }` loop.
- Keep the startup prime (`await RefreshAsync()` once in `ExecuteAsync`).
- Inject `IdentityWeightCalibrationService`; subscribe to `WeightsUpdated` and call `RefreshAsync` on the event.
- Unsubscribe on `StopAsync`.

Keep `Identity.Weights.GlobalRefreshSeconds` in the options class but mark it `[Obsolete("WeightsUpdated event drives refresh; this floor is unused.")]`. The dashboard / admin reload endpoint can still trigger a manual refresh.

- [ ] **Step 4: Run, verify green**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~IdentityGlobalWeightsCacheEventTests" --no-restore
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityGlobalWeightsCache.cs \
        src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs \
        src/Mostlylucid.BotDetection.Test/Identity/IdentityGlobalWeightsCacheEventTests.cs
git commit -m "feat(identity): weights cache refreshes on WeightsUpdated event, no polling"
```

---

## Task 7: `FingerprintDriftService` subscribes to ambiguity + threshold signals; backstop drops to 5 min

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/FingerprintDriftService.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs` (extend `DriftOptions` with `BackstopSweepIntervalSeconds`)
- New: `src/Mostlylucid.BotDetection.Test/Identity/FingerprintDriftServiceSignalSubscribeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/FingerprintDriftServiceSignalSubscribeTests.cs
using System.Threading.Tasks;
using Xunit;
using Mostlylucid.BotDetection.Models;

public class FingerprintDriftServiceSignalSubscribeTests
{
    [Fact]
    public async Task AmbiguityPersistenceSignal_TriggersImmediateL2Verify()
    {
        var rig = new DriftServiceTestRig();
        await rig.StartAsync();
        rig.MakeFingerprint("fp-1", maturity: 10, observation: new float[129]);

        rig.EmitSignal(SignalKeys.IdentityAmbiguityPersistence, 0.8, fingerprintId: "fp-1");
        await Task.Delay(100);

        Assert.Equal(1, rig.L2VerifyCountFor("fp-1"));
    }

    [Fact]
    public async Task NoSignal_BackstopRunsAtConfiguredCadenceNotAt5sFloor()
    {
        var rig = new DriftServiceTestRig(opts =>
        {
            opts.Drift.BackstopSweepIntervalSeconds = 1;
            opts.Drift.DriftCheckIntervalSeconds = 5; // deprecated; should be ignored
        });
        await rig.StartAsync();
        await Task.Delay(2500);
        Assert.InRange(rig.BackstopSweepCount, 1, 4); // ~2 expected; not 500
    }
}
```

- [ ] **Step 2: Run, verify red**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~FingerprintDriftServiceSignalSubscribeTests" --no-restore
```

- [ ] **Step 3: Rewire `FingerprintDriftService`**

- Inject `IDetectionOrchestrator` (or `IDetectionSignalBus` directly).
- In `ExecuteAsync`, subscribe via `orchestrator.SubscribeToSignals(OnSignal)` before entering the backstop loop. Filter for `SignalKeys.IdentityAmbiguityPersistence`, `IdentityAmbiguityProbing`, `FingerprintMaturityThreshold`, and `FingerprintObservationCountCrossed`.
- On signal arrival, resolve the fingerprint id from the same signal payload (`SignalKeys.IdentityFingerprintId` is always present per BDF Rule) and call the existing `VerifyOneAsync(fpId, ct)`.
- Change the backstop interval from `_options.Drift.DriftCheckIntervalSeconds` to `_options.Drift.BackstopSweepIntervalSeconds` (add the option, default 300s).
- Mark `DriftCheckIntervalSeconds` `[Obsolete("Backstop is now the only timer; see BackstopSweepIntervalSeconds.")]`.
- Dispose the subscription on `StopAsync`.

- [ ] **Step 4: Run, verify green + BDF rig**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~FingerprintDriftServiceSignalSubscribeTests" --no-restore
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests -c Release \
  --filter "FullyQualifiedName~BdfReplayTests" --no-restore
```

Expected: both PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/FingerprintDriftService.cs \
        src/Mostlylucid.BotDetection/Identity/IdentityOptions.cs \
        src/Mostlylucid.BotDetection.Test/Identity/FingerprintDriftServiceSignalSubscribeTests.cs
git commit -m "feat(identity): drift verifier wakes on ambiguity + threshold signals; 5min backstop"
```

---

## Task 8: Extract shared `IdentityFingerprintScanner` for the two backstop sweeps

**Files:**
- New: `src/Mostlylucid.BotDetection/Identity/IdentityFingerprintScanner.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/FingerprintAbsorptionService.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/FingerprintDriftService.cs`
- New: `src/Mostlylucid.BotDetection.Test/Identity/IdentityFingerprintScannerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/IdentityFingerprintScannerTests.cs
using System.Linq;
using System.Threading.Tasks;
using Xunit;

public class IdentityFingerprintScannerTests
{
    [Fact]
    public async Task Sweep_SurfacesBothAbsorptionAndDriftCandidatesInSingleQuery()
    {
        var rig = new ScannerTestRig();
        rig.SeedFingerprints(
            absorptionReady: new[] { "fp-a1", "fp-a2" },
            driftStale: new[] { "fp-d1", "fp-d2", "fp-a1" }); // overlap intentional

        var pass = await rig.Scanner.SweepAsync(default);

        Assert.Equal(new[] { "fp-a1", "fp-a2" }, pass.AbsorptionCandidates.Select(c => c.FingerprintId).OrderBy(s => s));
        Assert.Equal(new[] { "fp-a1", "fp-d1", "fp-d2" }, pass.DriftCandidates.Select(c => c.FingerprintId).OrderBy(s => s));
        Assert.Equal(1, rig.Store.QueriesIssued); // one cursor, two effects
    }
}
```

- [ ] **Step 2: Run, verify red**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~IdentityFingerprintScannerTests" --no-restore
```

- [ ] **Step 3: Implement the scanner**

`IdentityFingerprintScanner.cs`:

```csharp
public sealed class IdentityFingerprintScanner
{
    private readonly IFingerprintStore _store;
    private readonly IdentityOptions _options;

    public IdentityFingerprintScanner(IFingerprintStore store, IOptions<BotDetectionOptions> options)
    {
        _store = store;
        _options = options.Value.Identity;
    }

    public async Task<ScanPass> SweepAsync(CancellationToken ct)
    {
        var rows = await _store.ListStaleOrPendingFingerprintsAsync(
            _options.Drift.CachedScoreTtlSeconds,
            batchSize: Math.Max(_options.Drift.DriftBatchSize, 50),
            ct);

        var absorption = rows.Where(r => r.PendingObservations > 0).ToList();
        var drift = rows.Where(r => r.CachedScoreCheckedAt is null
                                   || r.CachedScoreCheckedAt < DateTime.UtcNow.AddSeconds(-_options.Drift.CachedScoreTtlSeconds))
                        .ToList();
        return new ScanPass(absorption, drift);
    }

    public sealed record ScanPass(IReadOnlyList<FingerprintScanRow> AbsorptionCandidates,
                                  IReadOnlyList<FingerprintScanRow> DriftCandidates);
}
```

Add a new method `IFingerprintStore.ListStaleOrPendingFingerprintsAsync` that returns rows with either pending observations or a stale score timestamp in one SQL query (replaces the existing two separate list methods, which become wrappers or get deleted).

- [ ] **Step 4: Point both backstop loops at the scanner**

In `FingerprintAbsorptionService.cs`, in the backstop body:

```csharp
var pass = await _scanner.SweepAsync(ct);
foreach (var fp in pass.AbsorptionCandidates)
    await RunAbsorptionForAsync(fp.FingerprintId, ct);
```

In `FingerprintDriftService.cs`, in the backstop body:

```csharp
var pass = await _scanner.SweepAsync(ct);
foreach (var fp in pass.DriftCandidates)
    await TickOneAsync(fp, ct); // existing per-fp drift verify
```

Register the scanner in DI (`ServiceCollectionExtensions.cs` under the identity wiring): `services.AddSingleton<IdentityFingerprintScanner>();`.

- [ ] **Step 5: Run all identity tests + BDF rig**

```bash
dotnet test src/Mostlylucid.BotDetection.Test -c Release \
  --filter "FullyQualifiedName~Identity|FullyQualifiedName~Fingerprint" --no-restore
dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests -c Release \
  --filter "FullyQualifiedName~BdfReplayTests" --no-restore
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityFingerprintScanner.cs \
        src/Mostlylucid.BotDetection/Identity/FingerprintAbsorptionService.cs \
        src/Mostlylucid.BotDetection/Identity/FingerprintDriftService.cs \
        src/Mostlylucid.BotDetection/Identity/IFingerprintStore.cs \
        src/Mostlylucid.BotDetection/Identity/SqliteFingerprintStore.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Test/Identity/IdentityFingerprintScannerTests.cs
git commit -m "refactor(identity): shared IdentityFingerprintScanner for backstop sweeps"
```

---

## Task 9: Document the new signal taxonomy + reference pattern in `signal-contracts.md`

**Files:**
- Modify: `docs/architecture/signal-contracts.md`

- [ ] **Step 1: Add a "Reference signal-driven coordination patterns" section**

At the bottom of `signal-contracts.md`, before any "References" section:

```markdown
## Reference signal-driven coordination

Two extant patterns illustrate the rules above:

1. **Foundation contributor emits, async service subscribes.** `FingerprintMatchContributor`
   emits `identity.fingerprint_first_seen`, `identity.fingerprint_observation_count_crossed`,
   and `identity.fingerprint_maturity_threshold` from the request path (foundation,
   runs unconditionally). `FingerprintAbsorptionService` and `FingerprintDriftService`
   subscribe to these signals via `IDetectionOrchestrator.SubscribeToSignals(...)`
   and wake on the relevant fingerprint without polling the durable tier.
   Per-fingerprint debounce collapses storms during hot-visitor bursts.

2. **Internal C# event on a service, in-process subscriber.** `BotClusterService.ClustersUpdated`
   is raised when Leiden clustering produces a fresh snapshot; `CentroidSequenceRebuildHostedService`
   subscribes and rebuilds the centroid sequence store. Same shape: one event,
   one subscriber, no time-driven polling between them. `IFingerprintStore.ObservationAppended`
   and `IdentityWeightCalibrationService.WeightsUpdated` follow this pattern.

For new async coordination, prefer the foundation-signal path when the trigger
is a request-derived fact (matcher, transport, signature). Use C# events when
the trigger is an internal background-write commit not visible to the orchestrator.
```

- [ ] **Step 2: Verify markdown renders + commit**

```bash
git add docs/architecture/signal-contracts.md
git commit -m "docs(architecture): reference signal-driven coordination patterns"
```

---

## Task 10: End-to-end verification (run the demo at moderate load, observe absorption latency)

**Files:** None.

This task is not a code change; it is the running-app verification step required by [[feedback_verify_before_checkin]] before merging to main.

- [ ] **Step 1: Run the demo with Identity enabled**

```bash
ASPNETCORE_ENVIRONMENT=Development \
BotDetection__Identity__Enabled=true \
dotnet run --project src/Mostlylucid.BotDetection.Demo
```

Wait for `Application started`.

- [ ] **Step 2: Drive ~100 RPS of repeated same-visitor traffic for 30 seconds**

Use the existing soak harness or hey:

```bash
hey -z 30s -c 10 -H "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36" \
  https://localhost:5001/SignatureDemo
```

- [ ] **Step 3: Tail the logs while running and confirm**

- `FingerprintAbsorptionService` log lines reference observation-appended subscriptions (NOT periodic ticks).
- `Drift tick` warning lines appear no more than once every ~5 minutes.
- p50 absorption latency from first observation to centroid write is under 1 second (was up to 5s before).

If the log shape doesn't show event-driven traces, add a one-line `LogDebug` in the absorption subscriber and re-run.

- [ ] **Step 4: Stop the demo. Note observations in the commit message of any follow-up bugfix.**

No commit on this task unless a fix lands.

---

## Self-review

**Spec coverage:**
- Review §5 recommendation 1 (absorb on observation-append) → Tasks 3 + 4
- Review §5 recommendation 2 (weights cache on event) → Tasks 5 + 6
- Review §5 recommendation 5 (foundation threshold signals + drift subscribes) → Tasks 1 + 2 + 7
- Review §5 recommendation 7 (consolidate scans) → Task 8
- Review §5 recommendation 9 (document the reference pattern) → Task 9
- Review §5 recommendations 3, 4, 6 (SessionFinalized fan-out): out of scope, separate plan
- Review §5 recommendation 8 (RemoteMetricCollector idle-skip): out of scope, separate plan

**Placeholder scan:** Searched for "TBD", "handle edge cases", "similar to Task N". None present.

**Type consistency:**
- `ObservationAppended` is `event Action<string>?` everywhere it appears (Tasks 3, 4, 8).
- `WeightsUpdated` is `event Action?` everywhere (Tasks 5, 6).
- `IdentityFingerprintScanner.ScanPass` record carries `IReadOnlyList<FingerprintScanRow>` for both lists (Task 8).
- `NotifyOnCountCrossings` is `int[]` on `AbsorptionOptions` (Tasks 2, 4).
- `BackstopSweepIntervalSeconds` lives on both `AbsorptionOptions` (Task 4) and `DriftOptions` (Task 7) with default 300 in both.
