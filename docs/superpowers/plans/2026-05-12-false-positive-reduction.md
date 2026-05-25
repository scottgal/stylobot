# False Positive Reduction in Content Sequence Detection

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut false-positive divergence triggers from `ContentSequenceContributor` by weighting unexpected requests according to their semantic importance, fixing the critical-phase API-call trap, computing the centroid from real session data instead of a template, and replacing the hardcoded `GlobalChain` with a site-learned baseline that suppresses divergence during warmup.

**Architecture:**
- Replace the flat `UnexpectedStateScore` constant with a per-`RequestState` weight map loaded from YAML. Static assets (CSS/JS/images) weigh near-zero; auth/notfound/search weigh high.
- Let `cacheWarm` flip on inside the critical window (0 to 500ms) when a `Cookie` header is present, since a returning visitor's browser legitimately skips static-asset re-fetches.
- Raise `HighRequestCountThreshold` from 50 to 200 and reset the count when the request gap exceeds a configurable idle window.
- Replace the template-based centroid build with a real aggregation: pull each cluster's recent sessions from `SqliteSessionStore`, parse `paths_json` into `RequestState[]`, and compute the modal state at each position with a minimum-support cutoff.
- Add a `LearnedGlobalChain` mode: until the system has ingested N confirmed-human sessions site-wide, suppress divergence scoring entirely (write `sequence.centroid_stale = true`). Once warmup completes, the learned global chain replaces the hardcoded template.

**Tech Stack:** .NET 10, xUnit, SQLite (Microsoft.Data.Sqlite), YAML (YamlDotNet), existing detector pipeline.

---

## File Structure

**New files:**
- `src/Mostlylucid.BotDetection/Services/StateDivergenceWeights.cs` (loads per-state weights from YAML defaults)
- `src/Mostlylucid.BotDetection/Services/SessionChainAggregator.cs` (computes modal `RequestState[]` chain from cluster sessions)
- `src/Mostlylucid.BotDetection.Test/Services/StateDivergenceWeightsTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/SessionChainAggregatorTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/CentroidSequenceStoreTests.cs` (only if not present, otherwise extend)

**Modified files:**
- `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/contentsequence.detector.yaml` (add per-state weights, idle window, learned-global thresholds, raise threshold)
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs` (use new weights, cookie-aware cache-warm, idle reset, learned-global guard)
- `src/Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs` (compute real chains via `SessionChainAggregator`, add learned global baseline state, persist learned global)
- `src/Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs` (pass session store to rebuild, learn global on first rebuild)
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` (register new services)
- `src/Mostlylucid.BotDetection/docs/centroid-freshness.md` (document new tuning)
- `src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs` (update assertions for new thresholds)

---

## Task 1: Per-state divergence weights (YAML + loader)

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/contentsequence.detector.yaml`
- Create: `src/Mostlylucid.BotDetection/Services/StateDivergenceWeights.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/StateDivergenceWeightsTests.cs`

- [ ] **Step 1.1: Extend YAML with per-state weights**

In `contentsequence.detector.yaml`, replace the existing `parameters:` block with:

```yaml
  parameters:
    divergence_threshold: 0.6
    timing_tolerance_multiplier: 3.0
    min_centroid_sample_size: 20
    session_gap_minutes: 30
    max_tracked_positions: 20
    machine_speed_threshold_ms: 20.0
    machine_speed_score: 0.3
    high_request_count_score: 0.2
    high_request_count_threshold: 200
    request_count_idle_reset_seconds: 60

    # Per-RequestState divergence weights. Flat 0.5 was a major false-positive source.
    # Static fetches are noise (browser side-effect); auth/notfound/search are highly meaningful.
    unexpected_weight_static_asset: 0.05
    unexpected_weight_page_view: 0.10
    unexpected_weight_api_call: 0.25
    unexpected_weight_signalr: 0.20
    unexpected_weight_websocket: 0.20
    unexpected_weight_server_sent_event: 0.20
    unexpected_weight_form_submit: 0.40
    unexpected_weight_auth_attempt: 0.60
    unexpected_weight_not_found: 0.50
    unexpected_weight_search: 0.40

    # Learned-global warmup: suppress divergence until this many confirmed-human sessions exist.
    learned_global_min_sessions: 50
```

- [ ] **Step 1.2: Write the failing test for `StateDivergenceWeights`**

Create `src/Mostlylucid.BotDetection.Test/Services/StateDivergenceWeightsTests.cs`:

```csharp
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class StateDivergenceWeightsTests
{
    [Fact]
    public void Default_StaticAsset_IsLowWeight()
    {
        var w = StateDivergenceWeights.Default;
        Assert.True(w.For(RequestState.StaticAsset) <= 0.1,
            "StaticAsset should be near-zero - it's browser noise");
    }

    [Fact]
    public void Default_AuthAttempt_IsHighWeight()
    {
        var w = StateDivergenceWeights.Default;
        Assert.True(w.For(RequestState.AuthAttempt) >= 0.5,
            "AuthAttempt should be high - it is a meaningful divergence");
    }

    [Fact]
    public void Default_NotFound_IsHighWeight()
    {
        var w = StateDivergenceWeights.Default;
        Assert.True(w.For(RequestState.NotFound) >= 0.4);
    }

    [Fact]
    public void FromParameters_OverridesDefaults()
    {
        var w = StateDivergenceWeights.FromParameters(
            (state, fallback) => state == RequestState.StaticAsset ? 0.99 : fallback);
        Assert.Equal(0.99, w.For(RequestState.StaticAsset));
        Assert.Equal(StateDivergenceWeights.Default.For(RequestState.ApiCall),
                     w.For(RequestState.ApiCall));
    }
}
```

- [ ] **Step 1.3: Run the test - expect compilation failure (type does not exist)**

Run:
```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~StateDivergenceWeightsTests"
```
Expected: build error `CS0246: The type or namespace name 'StateDivergenceWeights' could not be found`.

- [ ] **Step 1.4: Implement `StateDivergenceWeights`**

Create `src/Mostlylucid.BotDetection/Services/StateDivergenceWeights.cs`:

```csharp
using System.Collections.Frozen;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Per-<see cref="RequestState"/> weights for divergence scoring.
///     Static assets are noise (browser side-effect): near-zero weight.
///     Auth / NotFound / Search are highly meaningful: high weight.
///     Loaded from YAML defaults via <see cref="FromParameters"/>.
/// </summary>
public sealed class StateDivergenceWeights
{
    public static readonly StateDivergenceWeights Default = new(new Dictionary<RequestState, double>
    {
        [RequestState.StaticAsset] = 0.05,
        [RequestState.PageView] = 0.10,
        [RequestState.ApiCall] = 0.25,
        [RequestState.SignalR] = 0.20,
        [RequestState.WebSocket] = 0.20,
        [RequestState.ServerSentEvent] = 0.20,
        [RequestState.FormSubmit] = 0.40,
        [RequestState.AuthAttempt] = 0.60,
        [RequestState.NotFound] = 0.50,
        [RequestState.Search] = 0.40,
    }.ToFrozenDictionary());

    private readonly FrozenDictionary<RequestState, double> _weights;

    private StateDivergenceWeights(FrozenDictionary<RequestState, double> weights)
        => _weights = weights;

    public double For(RequestState state)
        => _weights.TryGetValue(state, out var w) ? w : 0.25;

    /// <summary>
    ///     Build a weight set from a resolver callback. Resolver receives the state and a default fallback
    ///     (the value from <see cref="Default"/>) and returns the configured value.
    /// </summary>
    public static StateDivergenceWeights FromParameters(Func<RequestState, double, double> resolve)
    {
        var dict = new Dictionary<RequestState, double>(Default._weights.Count);
        foreach (var state in Enum.GetValues<RequestState>())
            dict[state] = resolve(state, Default.For(state));
        return new StateDivergenceWeights(dict.ToFrozenDictionary());
    }
}
```

- [ ] **Step 1.5: Run the test - expect pass**

Run:
```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~StateDivergenceWeightsTests"
```
Expected: 4 passed.

- [ ] **Step 1.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/StateDivergenceWeights.cs \
        src/Mostlylucid.BotDetection.Test/Services/StateDivergenceWeightsTests.cs \
        src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/contentsequence.detector.yaml
git commit -m "$(cat <<'EOF'
feat(sequence): per-state divergence weights replace flat unexpected-state score

Static-asset divergences were weighted the same as auth/notfound, producing
false positives on routine browser fetches. New StateDivergenceWeights maps
each RequestState to a calibrated weight, loaded from YAML.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Apply per-state weights in `ContentSequenceContributor`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs:91-95, 342-371`
- Modify: `src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs`

- [ ] **Step 2.1: Write the failing test for weighted scoring**

Append to `ContentSequenceContributorTests.cs` (inside the class, in an appropriate region):

```csharp
[Fact]
public async Task UnexpectedStaticAsset_DoesNotTripDivergence()
{
    const string sig = "weighted-static";
    SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddSeconds(-1));

    var contributor = CreateContributor();
    var state = CreateState(sig, configureHttp: ctx =>
    {
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/extra.css";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "style";
    });

    await contributor.ContributeAsync(state, CancellationToken.None);

    var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
    var score = state.GetSignal<double>(SignalKeys.SequenceDivergenceScore);
    Assert.False(diverged, $"StaticAsset alone must not trip divergence (score={score:F2})");
}

[Fact]
public async Task UnexpectedAuthAttempt_TripsDivergence()
{
    const string sig = "weighted-auth";
    SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddSeconds(-1));

    var contributor = CreateContributor();
    var state = CreateState(sig, configureHttp: ctx =>
    {
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/login";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "empty";
        ctx.Request.Headers["Content-Type"] = "application/x-www-form-urlencoded";
    });

    await contributor.ContributeAsync(state, CancellationToken.None);

    var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
    Assert.True(diverged, "AuthAttempt should trip divergence on a fresh session at position 1");
}
```

- [ ] **Step 2.2: Run tests - expect failures (current flat score still trips on static)**

Run:
```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~ContentSequenceContributorTests.UnexpectedStaticAsset"
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~ContentSequenceContributorTests.UnexpectedAuthAttempt"
```
Expected: `UnexpectedStaticAsset_DoesNotTripDivergence` FAILS (today the flat 0.5 trips threshold 0.4). `UnexpectedAuthAttempt_TripsDivergence` may pass with the old scoring, but should also pass after refactor.

- [ ] **Step 2.3: Refactor `ContentSequenceContributor` to use weights and raise threshold**

In `ContentSequenceContributor.cs`, remove the line `private double UnexpectedStateScore => GetParam("unexpected_state_score", 0.5);` (around line 93) and add the weights field plus a loader. The full set of edits:

1. Add field after `_clusterService`:

```csharp
    private StateDivergenceWeights? _weights;
```

2. Add a lazy weight loader (place near other private members):

```csharp
    private StateDivergenceWeights GetWeights() =>
        _weights ??= StateDivergenceWeights.FromParameters((state, fallback) =>
            GetParam(YamlKeyFor(state), fallback));

    private static string YamlKeyFor(RequestState state) => state switch
    {
        RequestState.StaticAsset => "unexpected_weight_static_asset",
        RequestState.PageView => "unexpected_weight_page_view",
        RequestState.ApiCall => "unexpected_weight_api_call",
        RequestState.SignalR => "unexpected_weight_signalr",
        RequestState.WebSocket => "unexpected_weight_websocket",
        RequestState.ServerSentEvent => "unexpected_weight_server_sent_event",
        RequestState.FormSubmit => "unexpected_weight_form_submit",
        RequestState.AuthAttempt => "unexpected_weight_auth_attempt",
        RequestState.NotFound => "unexpected_weight_not_found",
        RequestState.Search => "unexpected_weight_search",
        _ => "unexpected_weight_api_call"
    };
```

3. Replace the body of `ComputeDivergenceScore` with:

```csharp
    private double ComputeDivergenceScore(
        RequestState requestState,
        double elapsedMs,
        RequestState[] expectedSet,
        SequenceContext ctx,
        bool cacheWarm)
    {
        double score = 0.0;
        var weights = GetWeights();

        var msSinceLastRequest = (DateTimeOffset.UtcNow - ctx.LastRequest).TotalMilliseconds;
        if (msSinceLastRequest < MachineSpeedThresholdMs)
            score += MachineSpeedScore;

        var isExpected = expectedSet.Contains(requestState);
        if (!isExpected)
        {
            var isCacheWarmException = cacheWarm && requestState == RequestState.ApiCall;
            if (!isCacheWarmException)
                score += weights.For(requestState);
        }

        if (ctx.RequestCountInWindow > HighRequestCountThreshold)
            score += HighRequestCountScore;

        return Math.Min(score, 1.0);
    }
```

4. Update default for `DivergenceThreshold` accessor to 0.6:

```csharp
    private double DivergenceThreshold => GetParam("divergence_threshold", 0.6);
```

- [ ] **Step 2.4: Update existing tests that asserted the old threshold/score behaviour**

Inspect `ContentSequenceContributorTests.cs` for tests that depend on the old defaults (`unexpected_state_score`, `divergence_threshold: 0.4`). Any test that passed `unexpected_state_score` via the `configParams` map should switch to passing the relevant `unexpected_weight_*` keys, and any test that relied on `divergence_threshold = 0.4` should either pass `divergence_threshold: 0.4` explicitly or use values consistent with the new 0.6 default. Read the file, find each occurrence, and update so the test's intent stays the same. Show the diff before committing.

- [ ] **Step 2.5: Run the new tests - expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~ContentSequenceContributorTests"
```
Expected: all passing.

- [ ] **Step 2.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs \
        src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs
git commit -m "$(cat <<'EOF'
feat(sequence): weight divergence by RequestState; raise threshold to 0.6

ContentSequenceContributor now scores unexpected requests using
StateDivergenceWeights instead of a flat score. Threshold raised from 0.4
to 0.6 so a single static-asset miss can no longer trip divergence.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Cookie-aware cache-warm in critical window

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs:244-246`
- Modify: `src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs`

- [ ] **Step 3.1: Write the failing test**

Add to `ContentSequenceContributorTests.cs`:

```csharp
[Fact]
public async Task CriticalWindow_ApiCall_WithCookie_CacheWarmFlipsImmediately()
{
    const string sig = "returning-visitor";
    SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddSeconds(-1));

    var contributor = CreateContributor();
    var state = CreateState(sig, configureHttp: ctx =>
    {
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/me";
        ctx.Request.Headers["Cookie"] = "session=abc";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "empty";
        ctx.Request.Headers["Accept"] = "application/json";
    });

    await contributor.ContributeAsync(state, CancellationToken.None);

    var cacheWarm = state.GetSignal<bool>(SignalKeys.SequenceCacheWarm);
    Assert.True(cacheWarm, "Returning visitor (Cookie present) should flip cache_warm in critical window");
    var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
    Assert.False(diverged, "ApiCall in critical window with Cookie + cache_warm must not diverge");
}
```

- [ ] **Step 3.2: Run the test - expect fail (today cacheWarm only flips at phaseIndex > 0)**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~CriticalWindow_ApiCall_WithCookie"
```
Expected: FAIL (cacheWarm false in critical window).

- [ ] **Step 3.3: Implement cookie-aware cache-warm**

In `ContentSequenceContributor.HandleContinuationRequest`, replace the existing cache-warm block:

```csharp
        // Cache warm detection:
        //  - critical window closed with no StaticAsset observed (returning visitor whose browser skipped statics), OR
        //  - returning visitor signalled by a Cookie header and the first continuation is not a StaticAsset
        var cacheWarm = ctx.CacheWarm;
        var hasCookie = request.Headers.ContainsKey("Cookie");
        if (!cacheWarm)
        {
            if (phaseIndex > 0 && !observedSet.Contains(RequestState.StaticAsset))
                cacheWarm = true;
            else if (hasCookie && requestState != RequestState.StaticAsset)
                cacheWarm = true;
        }
```

- [ ] **Step 3.4: Run the test - expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~CriticalWindow_ApiCall_WithCookie"
```
Expected: PASS.

- [ ] **Step 3.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs \
        src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs
git commit -m "$(cat <<'EOF'
fix(sequence): flip cache_warm immediately for returning visitors

Critical-window cache_warm only fired at phase boundary, so the first XHR
of a returning visitor was scored against an empty cache assumption. A
Cookie header is a strong signal of a returning visitor whose browser has
warm assets; flip cache_warm on the first non-static continuation.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Bump high-request-count threshold and idle reset

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/SequenceContextStore.cs` (add `IdleResetSeconds` plumbing if needed) - no, see below: the reset lives in the contributor.
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs:230-289`
- Modify: `src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs`

- [ ] **Step 4.1: Write the failing test**

```csharp
[Fact]
public async Task RequestCount_ResetsAfterIdleGap()
{
    const string sig = "idle-reset";
    SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddSeconds(-5));
    var ctxBefore = _contextStore.TryGet(sig)!;
    _contextStore.Update(sig, ctxBefore with
    {
        RequestCountInWindow = 199,
        LastRequest = DateTimeOffset.UtcNow.AddMinutes(-2)
    });

    // idle gap (2 min) > idle reset (60 sec) → window should reset on next request
    var contributor = CreateContributor(new Dictionary<string, object>
    {
        ["request_count_idle_reset_seconds"] = 60
    });
    var state = CreateState(sig, configureHttp: ctx =>
    {
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/widget.js";
        ctx.Request.Headers["Sec-Fetch-Dest"] = "script";
    });

    await contributor.ContributeAsync(state, CancellationToken.None);

    var ctxAfter = _contextStore.TryGet(sig)!;
    Assert.Equal(1, ctxAfter.RequestCountInWindow);
}
```

- [ ] **Step 4.2: Run the test - expect fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~RequestCount_ResetsAfterIdleGap"
```
Expected: FAIL (RequestCount currently increments to 200).

- [ ] **Step 4.3: Implement idle reset**

In `ContentSequenceContributor`:

1. Add the param accessor (place with the others):

```csharp
    private int RequestCountIdleResetSeconds => GetParam("request_count_idle_reset_seconds", 60);
```

2. In `HandleContinuationRequest`, replace the `RequestCountInWindow = ctx.RequestCountInWindow + 1` line with idle-aware logic. Replace the `updatedCtx` block as:

```csharp
        var idleSeconds = (now - ctx.LastRequest).TotalSeconds;
        var resetWindow = idleSeconds >= RequestCountIdleResetSeconds;
        var newRequestCount = resetWindow ? 1 : ctx.RequestCountInWindow + 1;
        var newWindowStart = resetWindow ? now : ctx.WindowStartTime;

        var updatedCtx = ctx with
        {
            Position = position,
            ObservedStateSet = resetWindow ? ImmutableHashSet<RequestState>.Empty.Add(requestState) : observedSet,
            WindowStartTime = newWindowStart,
            RequestCountInWindow = newRequestCount,
            LastRequest = now,
            HasDiverged = hasDiverged,
            DivergenceCount = divergenceCount,
            CacheWarm = resetWindow ? false : cacheWarm
        };
```

- [ ] **Step 4.4: Run all sequence tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~ContentSequenceContributorTests"
```
Expected: all pass. The new test passes; raising the default to 200 (already done in Task 1) is what wires it together.

- [ ] **Step 4.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs \
        src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs
git commit -m "$(cat <<'EOF'
fix(sequence): reset request-count window after idle gap

Heavy SPAs (dashboards with polling/widgets) cross 50 requests in normal
use; the old threshold lit divergence permanently. Threshold raised to 200
and the window now resets after 60s of inactivity, so a long-lived session
no longer accumulates a permanent bot signal.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Compute centroid chains from real session data

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/SessionChainAggregator.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/SessionChainAggregatorTests.cs`
- Modify: `src/Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs:137-163`
- Modify: `src/Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs`

- [ ] **Step 5.1: Write the failing test for `SessionChainAggregator`**

Create `src/Mostlylucid.BotDetection.Test/Services/SessionChainAggregatorTests.cs`:

```csharp
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class SessionChainAggregatorTests
{
    [Fact]
    public void Empty_ReturnsEmptyChain()
    {
        var chain = SessionChainAggregator.Aggregate(
            sessions: Array.Empty<RequestState[]>(),
            chainLength: 5,
            minSupportFraction: 0.5);
        Assert.Empty(chain);
    }

    [Fact]
    public void Modal_PicksMostFrequentStatePerPosition()
    {
        // 3 sessions, all start with PageView then StaticAsset.
        var sessions = new[]
        {
            new[] { RequestState.PageView, RequestState.StaticAsset, RequestState.ApiCall },
            new[] { RequestState.PageView, RequestState.StaticAsset, RequestState.ApiCall },
            new[] { RequestState.PageView, RequestState.StaticAsset, RequestState.SignalR },
        };

        var chain = SessionChainAggregator.Aggregate(sessions, chainLength: 3, minSupportFraction: 0.5);

        Assert.Equal(3, chain.Length);
        Assert.Equal(RequestState.PageView, chain[0]);
        Assert.Equal(RequestState.StaticAsset, chain[1]);
        Assert.Equal(RequestState.ApiCall, chain[2]); // 2 of 3 - meets 0.5 support
    }

    [Fact]
    public void BelowMinSupport_TruncatesChain()
    {
        // No state appears at position 2 in a majority - chain stops at 2 positions.
        var sessions = new[]
        {
            new[] { RequestState.PageView, RequestState.ApiCall, RequestState.NotFound },
            new[] { RequestState.PageView, RequestState.ApiCall, RequestState.Search },
            new[] { RequestState.PageView, RequestState.ApiCall, RequestState.AuthAttempt },
        };

        var chain = SessionChainAggregator.Aggregate(sessions, chainLength: 3, minSupportFraction: 0.5);

        Assert.Equal(2, chain.Length);
        Assert.Equal(RequestState.PageView, chain[0]);
        Assert.Equal(RequestState.ApiCall, chain[1]);
    }

    [Fact]
    public void ParsePathsJson_PathsToRequestStates()
    {
        // paths_json shape is a JSON array of objects with state strings; verify the parser.
        var json = """
        [
          {"state":"PageView","path":"/"},
          {"state":"StaticAsset","path":"/site.css"},
          {"state":"ApiCall","path":"/api/data"}
        ]
        """;

        var states = SessionChainAggregator.ParsePathsJson(json);

        Assert.Equal(3, states.Length);
        Assert.Equal(RequestState.PageView, states[0]);
        Assert.Equal(RequestState.StaticAsset, states[1]);
        Assert.Equal(RequestState.ApiCall, states[2]);
    }

    [Fact]
    public void ParsePathsJson_UnknownState_IsSkipped()
    {
        var json = """[{"state":"WeirdValue","path":"/"},{"state":"PageView","path":"/"}]""";
        var states = SessionChainAggregator.ParsePathsJson(json);
        Assert.Single(states);
        Assert.Equal(RequestState.PageView, states[0]);
    }
}
```

- [ ] **Step 5.2: Run the test - expect compile failure**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SessionChainAggregatorTests"
```
Expected: build error.

- [ ] **Step 5.3: Verify the actual paths_json shape**

Before writing the parser, confirm the live JSON schema:

```bash
grep -n "paths_json\|PathsJson" src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs | head -20
```

Open `SqliteSessionStore.cs` around the persistence path and the `PersistedSession` record (look for `paths_json` references). Read enough to know whether the JSON is `[{"state":"...","path":"..."}]` or just `["...","..."]` or some other shape. **Update the parser in step 5.4 to match the real shape**, and update the parser test in 5.1 if the shape is different. Do not commit a parser that does not match the producer.

- [ ] **Step 5.4: Implement `SessionChainAggregator`**

Create `src/Mostlylucid.BotDetection/Services/SessionChainAggregator.cs`:

```csharp
using System.Text.Json;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Aggregates per-cluster session paths into a modal <see cref="RequestState"/> chain.
///     For each position 0..chainLength-1, picks the most frequent state across the input
///     sessions, gated by a minimum-support fraction so noisy positions truncate the chain.
/// </summary>
public static class SessionChainAggregator
{
    public static RequestState[] Aggregate(
        IReadOnlyList<RequestState[]> sessions,
        int chainLength,
        double minSupportFraction)
    {
        if (sessions.Count == 0 || chainLength <= 0)
            return Array.Empty<RequestState>();

        var result = new List<RequestState>(chainLength);
        for (var pos = 0; pos < chainLength; pos++)
        {
            var counts = new Dictionary<RequestState, int>();
            var total = 0;
            foreach (var session in sessions)
            {
                if (session.Length <= pos)
                    continue;
                var state = session[pos];
                counts[state] = counts.TryGetValue(state, out var c) ? c + 1 : 1;
                total++;
            }

            if (total == 0)
                break;

            var (modeState, modeCount) = counts.MaxBy(kv => kv.Value);
            var support = (double)modeCount / total;
            if (support < minSupportFraction)
                break;

            result.Add(modeState);
        }

        return result.ToArray();
    }

    /// <summary>
    ///     Parses a SqliteSessionStore <c>paths_json</c> payload into a <see cref="RequestState"/> array,
    ///     skipping entries whose <c>state</c> field does not parse to a known enum value.
    /// </summary>
    public static RequestState[] ParsePathsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<RequestState>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<RequestState>();

            var states = new List<RequestState>(doc.RootElement.GetArrayLength());
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                string? stateStr = element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : element.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString()
                        : null;
                if (stateStr is null) continue;
                if (Enum.TryParse<RequestState>(stateStr, ignoreCase: true, out var state))
                    states.Add(state);
            }
            return states.ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<RequestState>();
        }
    }
}
```

- [ ] **Step 5.5: Run the tests - expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SessionChainAggregatorTests"
```
Expected: 5 passed (or 4 if the shape adjustment in 5.3 eliminated one). Investigate any failure before continuing.

- [ ] **Step 5.6: Wire `SessionChainAggregator` into `CentroidSequenceStore.RebuildAsync`**

In `CentroidSequenceStore.cs`:

1. Change the constructor to accept an optional session-store loader. Add a delegate field:

```csharp
    public delegate Task<List<RequestState[]>> ClusterSessionLoader(
        IReadOnlyList<string> memberSignatures, int perSignature, CancellationToken ct);

    private readonly ClusterSessionLoader? _sessionLoader;

    public CentroidSequenceStore(
        string connectionString,
        ILogger<CentroidSequenceStore> logger,
        ClusterSessionLoader? sessionLoader = null)
    {
        _connectionString = connectionString;
        _logger = logger;
        _sessionLoader = sessionLoader;
    }
```

2. Replace the body of `RebuildAsync` so it calls the aggregator when a loader is available, falling back to the template otherwise:

```csharp
    public async Task RebuildAsync(IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
    {
        var newChains = new Dictionary<string, CentroidSequence>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var type = DetermineClusterType(cluster);
            var sampleSize = cluster.MemberCount;

            RequestState[] states;
            if (_sessionLoader != null)
            {
                var perSig = Math.Max(1, 200 / Math.Max(1, cluster.MemberSignatures.Count));
                var observed = await _sessionLoader(cluster.MemberSignatures, perSig, ct);
                var modal = SessionChainAggregator.Aggregate(observed, chainLength: 5, minSupportFraction: 0.5);
                states = modal.Length > 0 ? modal : DefaultChainFor(type);
            }
            else
            {
                states = DefaultChainFor(type);
            }

            var (gaps, tolerances) = type == CentroidType.Bot
                ? (DefaultBotGapsMs, DefaultBotTolerancesMs)
                : (DefaultHumanGapsMs, DefaultHumanTolerancesMs);

            newChains[cluster.ClusterId] = new CentroidSequence
            {
                CentroidId = cluster.ClusterId,
                Type = type,
                ExpectedStates = states,
                TypicalGapsMs = gaps,
                GapToleranceMs = tolerances,
                SampleSize = sampleSize
            };
        }

        _centroidChains = newChains.ToFrozenDictionary();
        await PersistAsync(newChains.Values, ct);
        _logger.LogDebug("CentroidSequenceStore rebuilt with {Count} clusters", newChains.Count);
    }

    private static RequestState[] DefaultChainFor(CentroidType type) =>
        type == CentroidType.Bot ? TypicalBotChain : TypicalHumanChain;
```

- [ ] **Step 5.7: Wire the session loader in `CentroidSequenceRebuildHostedService`**

In `CentroidSequenceRebuildHostedService.cs`, inject `SqliteSessionStore` (or `ISessionStore`) and pass a loader closure during construction. Update the DI registration site in `ServiceCollectionExtensions.cs` to pass the loader to the `CentroidSequenceStore`. The loader fetches up to `perSignature` recent sessions per member, parses `paths_json` via `SessionChainAggregator.ParsePathsJson`, and returns the result. Implementation sketch:

```csharp
// In CentroidSequenceRebuildHostedService (or wherever CentroidSequenceStore is constructed)
async Task<List<RequestState[]>> LoadSessions(IReadOnlyList<string> signatures, int perSig, CancellationToken ct)
{
    var result = new List<RequestState[]>();
    foreach (var sig in signatures)
    {
        var sessions = await _sessionStore.GetSessionsAsync(sig, perSig, ct);
        foreach (var s in sessions)
            if (!string.IsNullOrEmpty(s.PathsJson))
                result.Add(SessionChainAggregator.ParsePathsJson(s.PathsJson));
    }
    return result;
}
```

Pass that closure to the `CentroidSequenceStore` constructor at registration time.

- [ ] **Step 5.8: Write an integration-style test for `RebuildAsync` with a stub loader**

Add to `src/Mostlylucid.BotDetection.Test/Services/CentroidSequenceStoreTests.cs` (create the file if missing):

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class CentroidSequenceStoreTests
{
    [Fact]
    public async Task RebuildAsync_UsesLoaderResult_WhenLoaderReturnsData()
    {
        var loader = new CentroidSequenceStore.ClusterSessionLoader((sigs, perSig, ct) =>
            Task.FromResult(new List<RequestState[]>
            {
                new[] { RequestState.PageView, RequestState.ApiCall, RequestState.ApiCall, RequestState.SignalR, RequestState.ApiCall },
                new[] { RequestState.PageView, RequestState.ApiCall, RequestState.ApiCall, RequestState.SignalR, RequestState.ApiCall },
            }));

        var store = new CentroidSequenceStore(
            "Data Source=:memory:",
            NullLogger<CentroidSequenceStore>.Instance,
            loader);
        await store.InitializeAsync();

        var cluster = new BotCluster
        {
            ClusterId = "cluster-1",
            Type = BotClusterType.HumanTraffic,
            MemberSignatures = new List<string> { "sig-a", "sig-b" },
            MemberCount = 25
        };

        await store.RebuildAsync(new[] { cluster });
        var chain = store.TryGetCentroidChain("cluster-1", minSampleSize: 20);
        Assert.NotNull(chain);
        Assert.Equal(RequestState.PageView, chain!.ExpectedStates[0]);
        Assert.Equal(RequestState.ApiCall, chain.ExpectedStates[1]);
        Assert.Equal(RequestState.SignalR, chain.ExpectedStates[3]);
    }

    [Fact]
    public async Task RebuildAsync_FallsBackToTemplate_WhenLoaderEmpty()
    {
        var loader = new CentroidSequenceStore.ClusterSessionLoader((_, _, _) =>
            Task.FromResult(new List<RequestState[]>()));
        var store = new CentroidSequenceStore(
            "Data Source=:memory:",
            NullLogger<CentroidSequenceStore>.Instance,
            loader);
        await store.InitializeAsync();

        var cluster = new BotCluster
        {
            ClusterId = "cluster-empty",
            Type = BotClusterType.HumanTraffic,
            MemberSignatures = new List<string> { "sig-x" },
            MemberCount = 25
        };

        await store.RebuildAsync(new[] { cluster });
        var chain = store.TryGetCentroidChain("cluster-empty", minSampleSize: 20);
        Assert.NotNull(chain);
        Assert.Equal(RequestState.StaticAsset, chain!.ExpectedStates[0]); // template fallback
    }
}
```

- [ ] **Step 5.9: Run tests - expect pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~CentroidSequenceStoreTests"
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~SessionChainAggregatorTests"
```
Expected: all pass.

- [ ] **Step 5.10: Verify build of whole solution**

```bash
dotnet build mostlylucid.stylobot.sln
```
Expected: 0 errors. Fix any DI registration breakage in `ServiceCollectionExtensions.cs`.

- [ ] **Step 5.11: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/SessionChainAggregator.cs \
        src/Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs \
        src/Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection.Test/Services/SessionChainAggregatorTests.cs \
        src/Mostlylucid.BotDetection.Test/Services/CentroidSequenceStoreTests.cs
git commit -m "$(cat <<'EOF'
feat(centroid): compute cluster chains from real sessions, not templates

CentroidSequenceStore.RebuildAsync now aggregates each cluster's recent
session paths into a modal chain using SessionChainAggregator. Falls back
to the previous template only when no session data is available. The
hostedservice wires SqliteSessionStore as the loader; tests cover both
paths and the JSON shape.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Learned global baseline (replace hardcoded `GlobalChain`)

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs`
- Modify: `src/Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs:163-217`
- Modify: `src/Mostlylucid.BotDetection.Test/Services/CentroidSequenceStoreTests.cs`

- [ ] **Step 6.1: Write the failing test**

Add to `CentroidSequenceStoreTests.cs`:

```csharp
[Fact]
public async Task LearnedGlobal_BelowMinSessions_ReportsWarmingUp()
{
    var store = new CentroidSequenceStore(
        "Data Source=:memory:",
        NullLogger<CentroidSequenceStore>.Instance);
    await store.InitializeAsync();
    Assert.False(store.IsGlobalReady, "fresh store with no sessions must not be ready");
}

[Fact]
public async Task LearnedGlobal_AboveMinSessions_BecomesReady()
{
    var humanSessions = Enumerable.Repeat(
        new[] { RequestState.PageView, RequestState.StaticAsset, RequestState.StaticAsset, RequestState.ApiCall, RequestState.SignalR },
        60).ToList();
    var loader = new CentroidSequenceStore.ClusterSessionLoader(
        (_, _, _) => Task.FromResult(humanSessions));

    var store = new CentroidSequenceStore(
        "Data Source=:memory:",
        NullLogger<CentroidSequenceStore>.Instance,
        loader);
    await store.InitializeAsync();

    await store.RelearnGlobalAsync(minSessions: 50);
    Assert.True(store.IsGlobalReady);
    Assert.Equal(RequestState.PageView, store.GlobalChain.ExpectedStates[0]);
}
```

- [ ] **Step 6.2: Run the test - expect compile failure**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LearnedGlobal"
```
Expected: build error (`IsGlobalReady`, `RelearnGlobalAsync` not defined).

- [ ] **Step 6.3: Add learned-global state to `CentroidSequenceStore`**

In `CentroidSequenceStore.cs`:

1. Add the readiness flag and getter:

```csharp
    public bool IsGlobalReady { get; private set; }
```

2. Add `RelearnGlobalAsync`:

```csharp
    /// <summary>
    ///     Build a site-wide baseline chain by sampling all confirmed-human sessions via the loader.
    ///     If fewer than <paramref name="minSessions"/> sessions are available, leaves
    ///     <see cref="IsGlobalReady"/> false so callers suppress divergence scoring.
    /// </summary>
    public async Task RelearnGlobalAsync(int minSessions, CancellationToken ct = default)
    {
        if (_sessionLoader == null) return;

        // Pull a broad sample - signatures list is "any", the loader returns recent sessions across the store.
        var sessions = await _sessionLoader(Array.Empty<string>(), minSessions * 2, ct);
        if (sessions.Count < minSessions)
        {
            IsGlobalReady = false;
            return;
        }

        var chain = SessionChainAggregator.Aggregate(sessions, chainLength: 5, minSupportFraction: 0.5);
        if (chain.Length == 0)
        {
            IsGlobalReady = false;
            return;
        }

        _globalChain = new CentroidSequence
        {
            CentroidId = "global",
            Type = CentroidType.Unknown,
            ExpectedStates = chain,
            TypicalGapsMs = DefaultHumanGapsMs,
            GapToleranceMs = DefaultHumanTolerancesMs,
            SampleSize = sessions.Count
        };
        IsGlobalReady = true;
        await PersistAsync(new[] { _globalChain }, ct);
    }
```

3. Update `LoadFromDatabaseAsync` so a persisted `"global"` row restores `_globalChain` and sets `IsGlobalReady = true`.

- [ ] **Step 6.4: Update the loader to support "any signatures" sampling**

The current loader signature takes a `memberSignatures` list. For learned-global, we need an "all" mode. Update the loader closure in `CentroidSequenceRebuildHostedService.cs` so that when called with an empty list it returns a broad cross-cluster sample. Implementation:

```csharp
async Task<List<RequestState[]>> LoadSessions(IReadOnlyList<string> signatures, int perSig, CancellationToken ct)
{
    var result = new List<RequestState[]>();
    if (signatures.Count == 0)
    {
        // Global-baseline sampling - take recent sessions across all signatures, prefer human-classified.
        var recent = await _sessionStore.GetRecentSessionsAsync(limit: perSig, isBot: false, ct: ct);
        foreach (var s in recent)
            if (!string.IsNullOrEmpty(s.PathsJson))
                result.Add(SessionChainAggregator.ParsePathsJson(s.PathsJson));
        return result;
    }

    foreach (var sig in signatures)
    {
        var sessions = await _sessionStore.GetSessionsAsync(sig, perSig, ct);
        foreach (var s in sessions)
            if (!string.IsNullOrEmpty(s.PathsJson))
                result.Add(SessionChainAggregator.ParsePathsJson(s.PathsJson));
    }
    return result;
}
```

- [ ] **Step 6.5: Trigger `RelearnGlobalAsync` on startup and after each cluster update**

In `CentroidSequenceRebuildHostedService.StartAsync`, after `InitializeAsync`, kick a background relearn:

```csharp
_ = Task.Run(async () =>
{
    try
    {
        await _centroidStore.RelearnGlobalAsync(minSessions: 50, CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Initial learned-global relearn failed; falling back to suppression");
    }
});
```

And in `OnClustersUpdated`, after `RebuildAsync` completes, call `RelearnGlobalAsync` again so the learned global re-converges as the human cluster grows. Make `minSessions` configurable via `BotDetectionOptions` if needed; otherwise the constant 50 here is acceptable (the YAML default sits with the contributor).

- [ ] **Step 6.6: Update `ContentSequenceContributor` to suppress divergence when global is warming up**

In `ContentSequenceContributor.HandleDocumentRequest`, when the resolved chain is the global fallback AND `_centroidStore.IsGlobalReady` is false, treat the centroid as stale and skip divergence scoring. Modify the `ResolveChain` call to return the readiness flag, and pipe it into the document-write block:

```csharp
        var (chain, centroidId, isLearned) = ResolveChain(signature);
        // ...
        var centroidStale = _centroidStore.IsEndpointStale(contentPath) || !isLearned;
        // ... existing WriteSignals uses centroidStale (already does)
```

And `ResolveChain`:

```csharp
    private (CentroidSequence chain, string centroidId, bool isLearned) ResolveChain(string signature)
    {
        if (_clusterService != null)
        {
            var cluster = _clusterService.FindCluster(signature);
            if (cluster != null)
            {
                var centroidChain = _centroidStore.TryGetCentroidChain(
                    cluster.ClusterId, MinCentroidSampleSize);
                if (centroidChain != null)
                    return (centroidChain, centroidChain.CentroidId, true);
            }
        }

        return (_centroidStore.GlobalChain, "global", _centroidStore.IsGlobalReady);
    }
```

In `HandleContinuationRequest`, gate the `ComputeDivergenceScore` call: if `ctx.CentroidType == CentroidType.Unknown` and the chain is the global one and the store reports `IsGlobalReady == false`, skip scoring entirely (score = 0). The simplest implementation reads the readiness flag from the store each request; pass it in via a small helper:

```csharp
        // Skip scoring during global warmup - prevents day-1 false positives.
        var scoringAllowed = ctx.CentroidType != CentroidType.Unknown || _centroidStore.IsGlobalReady;
        double divergenceScore = 0.0;
        if (!isPrefetch && scoringAllowed)
            divergenceScore = ComputeDivergenceScore(requestState, elapsedMs, expectedSet, ctx, cacheWarm);
```

- [ ] **Step 6.7: Run all sequence and centroid tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~CentroidSequenceStoreTests|FullyQualifiedName~ContentSequenceContributorTests|FullyQualifiedName~SessionChainAggregatorTests"
```
Expected: all pass. Update any test that asserted the old hardcoded global chain.

- [ ] **Step 6.8: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/CentroidSequenceStore.cs \
        src/Mostlylucid.BotDetection/Services/CentroidSequenceRebuildHostedService.cs \
        src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/ContentSequenceContributor.cs \
        src/Mostlylucid.BotDetection.Test/Services/CentroidSequenceStoreTests.cs \
        src/Mostlylucid.BotDetection.Test/Orchestration/ContentSequenceContributorTests.cs
git commit -m "$(cat <<'EOF'
feat(centroid): site-learned global baseline replaces hardcoded chain

CentroidSequenceStore learns the global chain from confirmed-human sessions
on startup and after each cluster update. During warmup (under 50 human
sessions), the contributor suppresses divergence scoring entirely so fresh
deployments cannot produce false positives against an assumed template.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Documentation update

**Files:**
- Modify: `src/Mostlylucid.BotDetection/docs/centroid-freshness.md`
- Modify: `src/Mostlylucid.BotDetection/docs/content-sequence-detection.md`

- [ ] **Step 7.1: Add new section to `centroid-freshness.md`**

Append a new section "Learned global baseline + per-state weights" that explains:
1. The hardcoded global template is gone; the global chain is learned from confirmed-human sessions site-wide.
2. The contributor suppresses divergence scoring entirely until `learned_global_min_sessions` is reached.
3. Per-state weights replace the flat unexpected-state score. Tuning guidance: lower static-asset weight further if seeing legitimate JS/CSS divergences; raise auth/notfound weight if missing scanner activity.
4. `request_count_idle_reset_seconds` controls when the request-count window resets.

Use the existing tone of the doc (no em dashes; colons/semicolons/parentheses). Approximately 60-90 lines.

- [ ] **Step 7.2: Update `content-sequence-detection.md` divergence-scoring section**

Find the "Divergence scoring" subsection (around the "unexpected-state contribution" paragraph) and rewrite to reflect:
- Per-state weights replace the flat 0.5.
- Threshold raised from 0.4 to 0.6.
- Cookie-aware cache-warm in critical window.
- Idle reset on the request-count window.

Approximately 30-50 lines of changes.

- [ ] **Step 7.3: Commit**

```bash
git add src/Mostlylucid.BotDetection/docs/centroid-freshness.md \
        src/Mostlylucid.BotDetection/docs/content-sequence-detection.md
git commit -m "$(cat <<'EOF'
docs(sequence): document per-state weights and learned global baseline

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Verification (full test run + live smoke)

**Files:** none (verification only)

- [ ] **Step 8.1: Run full test suite**

```bash
dotnet test mostlylucid.stylobot.sln
```
Expected: 0 failures. If anything regressed, return to Phase 1 of systematic-debugging and root-cause before patching.

- [ ] **Step 8.2: Build and run the Demo, exercise the dashboard**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo
```
In another terminal or browser:
1. Open `https://localhost:5001/SignatureDemo` in a fresh incognito profile (cold cache, no cookies).
2. Click through several pages with the dev tools network panel open.
3. Open `http://localhost:5080/_stylobot` and confirm no false-positive bot flagging for the navigating browser.
4. Repeat with the same browser (warm cookies) and confirm cache_warm now flips immediately (no divergence on the first XHR).
5. Open the Sessions tab, drill into your own session, and confirm divergence_score values are low (under 0.4) for normal use.

If divergence is still tripping on the navigating browser, dump signals via the dashboard's signature detail view and adjust the YAML weights or the idle-reset window. Do not commit hardcoded site exceptions; the YAML is the only adjustment surface.

- [ ] **Step 8.3: Final commit if any tuning adjustments**

If Step 8.2 requires YAML adjustments, commit them as a follow-up `tune(sequence): ...` commit. Do not push.

---

## Notes for the executor

- Per project rules (memory: `feedback_no_emdash`): never use em dashes anywhere - in code comments, in docs, in commit messages, in the YAML. Use colons, semicolons, or parentheses.
- Per project rules (memory: `feedback_verify_before_checkin`): run the affected test slice before each commit; run the full solution build before Task 5 and Task 6 commits.
- Per project rules (memory: `feedback_never_push_without_approval`): never `git push`. Commits stay local until the user instructs otherwise.
- Per project rules (memory: `feedback_no_minimal_demo`): use `Mostlylucid.BotDetection.Demo` for live verification, never any `MinimalDemo` project.
- Per project rules (`CLAUDE.md`): never add hardcoded site-specific exceptions, bypass keys, or allowlists. All tuning happens through YAML parameters.
- The `IsGlobalReady` initial state on cold startup with no persisted "global" row must be `false`. Verify this in Task 6.