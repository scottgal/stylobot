# Adaptive Load-Shed Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace global-latency-baseline load shed (cause of the 2026-06-25 staging 503s) with a per-endpoint perf centroid model plus visitor-class-aware shed decisions that never shed verified humans by default.

**Architecture:** A new `IEndpointPerfBaseline` reads per-(method, normalized-template) p95 from the existing `IDashboardEventStore`, refreshed on `ScheduleCoordinator.Tick1m`. `PipelineLoadSensor` switches its upstream axis from absolute ms to a dimensionless deviation ratio (`actual / expected`). `LoadShedDecision` switches from a single-fraction-per-band to per-visitor-class fractions resolved against per-policy `ClassGate` thresholds; humans never shed by default, bots always shed when the band escalates.

**Tech Stack:** .NET 10, xUnit, `Microsoft.Extensions.DependencyInjection`, the existing `IScheduleCoordinator` cadence pattern. All FOSS code lives in `src/Mostlylucid.BotDetection`.

## Global Constraints

- **Spec source:** `docs/superpowers/specs/2026-06-25-adaptive-load-shed-redesign.md` (commit `a60c3f4a`). Every requirement in the spec must be implemented; deviations require updating the spec first.
- **All settings configurable:** every threshold, fraction, sample count, and interval lives on an Options class. No magic numbers in implementation files.
- **No parasitic store:** `DashboardEventStoreBackedEndpointPerfBaseline` exposes `IEndpointPerfBaseline` only. No `GetTemplateStats` / `GetAllTemplates` / convenience read methods. Class is `internal` in `Mostlylucid.BotDetection`.
- **Single consumer:** only the middleware OnCompleted hook reads from `IEndpointPerfBaseline`. Dashboard rendering, policy decisions, ops surfaces all keep reading raw-path stats from `IDashboardEventStore`.
- **Optional DI:** website / dashboard-only hosts that have no `IDashboardEventStore` must continue to boot. The middleware gracefully degrades to ratio 1.0 when `IEndpointPerfBaseline` is absent.
- **No BackgroundService:** refresh runs on `IScheduleCoordinator.Tick1m`. Subscribe with `CostHint.Low`.
- **Commit on `main`:** do NOT create a feature branch. The user's standing rule overrides any skill-suggested branch flow.
- **No em-dash or `--` in copy:** code comments, XML doc, commit messages all use plain punctuation. Both are AI tells.
- **Gate boundaries are INCLUSIVE on both sides:** `prob <= MaxBotProb` (human side), `prob >= MinBotProb` (bot side), `conf >= MinConfidence` (both sides). A verdict exactly at the boundary qualifies.
- **Sample-count threshold is strict less-than:** `samples < MinSamplesForTrustedBaseline` returns 0 (treated as no baseline).
- **Out of scope for this plan (per spec):** band-transition signals, pushing template aggregation into the store itself, dashboard surface for shed state, cross-host coordination.

---

## File structure

**Create:**
- `src/Mostlylucid.BotDetection/Services/VisitorClass.cs` — enum + `ClassGate` record + `ClassGateResolver` static helper
- `src/Mostlylucid.BotDetection/Services/IEndpointPerfBaseline.cs` — interface + `NullEndpointPerfBaseline`
- `src/Mostlylucid.BotDetection/Services/DashboardEventStoreBackedEndpointPerfBaseline.cs` — concrete impl
- `src/Mostlylucid.BotDetection.Test/Services/VisitorClassTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/ClassGateResolverTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/EndpointPerfBaselineTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/StagingMixedWorkloadShedTests.cs`
- `src/Mostlylucid.BotDetection.Test/Services/HumansNeverShedUnderCriticalTests.cs`
- `src/Mostlylucid.BotDetection.Test/Policies/LoadShedOptionsDefaultsTests.cs`

**Modify:**
- `src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs` — add `HumanGate`, `BotGate`, six per-class shed fractions
- `src/Mostlylucid.BotDetection/Services/PipelineLoadSensorOptions.cs` — add `MinSamplesForTrustedBaseline`, `BaselineRefreshInterval`
- `src/Mostlylucid.BotDetection/Services/PipelineLoadSensor.cs` — replace `RecordUpstreamRtt(ms)` with `RecordUpstreamDeviation(ratio)`; remove rolling-window upstream baseline state
- `src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs` — new `ShouldShed(VisitorClass, LoadShedOptions, int)` signature; remove `ShedHint` enum
- `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` — replace `ResolveShedHint` with `ClassGateResolver` call; switch OnCompleted to `RecordUpstreamDeviation` with template-keyed baseline lookup
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` — DI registration for `IEndpointPerfBaseline` (DashboardEventStore-backed when store is registered, Null otherwise)
- `src/Mostlylucid.BotDetection.Test/Services/PipelineLoadSensorTests.cs` — drop `Baseline_RecoversFromAnomalouslyFastWarmupSample`; add deviation-axis tests

---

## Task 1: VisitorClass + ClassGate + resolver

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/VisitorClass.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/VisitorClassTests.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/ClassGateResolverTests.cs`

**Interfaces:**
- Consumes: nothing (foundation task)
- Produces: `enum VisitorClass { Human, Unknown, Bot }`, `record ClassGate(double MaxBotProb, double MinBotProb, double MinConfidence)`, `static class ClassGateResolver { static VisitorClass Resolve(double? prob, double? conf, ClassGate humanGate, ClassGate botGate) }`

- [ ] **Step 1: Write the failing tests**

`src/Mostlylucid.BotDetection.Test/Services/VisitorClassTests.cs`:

```csharp
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class VisitorClassTests
{
    [Fact]
    public void VisitorClass_has_three_values_in_order_human_unknown_bot()
    {
        Assert.Equal(0, (int)VisitorClass.Human);
        Assert.Equal(1, (int)VisitorClass.Unknown);
        Assert.Equal(2, (int)VisitorClass.Bot);
    }

    [Fact]
    public void ClassGate_default_ctor_args_match_unconstrained_neutral()
    {
        var gate = new ClassGate();
        Assert.Equal(1.0, gate.MaxBotProb);
        Assert.Equal(0.0, gate.MinBotProb);
        Assert.Equal(0.0, gate.MinConfidence);
    }
}
```

`src/Mostlylucid.BotDetection.Test/Services/ClassGateResolverTests.cs`:

```csharp
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class ClassGateResolverTests
{
    private static readonly ClassGate HumanGate = new(MaxBotProb: 0.3, MinConfidence: 0.7);
    private static readonly ClassGate BotGate = new(MinBotProb: 0.5, MinConfidence: 0.7);

    [Fact]
    public void Null_prob_returns_unknown()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(null, 0.9, HumanGate, BotGate));
    }

    [Fact]
    public void Null_conf_returns_unknown()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.2, null, HumanGate, BotGate));
    }

    [Fact]
    public void Low_prob_high_conf_returns_human()
    {
        Assert.Equal(VisitorClass.Human, ClassGateResolver.Resolve(0.2, 0.9, HumanGate, BotGate));
    }

    [Fact]
    public void High_prob_high_conf_returns_bot()
    {
        Assert.Equal(VisitorClass.Bot, ClassGateResolver.Resolve(0.8, 0.9, HumanGate, BotGate));
    }

    [Fact]
    public void Low_prob_low_conf_returns_unknown_because_human_gate_requires_conf()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.1, 0.3, HumanGate, BotGate));
    }

    [Fact]
    public void High_prob_low_conf_returns_unknown_because_bot_gate_requires_conf()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.9, 0.3, HumanGate, BotGate));
    }

    [Fact]
    public void Borderline_prob_returns_unknown()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.4, 0.9, HumanGate, BotGate));
    }

    [Theory]
    [InlineData(0.3, 0.7, VisitorClass.Human)]
    [InlineData(0.5, 0.7, VisitorClass.Bot)]
    public void Boundary_values_qualify_inclusively(double prob, double conf, VisitorClass expected)
    {
        Assert.Equal(expected, ClassGateResolver.Resolve(prob, conf, HumanGate, BotGate));
    }

    [Theory]
    [InlineData(double.NaN, 0.9)]
    [InlineData(0.2, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.9)]
    [InlineData(0.2, double.NegativeInfinity)]
    public void NaN_or_infinite_values_return_unknown(double prob, double conf)
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(prob, conf, HumanGate, BotGate));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~VisitorClassTests|FullyQualifiedName~ClassGateResolverTests"
```

Expected: build failure, `VisitorClass` and `ClassGate` and `ClassGateResolver` not defined.

- [ ] **Step 3: Implement**

`src/Mostlylucid.BotDetection/Services/VisitorClass.cs`:

```csharp
namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Resolved class of the current request's visitor at shed-decision time,
///     derived from the cached fingerprint verdict against the policy's
///     <see cref="ClassGate"/> thresholds. The shed runs before detection so
///     we cannot know the current request's verdict; we use the prior verdict
///     stashed by the verdict cache.
/// </summary>
public enum VisitorClass
{
    /// <summary>
    ///     Prior verdict confidently classified this fingerprint as human.
    ///     Default policy: never shed.
    /// </summary>
    Human = 0,

    /// <summary>
    ///     No prior verdict, borderline prior, or low-confidence prior. Default
    ///     policy: shed at the configured unknown-class fraction.
    /// </summary>
    Unknown = 1,

    /// <summary>
    ///     Prior verdict confidently classified this fingerprint as bot.
    ///     Default policy: always shed when the band escalates.
    /// </summary>
    Bot = 2,
}

/// <summary>
///     Per-policy boundary defining which (prob, conf) tuples count as the
///     human / bot side. Boundaries are INCLUSIVE on both sides:
///     <c>prob &lt;= MaxBotProb</c> on the human side,
///     <c>prob &gt;= MinBotProb</c> on the bot side,
///     <c>conf &gt;= MinConfidence</c> on both. A verdict exactly at the
///     boundary qualifies.
/// </summary>
public sealed record ClassGate(
    double MaxBotProb = 1.0,
    double MinBotProb = 0.0,
    double MinConfidence = 0.0);

/// <summary>
///     Pure static resolver: given the cached prior (prob, conf) and the
///     policy's two gates, returns the visitor class. NaN / infinite / null
///     inputs all degrade to <see cref="VisitorClass.Unknown"/>; the resolver
///     never throws so the caller does not need a try/catch on the hot path.
/// </summary>
public static class ClassGateResolver
{
    public static VisitorClass Resolve(
        double? prob,
        double? conf,
        ClassGate humanGate,
        ClassGate botGate)
    {
        if (prob is null || conf is null) return VisitorClass.Unknown;
        var p = prob.Value;
        var c = conf.Value;
        if (double.IsNaN(p) || double.IsNaN(c) || double.IsInfinity(p) || double.IsInfinity(c))
            return VisitorClass.Unknown;
        if (p <= humanGate.MaxBotProb && c >= humanGate.MinConfidence) return VisitorClass.Human;
        if (p >= botGate.MinBotProb && c >= botGate.MinConfidence) return VisitorClass.Bot;
        return VisitorClass.Unknown;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~VisitorClassTests|FullyQualifiedName~ClassGateResolverTests"
```

Expected: 12 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
git branch --show-current   # MUST print "main"
git add src/Mostlylucid.BotDetection/Services/VisitorClass.cs \
        src/Mostlylucid.BotDetection.Test/Services/VisitorClassTests.cs \
        src/Mostlylucid.BotDetection.Test/Services/ClassGateResolverTests.cs
git commit -m "feat(load-shed): VisitorClass + ClassGate + resolver primitives"
```

---

## Task 2: Extend LoadShedOptions

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Policies/LoadShedOptionsDefaultsTests.cs`

**Interfaces:**
- Consumes: `ClassGate` from Task 1
- Produces: `LoadShedOptions` extended with `HumanGate`, `BotGate`, six per-class shed fractions. Existing `DropFractionAtHigh` / `DropFractionAtCritical` keep their field names and numeric values but now apply specifically to the unknown class.

- [ ] **Step 1: Write the failing test**

`src/Mostlylucid.BotDetection.Test/Policies/LoadShedOptionsDefaultsTests.cs`:

```csharp
using Mostlylucid.BotDetection.Policies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies;

/// <summary>
///     Pins the per-policy shed defaults that express the contract:
///     humans never shed by default, bots always shed when the band
///     escalates, unknowns shed at the operator-tunable fractions.
/// </summary>
public sealed class LoadShedOptionsDefaultsTests
{
    private static readonly LoadShedOptions Defaults = new();

    [Fact]
    public void Human_gate_default_is_strict()
    {
        Assert.Equal(0.3, Defaults.HumanGate.MaxBotProb);
        Assert.Equal(0.7, Defaults.HumanGate.MinConfidence);
    }

    [Fact]
    public void Bot_gate_default_is_strict()
    {
        Assert.Equal(0.5, Defaults.BotGate.MinBotProb);
        Assert.Equal(0.7, Defaults.BotGate.MinConfidence);
    }

    [Fact]
    public void Humans_never_shed_by_default()
    {
        Assert.Equal(0.0, Defaults.HumanShedAtHigh);
        Assert.Equal(0.0, Defaults.HumanShedAtCritical);
    }

    [Fact]
    public void Bots_always_shed_when_band_escalates_by_default()
    {
        Assert.Equal(1.0, Defaults.BotShedAtHigh);
        Assert.Equal(1.0, Defaults.BotShedAtCritical);
    }

    [Fact]
    public void Unknown_default_fractions_preserve_legacy_dropfraction_meaning()
    {
        Assert.Equal(0.3, Defaults.UnknownShedAtHigh);
        Assert.Equal(0.7, Defaults.UnknownShedAtCritical);
    }

    [Fact]
    public void Legacy_dropfraction_fields_remain_for_backward_compat()
    {
        // These existed before the redesign. Operator configs that bound them
        // continue to compile, even though the runtime now reads the
        // class-specific UnknownShedAt* fields. Kept for migration grace.
        Assert.Equal(0.2, Defaults.DropFractionAtHigh);
        Assert.Equal(0.5, Defaults.DropFractionAtCritical);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LoadShedOptionsDefaultsTests"
```

Expected: build failure, `HumanGate` / `BotGate` / `HumanShedAtHigh` etc. not defined.

- [ ] **Step 3: Implement**

Replace the entire contents of `src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs` with:

```csharp
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy load-shed configuration. Consulted by
///     <see cref="Services.LoadShedDecision"/> at request intake, BEFORE the
///     orchestrator is called. Sheds (refuses with 503 + Retry-After) when the
///     pipeline is under sustained High or Critical load, as reported by
///     <see cref="Services.PipelineLoadSensor.CurrentBand"/>.
///     <para>
///     Visitor-class-aware: every request is resolved against
///     <see cref="HumanGate"/> and <see cref="BotGate"/> to produce a
///     <see cref="VisitorClass"/>; the resolved class picks the matching
///     shed fraction. Humans are never shed by default, bots always shed
///     when the band escalates, and unknowns shed at an operator-tunable
///     fraction. An operator who wants humans shed under critical pressure
///     must explicitly set <see cref="HumanShedAtCritical"/> &gt; 0.
///     </para>
///     <para>
///     Normal and Low bands never shed any class; the per-class fractions
///     are only consulted at High and Critical. Sensor designs that expose
///     additional bands (currently none) should extend this options shape.
///     </para>
/// </summary>
public sealed record LoadShedOptions
{
    /// <summary>
    ///     Legacy per-band shed fraction for the High band. Kept for
    ///     backward-compat with operator configs that pre-date the
    ///     visitor-class-aware redesign. The runtime now reads
    ///     <see cref="UnknownShedAtHigh"/> instead; this field has no
    ///     effect on the shed decision.
    /// </summary>
    public double DropFractionAtHigh { get; init; } = 0.2;

    /// <summary>
    ///     Legacy per-band shed fraction for the Critical band. Kept for
    ///     backward-compat with operator configs that pre-date the
    ///     visitor-class-aware redesign. The runtime now reads
    ///     <see cref="UnknownShedAtCritical"/> instead; this field has no
    ///     effect on the shed decision.
    /// </summary>
    public double DropFractionAtCritical { get; init; } = 0.5;

    /// <summary>
    ///     Boundary defining which cached (prob, conf) tuples count as human
    ///     for the never-shed-humans-by-default guarantee. Default:
    ///     prob &lt;= 0.3 AND conf &gt;= 0.7.
    /// </summary>
    public ClassGate HumanGate { get; init; } = new(MaxBotProb: 0.3, MinConfidence: 0.7);

    /// <summary>
    ///     Boundary defining which cached (prob, conf) tuples count as bot
    ///     for the shed-bots-first guarantee. Default:
    ///     prob &gt;= 0.5 AND conf &gt;= 0.7.
    /// </summary>
    public ClassGate BotGate { get; init; } = new(MinBotProb: 0.5, MinConfidence: 0.7);

    /// <summary>Fraction of human-class requests shed at High band. Default 0.0 (never).</summary>
    public double HumanShedAtHigh { get; init; } = 0.0;

    /// <summary>Fraction of human-class requests shed at Critical band. Default 0.0 (never).</summary>
    public double HumanShedAtCritical { get; init; } = 0.0;

    /// <summary>Fraction of unknown-class requests shed at High band. Default 0.3.</summary>
    public double UnknownShedAtHigh { get; init; } = 0.3;

    /// <summary>Fraction of unknown-class requests shed at Critical band. Default 0.7.</summary>
    public double UnknownShedAtCritical { get; init; } = 0.7;

    /// <summary>Fraction of bot-class requests shed at High band. Default 1.0 (always).</summary>
    public double BotShedAtHigh { get; init; } = 1.0;

    /// <summary>Fraction of bot-class requests shed at Critical band. Default 1.0 (always).</summary>
    public double BotShedAtCritical { get; init; } = 1.0;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LoadShedOptionsDefaultsTests"
```

Expected: 6 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Policies/LoadShedOptions.cs \
        src/Mostlylucid.BotDetection.Test/Policies/LoadShedOptionsDefaultsTests.cs
git commit -m "feat(load-shed): extend LoadShedOptions with HumanGate/BotGate + per-class shed fractions"
```

---

## Task 3: Extend PipelineLoadSensorOptions

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/PipelineLoadSensorOptions.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `PipelineLoadSensorOptions` extended with `MinSamplesForTrustedBaseline` (int, default 30) and `BaselineRefreshInterval` (TimeSpan, default 1 min).

- [ ] **Step 1: Write the failing test**

Append to `src/Mostlylucid.BotDetection.Test/Services/PipelineLoadSensorTests.cs` (at the bottom of the class, before the closing brace):

```csharp
    [Fact]
    public void Options_default_MinSamplesForTrustedBaseline_is_30()
    {
        var opts = new PipelineLoadSensorOptions();
        Assert.Equal(30, opts.MinSamplesForTrustedBaseline);
    }

    [Fact]
    public void Options_default_BaselineRefreshInterval_is_one_minute()
    {
        var opts = new PipelineLoadSensorOptions();
        Assert.Equal(TimeSpan.FromMinutes(1), opts.BaselineRefreshInterval);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~PipelineLoadSensorTests.Options_default"
```

Expected: build failure, `MinSamplesForTrustedBaseline` and `BaselineRefreshInterval` not defined.

- [ ] **Step 3: Implement**

Append to `src/Mostlylucid.BotDetection/Services/PipelineLoadSensorOptions.cs` (inside the class, after `CriticalGen2PerSec`):

```csharp
    // --- baseline freshness (consumed by IEndpointPerfBaseline impls) ---

    /// <summary>
    ///     Minimum aggregated sample count for a per-endpoint template before
    ///     its baseline is trusted. Below this floor,
    ///     <see cref="IEndpointPerfBaseline.GetExpectedMs(string,string)"/>
    ///     returns 0 (treated as no baseline so the request contributes a
    ///     neutral 1.0 ratio). Strict less-than: a template with exactly this
    ///     many samples IS trusted. Default 30.
    /// </summary>
    public int MinSamplesForTrustedBaseline { get; set; } = 30;

    /// <summary>
    ///     How often
    ///     <see cref="DashboardEventStoreBackedEndpointPerfBaseline"/>
    ///     refreshes its in-memory snapshot. Piggybacks on the existing
    ///     <see cref="IScheduleCoordinator"/> tick cadence (matched to the
    ///     nearest available cadence; current impl subscribes to
    ///     <c>TickCadence.Tick1m</c>). Default 1 minute.
    /// </summary>
    public TimeSpan BaselineRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~PipelineLoadSensorTests.Options_default"
```

Expected: 2 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/PipelineLoadSensorOptions.cs \
        src/Mostlylucid.BotDetection.Test/Services/PipelineLoadSensorTests.cs
git commit -m "feat(load-shed): PipelineLoadSensorOptions MinSamplesForTrustedBaseline + BaselineRefreshInterval"
```

---

## Task 4: IEndpointPerfBaseline + NullEndpointPerfBaseline

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/IEndpointPerfBaseline.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/EndpointPerfBaselineTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `interface IEndpointPerfBaseline { double GetExpectedMs(string method, string normalizedPath) }`, `internal sealed class NullEndpointPerfBaseline : IEndpointPerfBaseline` (always returns 0).

- [ ] **Step 1: Write the failing test**

`src/Mostlylucid.BotDetection.Test/Services/EndpointPerfBaselineTests.cs`:

```csharp
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class NullEndpointPerfBaselineTests
{
    [Fact]
    public void Null_baseline_returns_zero_for_any_input()
    {
        var baseline = new NullEndpointPerfBaseline();
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/"));
        Assert.Equal(0.0, baseline.GetExpectedMs("POST", "/api/users"));
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/dashboard/entity/{slug}"));
    }

    [Fact]
    public void Null_baseline_tolerates_empty_and_null_inputs()
    {
        var baseline = new NullEndpointPerfBaseline();
        Assert.Equal(0.0, baseline.GetExpectedMs("", ""));
        Assert.Equal(0.0, baseline.GetExpectedMs(null!, null!));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~NullEndpointPerfBaselineTests"
```

Expected: build failure, `IEndpointPerfBaseline` and `NullEndpointPerfBaseline` not defined.

- [ ] **Step 3: Implement**

`src/Mostlylucid.BotDetection/Services/IEndpointPerfBaseline.cs`:

```csharp
namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Read-only per-(method, normalized-template) p95 lookup consumed by the
///     load-shed hot path to normalize upstream RTT into a dimensionless
///     deviation ratio. Implementations cache the values; the hot-path call
///     must be lock-free and allocation-free.
///     <para>
///     Optional DI: hosts that have no <see cref="UI.Services.IDashboardEventStore"/>
///     register <see cref="NullEndpointPerfBaseline"/>, and consumers degrade
///     to ratio 1.0 (no shed contribution) on those hosts.
///     </para>
///     <para>
///     <strong>Single consumer.</strong> Only the middleware OnCompleted hook
///     reads from this interface. Dashboard rendering, policy decisions, ops
///     surfaces all continue to read raw-path stats from
///     <see cref="UI.Services.IDashboardEventStore"/> directly. Do not add
///     convenience members here that would invite other call sites.
///     </para>
/// </summary>
public interface IEndpointPerfBaseline
{
    /// <summary>
    ///     Expected p95 in milliseconds for the given (method, normalized
    ///     template). Returns 0 when no trustworthy baseline exists yet (no
    ///     observations, below <see cref="PipelineLoadSensorOptions.MinSamplesForTrustedBaseline"/>,
    ///     or implementation absent). Callers MUST treat 0 as
    ///     "unknown endpoint, contribute neutral 1.0 ratio".
    /// </summary>
    double GetExpectedMs(string method, string normalizedPath);
}

/// <summary>
///     No-op default. Boots hosts that have no per-endpoint stats source
///     (website-only / remote dashboard mode). Always returns 0.
/// </summary>
internal sealed class NullEndpointPerfBaseline : IEndpointPerfBaseline
{
    public double GetExpectedMs(string method, string normalizedPath) => 0.0;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~NullEndpointPerfBaselineTests"
```

Expected: 2 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/IEndpointPerfBaseline.cs \
        src/Mostlylucid.BotDetection.Test/Services/EndpointPerfBaselineTests.cs
git commit -m "feat(load-shed): IEndpointPerfBaseline interface + NullEndpointPerfBaseline default"
```

---

## Task 5: DashboardEventStoreBackedEndpointPerfBaseline

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/DashboardEventStoreBackedEndpointPerfBaseline.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/EndpointPerfBaselineTests.cs` (append a second test class)

**Interfaces:**
- Consumes: `IEndpointPerfBaseline` (Task 4), `PipelineLoadSensorOptions.MinSamplesForTrustedBaseline` (Task 3), `IScheduleCoordinator` + `TickCadence.Tick1m` (existing FOSS abstractions), `IDashboardEventStore.GetEndpointStatsAsync` (existing FOSS abstraction in `Mostlylucid.BotDetection.UI.Services`), `PathNormalizer.Normalize` (existing FOSS helper in `Mostlylucid.BotDetection.Markov`).
- Produces: `internal sealed class DashboardEventStoreBackedEndpointPerfBaseline : IEndpointPerfBaseline, IDisposable` with constructor `(IDashboardEventStore store, IOptions<BotDetectionOptions> options, IScheduleCoordinator? coordinator = null, ILogger<DashboardEventStoreBackedEndpointPerfBaseline>? logger = null)`.

- [ ] **Step 1: Write the failing test**

Append to `src/Mostlylucid.BotDetection.Test/Services/EndpointPerfBaselineTests.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Models;

public sealed class DashboardEventStoreBackedEndpointPerfBaselineTests
{
    /// <summary>
    ///     Fully-stubbed IDashboardEventStore that throws on every member
    ///     except GetEndpointStatsAsync. The baseline only consults that
    ///     one member; the rest of the FOSS interface (a wide surface) is
    ///     irrelevant to this test class.
    /// </summary>
    private class FakeStore : IDashboardEventStore
    {
        public virtual List<DashboardEndpointStats> Stats { get; set; } = new();

        public virtual Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null)
            => Task.FromResult(Stats);

        // --- Everything else throws. Bulk stubs to keep the file compilable. ---

        public Task AddDetectionAsync(DashboardDetectionEvent detection)
            => throw new System.NotSupportedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
            => throw new System.NotSupportedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0,
            bool? isBot = null) => throw new System.NotSupportedException();
        public Task<DashboardSummary> GetSummaryAsync(System.DateTime? startTime = null,
            System.DateTime? endTime = null, string? audienceFilter = null)
            => throw new System.NotSupportedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(System.DateTime startTime,
            System.DateTime endTime, System.TimeSpan bucketSize, string? audienceFilter = null)
            => throw new System.NotSupportedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10,
            System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null) => throw new System.NotSupportedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20,
            System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null) => throw new System.NotSupportedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode,
            System.DateTime? startTime = null, System.DateTime? endTime = null)
            => throw new System.NotSupportedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature,
            int topN = 25, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path,
            System.DateTime? startTime = null, System.DateTime? endTime = null)
            => throw new System.NotSupportedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20,
            System.DateTime? startTime = null, System.DateTime? endTime = null)
            => throw new System.NotSupportedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20)
            => throw new System.NotSupportedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family,
            int hours = 168, CancellationToken ct = default)
            => throw new System.NotSupportedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50,
            System.DateTime? startTime = null, System.DateTime? endTime = null,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter,
            CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<int> PruneOldDetectionsAsync(System.DateTime cutoff, CancellationToken ct = default)
            => throw new System.NotSupportedException();
    }

    private static DashboardEventStoreBackedEndpointPerfBaseline NewBaseline(
        FakeStore store, int minSamples = 30)
    {
        var opts = Options.Create(new BotDetectionOptions
        {
            PipelineLoadSensor = new PipelineLoadSensorOptions
            {
                MinSamplesForTrustedBaseline = minSamples,
            },
        });
        return new DashboardEventStoreBackedEndpointPerfBaseline(
            store, opts, scheduleCoordinator: null);
    }

    [Fact]
    public async Task GetExpectedMs_returns_zero_before_any_refresh()
    {
        var baseline = NewBaseline(new FakeStore());
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/"));
    }

    [Fact]
    public async Task Refresh_aggregates_raw_paths_into_normalized_templates()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 20 },
                new DashboardEndpointStats { Method = "GET", Path = "/users/456",
                    P95ProcessingTimeMs = 110, TotalCount = 20 },
            },
        };
        var baseline = NewBaseline(store, minSamples: 30);
        await baseline.RefreshNowAsync(CancellationToken.None);
        // Combined sample count (40) >= 30 -> baseline is trusted.
        // Weighted p95 across two rows with equal counts ~= 105ms.
        var actual = baseline.GetExpectedMs("GET", "/users/{id}");
        Assert.InRange(actual, 100, 110);
    }

    [Fact]
    public async Task Below_threshold_template_returns_zero()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 5 },
            },
        };
        var baseline = NewBaseline(store, minSamples: 30);
        await baseline.RefreshNowAsync(CancellationToken.None);
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/users/{id}"));
    }

    [Fact]
    public async Task Unknown_method_or_path_returns_zero()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 100 },
            },
        };
        var baseline = NewBaseline(store);
        await baseline.RefreshNowAsync(CancellationToken.None);
        Assert.Equal(0.0, baseline.GetExpectedMs("POST", "/users/{id}"));
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/admin"));
    }

    [Fact]
    public async Task Refresh_failure_preserves_prior_snapshot()
    {
        var store = new FakeStore
        {
            Stats =
            {
                new DashboardEndpointStats { Method = "GET", Path = "/users/123",
                    P95ProcessingTimeMs = 100, TotalCount = 100 },
            },
        };
        var baseline = NewBaseline(store);
        await baseline.RefreshNowAsync(CancellationToken.None);
        var before = baseline.GetExpectedMs("GET", "/users/{id}");
        Assert.True(before > 0);

        // Swap to a faulting store; the next refresh should fail silently
        // and leave the prior snapshot in place.
        var faulting = new FailingStore();
        var faulted = new DashboardEventStoreBackedEndpointPerfBaseline(
            faulting,
            Options.Create(new BotDetectionOptions
            {
                PipelineLoadSensor = new PipelineLoadSensorOptions { MinSamplesForTrustedBaseline = 30 },
            }),
            scheduleCoordinator: null);
        await faulted.RefreshNowAsync(CancellationToken.None);
        // The faulted baseline has no prior snapshot so its lookup is 0.
        Assert.Equal(0.0, faulted.GetExpectedMs("GET", "/users/{id}"));
        // The original baseline's snapshot is untouched.
        Assert.Equal(before, baseline.GetExpectedMs("GET", "/users/{id}"));
    }

    /// <summary>
    ///     Variant of FakeStore where GetEndpointStatsAsync throws. Used to
    ///     pin the "refresh failure preserves prior snapshot" contract.
    /// </summary>
    private sealed class FailingStore : FakeStore
    {
        public override Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, System.DateTime? startTime = null, System.DateTime? endTime = null,
            string? audienceFilter = null)
            => throw new System.InvalidOperationException("store offline");
    }
}
```

NOTE TO IMPLEMENTER: `IDashboardEventStore` has a wide surface beyond `GetEndpointStatsAsync` / `GetEndpointDetailAsync`. The test fixtures only exercise `GetEndpointStatsAsync`; stub the other interface members to throw `NotSupportedException` (the test never calls them). Look at `src/Mostlylucid.BotDetection.Test/UI/SqliteDashboardStoreFixture.cs` for the exact set of members to stub.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~DashboardEventStoreBackedEndpointPerfBaselineTests"
```

Expected: build failure, `DashboardEventStoreBackedEndpointPerfBaseline` not defined.

- [ ] **Step 3: Implement**

`src/Mostlylucid.BotDetection/Services/DashboardEventStoreBackedEndpointPerfBaseline.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Read-through baseline cache backed by the FOSS dashboard event store.
///     On each <see cref="TickCadence.Tick1m"/> (or whatever cadence
///     <see cref="PipelineLoadSensorOptions.BaselineRefreshInterval"/>
///     translates to), pulls per-(method, path) stats from
///     <see cref="IDashboardEventStore.GetEndpointStatsAsync"/>, groups by
///     <c>(method, PathNormalizer.Normalize(path))</c>, computes a per-template
///     count-weighted p95 plus total sample count, and atomically swaps the
///     in-memory dictionary. <see cref="GetExpectedMs"/> is a lock-free read
///     against the snapshot.
///     <para>
///     Returns 0 for templates whose aggregated sample count is below
///     <see cref="PipelineLoadSensorOptions.MinSamplesForTrustedBaseline"/>
///     (strict less-than). Returns 0 on cache miss. Refresh failures preserve
///     the prior snapshot and emit a single warn log per failure (no spam).
///     </para>
/// </summary>
internal sealed class DashboardEventStoreBackedEndpointPerfBaseline : IEndpointPerfBaseline, IDisposable
{
    private readonly IDashboardEventStore _store;
    private readonly PipelineLoadSensorOptions _options;
    private readonly ILogger<DashboardEventStoreBackedEndpointPerfBaseline>? _logger;
    private readonly IDisposable? _subscription;

    // Atomic snapshot. Reads via Volatile.Read; writes via Interlocked.Exchange.
    private IReadOnlyDictionary<(string Method, string Template), double> _snapshot
        = new Dictionary<(string, string), double>();

    public DashboardEventStoreBackedEndpointPerfBaseline(
        IDashboardEventStore store,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null,
        ILogger<DashboardEventStoreBackedEndpointPerfBaseline>? logger = null)
    {
        _store = store;
        _options = options.Value.PipelineLoadSensor;
        _logger = logger;

        // Optional so test fixtures that construct the baseline directly (without
        // scheduling) keep working. Production DI passes the real coordinator.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1m,
                "EndpointPerfBaseline",
                CostHint.Low,
                OnTickAsync);
        }
    }

    public double GetExpectedMs(string method, string normalizedPath)
    {
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(normalizedPath)) return 0.0;
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.TryGetValue((method.ToUpperInvariant(), normalizedPath), out var p95) ? p95 : 0.0;
    }

    public void Dispose() => _subscription?.Dispose();

    private Task OnTickAsync(CancellationToken ct) => RefreshNowAsync(ct);

    /// <summary>
    ///     Test hook: run one refresh synchronously. Production callers use
    ///     the tick subscription.
    /// </summary>
    internal async Task RefreshNowAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _store.GetEndpointStatsAsync(count: int.MaxValue);
            var grouped = new Dictionary<(string, string), (double WeightedP95Sum, long TotalCount)>();
            foreach (var s in stats)
            {
                if (string.IsNullOrEmpty(s.Method) || string.IsNullOrEmpty(s.Path)) continue;
                var template = PathNormalizer.Normalize(s.Path);
                var key = (s.Method.ToUpperInvariant(), template);
                grouped.TryGetValue(key, out var prior);
                grouped[key] = (
                    WeightedP95Sum: prior.WeightedP95Sum + s.P95ProcessingTimeMs * s.TotalCount,
                    TotalCount: prior.TotalCount + s.TotalCount);
            }
            var snapshot = new Dictionary<(string, string), double>(grouped.Count);
            foreach (var (key, agg) in grouped)
            {
                if (agg.TotalCount < _options.MinSamplesForTrustedBaseline) continue;
                snapshot[key] = agg.TotalCount > 0 ? agg.WeightedP95Sum / agg.TotalCount : 0.0;
            }
            Interlocked.Exchange(ref _snapshot, snapshot);
        }
        catch (Exception ex)
        {
            // Sampled warn (one per failure, no spam under sustained errors).
            // Prior snapshot stays in place so the hot path keeps reading
            // last-good values until the next successful refresh.
            _logger?.LogWarning(ex,
                "EndpointPerfBaseline refresh failed; keeping prior snapshot ({Count} templates)",
                Volatile.Read(ref _snapshot).Count);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~DashboardEventStoreBackedEndpointPerfBaselineTests"
```

Expected: 5 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/DashboardEventStoreBackedEndpointPerfBaseline.cs \
        src/Mostlylucid.BotDetection.Test/Services/EndpointPerfBaselineTests.cs
git commit -m "feat(load-shed): DashboardEventStoreBackedEndpointPerfBaseline (Tick1m refresh + template grouping)"
```

---

## Task 6: PipelineLoadSensor.RecordUpstreamDeviation

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/PipelineLoadSensor.cs`
- Modify: `src/Mostlylucid.BotDetection.Test/Services/PipelineLoadSensorTests.cs`

**Interfaces:**
- Consumes: nothing new
- Produces: `PipelineLoadSensor.RecordUpstreamDeviation(double ratio)` replaces the existing `RecordUpstreamRtt(double ms)`. The deviation EMA fires the band against `HighRatio` / `CriticalRatio` directly (no per-axis baseline state; the implicit baseline is 1.0). The rolling-window upstream-baseline arrays + helper are removed; the detection-latency axis keeps its existing rolling-window baseline unchanged.

- [ ] **Step 1: Write the failing tests**

Append to `src/Mostlylucid.BotDetection.Test/Services/PipelineLoadSensorTests.cs` (after the new options tests from Task 3, before the closing brace):

```csharp
    [Fact]
    public void Upstream_deviation_at_one_keeps_band_low()
    {
        var s = New();
        for (var i = 0; i < 60; i++)
        {
            s.RecordUpstreamDeviation(1.0);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Low, s.CurrentBand);
    }

    [Fact]
    public void Upstream_deviation_at_threshold_high_fires_High()
    {
        var s = New(highRatio: 2.0, criticalRatio: 5.0);
        for (var i = 0; i < 60; i++)
        {
            s.RecordUpstreamDeviation(2.5);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.High, s.CurrentBand);
    }

    [Fact]
    public void Upstream_deviation_at_threshold_critical_fires_Critical()
    {
        var s = New(highRatio: 2.0, criticalRatio: 5.0);
        for (var i = 0; i < 60; i++)
        {
            s.RecordUpstreamDeviation(6.0);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Critical, s.CurrentBand);
    }
```

Delete the test `Baseline_RecoversFromAnomalouslyFastWarmupSample` from the same file (it pins behaviour of the old absolute-RTT baseline which no longer exists).

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~PipelineLoadSensorTests.Upstream_deviation"
```

Expected: build failure, `RecordUpstreamDeviation` not defined.

- [ ] **Step 3: Implement**

In `src/Mostlylucid.BotDetection/Services/PipelineLoadSensor.cs`:

  1. Remove the field block for the upstream-RTT rolling window (the
     `_rttWindow`, `_rttWindowWriteIdx`, `_rttWindowFilled`, `_rttWindowLock`
     fields, plus their initialization in both constructors).
  2. Replace the existing public method `RecordUpstreamRtt(double ms)` with:

```csharp
    /// <summary>
    ///     Records the post-detection-deviation ratio for one completed
    ///     request, where <paramref name="ratio"/> is
    ///     <c>actualUpstreamMs / expectedMsFromIEndpointPerfBaseline</c>.
    ///     1.0 means the request hit its endpoint's own normal; 2.0 means
    ///     twice the endpoint's p95; etc. Unknown endpoints (no baseline)
    ///     should pass 1.0 so they contribute a neutral sample.
    ///     <para>
    ///     Replaces the pre-2026-06-25 <c>RecordUpstreamRtt(double ms)</c>
    ///     which fed absolute milliseconds into a global rolling-window
    ///     baseline. That design tripped Critical on hosts that serve both
    ///     fast static assets and slow dashboard pages (different intrinsic
    ///     latencies cannot share one baseline). The deviation ratio
    ///     normalises against each endpoint's own normal, so a mixed
    ///     workload stays in Low band by default.
    ///     </para>
    /// </summary>
    public void RecordUpstreamDeviation(double ratio)
    {
        if (ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(ratio)) return;
        Interlocked.Add(ref _rttAccumUs, (long)(ratio * 1_000_000.0));  // scaled so the EMA accumulator stays in long range
        Interlocked.Increment(ref _rttSampleCount);
    }
```

  3. In the existing `Tick` method, in the block under
     `// ---- Upstream RTT: same shape ----`, replace the per-tick baseline
     update + the `UpdateRollingBaseline(_rttWindow, ...)` call with a direct
     EMA update against an implicit baseline of 1.0:

```csharp
        // ---- Upstream deviation EMA (replaces the old rolling-baseline path) ----
        // _rttAccumUs / _rttSampleCount produces the mean ratio for the tick;
        // the EMA smooths it; band selection (below) compares EMA ratio
        // directly to HighRatio / CriticalRatio. Baseline is implicitly 1.0
        // because the input is already normalised by the per-endpoint p95
        // from IEndpointPerfBaseline at the call site.
        var rttAccum = Interlocked.Exchange(ref _rttAccumUs, 0);
        var rttCount = Interlocked.Exchange(ref _rttSampleCount, 0);
        if (rttCount > 0)
        {
            // Recover the ratio from the scaled accumulator (see RecordUpstreamDeviation).
            var meanRatio = (rttAccum / 1_000_000.0) / rttCount;
            var prevEma = Volatile.Read(ref _rttEmaUs);
            // Re-use _rttEmaUs to hold the EMA of ratios (scaled by 1.0 here
            // since the input is already dimensionless).
            Interlocked.Exchange(ref _rttEmaUs, Ewma.Update(prevEma, meanRatio, Alpha));
            // Mark the axis as having data so the band selector engages.
            Interlocked.Increment(ref _rttBaselineSamples);
        }
```

  4. In the existing `CurrentBand` getter, in the block currently labelled
     `if (rttReady) { ... ratio = _rttEmaUs / baseUs ... }`, replace the
     baseline-relative ratio with a direct read of `_rttEmaUs` (which now
     IS the EMA-of-ratio):

```csharp
            if (rttReady)
            {
                var ratio = Volatile.Read(ref _rttEmaUs);
                if (ratio >= _criticalRatio) return LoadBand.Critical;
                if (ratio >= _highRatio)     band = Worse(band, LoadBand.High);
                else if (ratio >= NormalRatio) band = Worse(band, LoadBand.Normal);
            }
```

  5. Remove the `_rttBaselineUs` field entirely (no longer used: the EMA
     itself IS the value the band compares against). Remove the
     `UpstreamRttRatio` public property (replaced by direct EMA inspection
     via a renamed property).

  6. Add a new public property:

```csharp
    /// <summary>
    ///     Current EMA of the per-request deviation ratio fed by
    ///     <see cref="RecordUpstreamDeviation"/>. Returns 0 before the
    ///     first sample. Band escalates when this value crosses the
    ///     configured High / Critical ratios.
    /// </summary>
    public double UpstreamDeviationEma => Volatile.Read(ref _rttEmaUs);
```

  7. Update `PressureSignalContributor.cs:43` (single line) so the
     pressure signal projection reads from the new property. Per the
     spec, the SIGNAL KEY NAME stays the same (existing policy
     predicates may filter on it), only the SEMANTIC changes from
     "absolute ms over baseline" to "EMA of deviation ratio":

```csharp
// Was:
//   signals.TryAdd("pressure.upstream_rtt_ratio",         _sensor.UpstreamRttRatio);
// Now:
signals.TryAdd("pressure.upstream_rtt_ratio",         _sensor.UpstreamDeviationEma);
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~PipelineLoadSensorTests"
```

Expected: all `PipelineLoadSensorTests` green (the existing detection-latency tests stay; the upstream-RTT-percentile-warmup test was deleted; the three new deviation tests pass).

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/PipelineLoadSensor.cs \
        src/Mostlylucid.BotDetection.Test/Services/PipelineLoadSensorTests.cs
git commit -m "feat(load-sensor): RecordUpstreamDeviation replaces RecordUpstreamRtt (EMA of ratio)"
```

---

## Task 7: LoadShedDecision rewrite

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs`
- Create: `src/Mostlylucid.BotDetection.Test/Services/LoadShedDecisionTests.cs`

**Interfaces:**
- Consumes: `VisitorClass`, `LoadShedOptions` (Tasks 1, 2), `ILoadBandSource` (existing FOSS abstraction), `DeterministicBucket.ShouldFire(int seed, double fraction)` (existing FOSS helper).
- Produces: `LoadShedDecision.ShouldShed(VisitorClass class, LoadShedOptions options, int requestSeed)` replaces the pre-redesign `ShouldShed(LoadShedOptions, int, ShedHint)`. The `ShedHint` enum is deleted.

- [ ] **Step 1: Write the failing test**

`src/Mostlylucid.BotDetection.Test/Services/LoadShedDecisionTests.cs`:

```csharp
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class LoadShedDecisionTests
{
    private sealed class FakeSource(LoadBand band) : ILoadBandSource
    {
        public LoadBand CurrentBand { get; } = band;
    }

    private static LoadShedDecision New(LoadBand band) => new(new FakeSource(band));

    [Theory]
    [InlineData(LoadBand.Low)]
    [InlineData(LoadBand.Normal)]
    public void Never_sheds_at_low_or_normal_regardless_of_class(LoadBand band)
    {
        var decision = New(band);
        var opts = new LoadShedOptions();
        Assert.False(decision.ShouldShed(VisitorClass.Human, opts, requestSeed: 1));
        Assert.False(decision.ShouldShed(VisitorClass.Unknown, opts, requestSeed: 1));
        Assert.False(decision.ShouldShed(VisitorClass.Bot, opts, requestSeed: 1));
    }

    [Fact]
    public void Humans_never_shed_at_high_by_default()
    {
        var decision = New(LoadBand.High);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.False(decision.ShouldShed(VisitorClass.Human, opts, requestSeed: seed));
    }

    [Fact]
    public void Humans_never_shed_at_critical_by_default()
    {
        var decision = New(LoadBand.Critical);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.False(decision.ShouldShed(VisitorClass.Human, opts, requestSeed: seed));
    }

    [Fact]
    public void Bots_always_shed_at_high_by_default()
    {
        var decision = New(LoadBand.High);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Bot, opts, requestSeed: seed));
    }

    [Fact]
    public void Bots_always_shed_at_critical_by_default()
    {
        var decision = New(LoadBand.Critical);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Bot, opts, requestSeed: seed));
    }

    [Fact]
    public void Operator_can_opt_in_to_shedding_humans()
    {
        var decision = New(LoadBand.Critical);
        var opts = new LoadShedOptions { HumanShedAtCritical = 1.0 };
        for (var seed = 0; seed < 1000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Human, opts, requestSeed: seed));
    }

    [Fact]
    public void Unknown_class_sheds_at_configured_fraction_deterministically()
    {
        var decision = New(LoadBand.High);
        var opts = new LoadShedOptions { UnknownShedAtHigh = 0.5 };
        var shedCount = 0;
        const int n = 10_000;
        for (var seed = 0; seed < n; seed++)
            if (decision.ShouldShed(VisitorClass.Unknown, opts, requestSeed: seed)) shedCount++;
        // DeterministicBucket distributes hashes uniformly; +-3% tolerance.
        var observed = shedCount / (double)n;
        Assert.InRange(observed, 0.47, 0.53);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LoadShedDecisionTests"
```

Expected: build failure, `LoadShedDecision.ShouldShed(VisitorClass, ...)` not defined.

- [ ] **Step 3: Implement**

Replace the entire contents of `src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs` with:

```csharp
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Minimal abstraction over <see cref="PipelineLoadSensor.CurrentBand"/>
///     so the shed decision can be unit-tested without spinning up the real
///     sensor.
/// </summary>
public interface ILoadBandSource
{
    LoadBand CurrentBand { get; }
}

/// <summary>
///     Visitor-class-aware shed decision. Resolves the per-band, per-class
///     shed fraction from <see cref="LoadShedOptions"/> and rolls a
///     deterministic bucket from the request seed.
///     <para>
///     Contract: humans never shed by default (operator must explicitly set
///     <see cref="LoadShedOptions.HumanShedAtCritical"/> &gt; 0 to opt in);
///     bots always shed when the band escalates; unknowns shed at the
///     configured fraction. Low and Normal bands always pass regardless of
///     class.
///     </para>
/// </summary>
public sealed class LoadShedDecision
{
    private readonly ILoadBandSource _source;

    public LoadShedDecision(ILoadBandSource source) => _source = source;

    /// <summary>
    ///     Returns true when the current request should be shed (refused
    ///     with 503 + Retry-After when band is Critical; skip detection +
    ///     forward when band is High).
    /// </summary>
    /// <param name="visitorClass">
    ///     Resolved by <see cref="ClassGateResolver.Resolve"/> from the
    ///     cached fingerprint verdict against the policy's
    ///     <see cref="LoadShedOptions.HumanGate"/> /
    ///     <see cref="LoadShedOptions.BotGate"/>.
    /// </param>
    /// <param name="options">Per-policy shed fractions.</param>
    /// <param name="requestSeed">
    ///     Stable hash seed (e.g. connection id) so identical requests get
    ///     identical shed outcomes.
    /// </param>
    public bool ShouldShed(VisitorClass visitorClass, LoadShedOptions options, int requestSeed)
    {
        var band = _source.CurrentBand;
        if (band == LoadBand.Low || band == LoadBand.Normal) return false;

        var fraction = (visitorClass, band) switch
        {
            (VisitorClass.Human,   LoadBand.High)     => options.HumanShedAtHigh,
            (VisitorClass.Human,   LoadBand.Critical) => options.HumanShedAtCritical,
            (VisitorClass.Unknown, LoadBand.High)     => options.UnknownShedAtHigh,
            (VisitorClass.Unknown, LoadBand.Critical) => options.UnknownShedAtCritical,
            (VisitorClass.Bot,     LoadBand.High)     => options.BotShedAtHigh,
            (VisitorClass.Bot,     LoadBand.Critical) => options.BotShedAtCritical,
            _ => 0.0,
        };
        return DeterministicBucket.ShouldFire(requestSeed, fraction);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LoadShedDecisionTests"
```

Expected: 7 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/LoadShedDecision.cs \
        src/Mostlylucid.BotDetection.Test/Services/LoadShedDecisionTests.cs
git commit -m "feat(load-shed): LoadShedDecision rewrite (VisitorClass-aware, drop ShedHint)"
```

---

## Task 8: BotDetectionMiddleware wire

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`

**Interfaces:**
- Consumes: `IEndpointPerfBaseline`, `NullEndpointPerfBaseline`, `DashboardEventStoreBackedEndpointPerfBaseline` (Tasks 4, 5), `VisitorClass`, `ClassGateResolver` (Task 1), `LoadShedDecision.ShouldShed(VisitorClass, LoadShedOptions, int)` (Task 7), `PipelineLoadSensor.RecordUpstreamDeviation(double)` (Task 6), `PathNormalizer.Normalize(string)` (existing).
- Produces: nothing new in the interface sense; this task wires the new abstractions into the request pipeline.

- [ ] **Step 1: Find and replace the `ResolveShedHint` call site (around line 696)**

In `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`, around line 696, replace:

```csharp
            var shedHint = ResolveShedHint(context);
            if (_loadShedDecision.ShouldShed(policy.LoadShed, loadShedSeed, shedHint))
```

with:

```csharp
            var visitorClass = ResolveVisitorClass(context, policy.LoadShed);
            if (_loadShedDecision.ShouldShed(visitorClass, policy.LoadShed, loadShedSeed))
```

- [ ] **Step 2: Replace the `ResolveShedHint` helper (around line 1497)**

Delete the existing private static method `ResolveShedHint(HttpContext context)`. Replace it with:

```csharp
    /// <summary>
    ///     Resolve the visitor class for the shed decision from the cached
    ///     prior verdict stashed in <c>HttpContext.Items</c>. Reads the
    ///     prior probability and confidence under
    ///     <see cref="SignalKeys.FingerprintPriorProbability"/> and
    ///     <see cref="SignalKeys.FingerprintPriorConfidence"/>, runs them
    ///     through <see cref="ClassGateResolver.Resolve"/> against the
    ///     policy's <see cref="LoadShedOptions.HumanGate"/> /
    ///     <see cref="LoadShedOptions.BotGate"/>. Cold cache returns
    ///     <see cref="VisitorClass.Unknown"/>.
    /// </summary>
    private static VisitorClass ResolveVisitorClass(HttpContext context, LoadShedOptions options)
    {
        double? prob = context.Items.TryGetValue(SignalKeys.FingerprintPriorProbability, out var p) && p is double pd
            ? pd
            : null;
        double? conf = context.Items.TryGetValue(SignalKeys.FingerprintPriorConfidence, out var c) && c is double cd
            ? cd
            : null;
        return ClassGateResolver.Resolve(prob, conf, options.HumanGate, options.BotGate);
    }
```

NOTE TO IMPLEMENTER: `SignalKeys.FingerprintPriorConfidence` may not exist yet. If a quick grep confirms it is missing, ADD it next to `FingerprintPriorProbability` in `src/Mostlylucid.BotDetection/Models/SignalKeys.cs` (single-line addition: a `public const string FingerprintPriorConfidence = "fingerprint.prior_confidence";`) AND audit every writer of `FingerprintPriorProbability` to also stash the matching confidence. The verdict-cache writer is the single canonical writer; updating it is part of this task.

- [ ] **Step 3: Update the OnCompleted hook (around line 165)**

In `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`, around line 165-171, the existing block is:

```csharp
            if (_loadSensor is not null && !context.Items.ContainsKey(BotDetectionShedKey))
            {
                var detectionMs = (context.Items[AggregatedEvidenceKey] as AggregatedEvidence)?.TotalProcessingTimeMs ?? 0.0;
                if (detectionMs > 0) _loadSensor.RecordDetectionLatency(detectionMs);
                var upstreamMs = latencyMs - detectionMs;
                if (upstreamMs > 0) _loadSensor.RecordUpstreamRtt(upstreamMs);
            }
```

Replace with:

```csharp
            if (_loadSensor is not null && !context.Items.ContainsKey(BotDetectionShedKey))
            {
                var detectionMs = (context.Items[AggregatedEvidenceKey] as AggregatedEvidence)?.TotalProcessingTimeMs ?? 0.0;
                if (detectionMs > 0) _loadSensor.RecordDetectionLatency(detectionMs);

                var upstreamMs = latencyMs - detectionMs;
                if (upstreamMs > 0)
                {
                    // Per-endpoint deviation: ratio = actualUpstreamMs / endpointP95.
                    // Unknown endpoints (no baseline) contribute neutral 1.0 so they
                    // cannot trip pressure on their own. Per-endpoint baseline is
                    // optional DI (NullEndpointPerfBaseline default), so this is
                    // safe on hosts that have no DashboardEventStore.
                    double ratio = 1.0;
                    try
                    {
                        var expected = _endpointPerfBaseline?.GetExpectedMs(
                            context.Request.Method,
                            PathNormalizer.Normalize(requestPath)) ?? 0.0;
                        if (expected > 0) ratio = upstreamMs / expected;
                    }
                    catch
                    {
                        // Defensive: a baseline impl that throws falls back to
                        // ratio 1.0 (no contribution). The other sensor axes
                        // continue to drive the band.
                        ratio = 1.0;
                    }
                    _loadSensor.RecordUpstreamDeviation(ratio);
                }
            }
```

NOTE TO IMPLEMENTER: `_endpointPerfBaseline` is a new private field. Add it to the field block at the top of the class (`private readonly IEndpointPerfBaseline? _endpointPerfBaseline;`) and inject it through the constructor (nullable so existing test fixtures that omit it keep working). Be sure to add the `using Mostlylucid.BotDetection.Markov;` import for `PathNormalizer` if the file does not already have it.

- [ ] **Step 4: Update DI registration**

In `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`, add immediately AFTER the existing `services.AddSingleton<Services.LoadShedDecision>();` line (around line 1283 in the current file):

```csharp
        // Per-endpoint perf baseline for the load-shed hot path.
        // TryAdd NullEndpointPerfBaseline as the default so hosts without
        // an IDashboardEventStore boot; deployments that DO register the
        // store can call AddDashboardEndpointPerfBaseline() to replace it
        // with the DashboardEventStore-backed impl. Per the
        // remote-mode-optional-DI rule, the middleware tolerates either.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddSingleton<Services.IEndpointPerfBaseline, Services.NullEndpointPerfBaseline>(services);
```

Then add a new public extension method on `ServiceCollectionExtensions` (or wherever the dashboard-store-registration extensions live in this file):

```csharp
    /// <summary>
    ///     Replace the default <see cref="Services.NullEndpointPerfBaseline"/>
    ///     with the <see cref="Services.DashboardEventStoreBackedEndpointPerfBaseline"/>
    ///     that reads per-(method, normalized-template) p95 from
    ///     <see cref="UI.Services.IDashboardEventStore"/>. Call this from the
    ///     gateway / dashboard-host bootstrap AFTER
    ///     <c>AddBotDetectionDashboard()</c> so the store is registered first.
    /// </summary>
    public static IServiceCollection AddDashboardEndpointPerfBaseline(this IServiceCollection services)
    {
        services.RemoveAll<Services.IEndpointPerfBaseline>();
        services.AddSingleton<Services.IEndpointPerfBaseline, Services.DashboardEventStoreBackedEndpointPerfBaseline>();
        return services;
    }
```

NOTE TO IMPLEMENTER: `RemoveAll<T>` lives in `Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions`. Make sure the `using` is present.

- [ ] **Step 5: Build the full FOSS solution and run all impacted tests**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~LoadShed|FullyQualifiedName~PipelineLoadSensor|FullyQualifiedName~VisitorClass|FullyQualifiedName~ClassGate|FullyQualifiedName~EndpointPerfBaseline"
```

Expected: build green, all impacted tests green.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs \
        src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs \
        src/Mostlylucid.BotDetection/Models/SignalKeys.cs
git commit -m "feat(load-shed): wire VisitorClass + IEndpointPerfBaseline into BotDetectionMiddleware + DI"
```

---

## Task 9: Regression test for the staging mixed-workload scenario

**Files:**
- Create: `src/Mostlylucid.BotDetection.Test/Services/StagingMixedWorkloadShedTests.cs`

**Interfaces:**
- Consumes: `PipelineLoadSensor.RecordUpstreamDeviation` (Task 6), `IEndpointPerfBaseline` (Task 4), the new `LoadShedDecision` (Task 7).
- Produces: a regression test that pins exactly the 2026-06-25 staging bug stays fixed.

- [ ] **Step 1: Write the failing test**

`src/Mostlylucid.BotDetection.Test/Services/StagingMixedWorkloadShedTests.cs`:

```csharp
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Regression: the 2026-06-25 staging incident where the website host
///     served both fast static assets (about 10ms) and a slower dashboard
///     URL (about 110ms). The pre-redesign sensor learned a global baseline
///     from the fast paths and read the dashboard URL as 11x over baseline,
///     tripping Critical and refusing 50% of requests with 503.
///
///     With the per-endpoint deviation axis, each endpoint contributes
///     ratio 1.0 (it is at its own normal) and the band stays Low. This
///     test simulates the exact traffic shape and asserts no band escalation.
/// </summary>
public sealed class StagingMixedWorkloadShedTests
{
    private sealed class FakeBaseline : IEndpointPerfBaseline
    {
        private readonly Dictionary<(string, string), double> _values = new()
        {
            { ("GET", "/img/{static}"), 10.0 },
            { ("GET", "/dashboard/entity/{slug}"), 110.0 },
        };

        public double GetExpectedMs(string method, string normalizedPath)
            => _values.TryGetValue((method, normalizedPath), out var v) ? v : 0.0;
    }

    [Fact]
    public void Mixed_workload_at_each_endpoints_own_normal_stays_in_low_band()
    {
        var sensor = new PipelineLoadSensor(
            normalRps: 1e9, highRps: 1e9, criticalRps: 1e9,
            highRatio: 2.0, criticalRatio: 5.0,
            highStarvedTicks: int.MaxValue, criticalStarvedTicks: int.MaxValue,
            highGen2PerSec: 1e9, criticalGen2PerSec: 1e9);
        var baseline = new FakeBaseline();

        // Simulate 60 ticks; per tick: 100 fast-static requests + 50 slow-dashboard
        // requests, each AT its own endpoint's normal p95. Ratio is 1.0 throughout.
        for (var tick = 0; tick < 60; tick++)
        {
            for (var i = 0; i < 100; i++)
            {
                var actualMs = 10.0;
                var expected = baseline.GetExpectedMs("GET", "/img/{static}");
                sensor.RecordUpstreamDeviation(actualMs / expected);
            }
            for (var i = 0; i < 50; i++)
            {
                var actualMs = 110.0;
                var expected = baseline.GetExpectedMs("GET", "/dashboard/entity/{slug}");
                sensor.RecordUpstreamDeviation(actualMs / expected);
            }
            sensor.TickOnce();
        }

        Assert.Equal(LoadBand.Low, sensor.CurrentBand);
    }

    [Fact]
    public void Genuine_systemwide_2x_slowdown_does_trip_high()
    {
        // Sanity check that the new axis still detects real pressure: every
        // endpoint runs at 2.5x its own p95 -> ratio averages 2.5 -> band
        // crosses HighRatio (2.0).
        var sensor = new PipelineLoadSensor(
            normalRps: 1e9, highRps: 1e9, criticalRps: 1e9,
            highRatio: 2.0, criticalRatio: 5.0,
            highStarvedTicks: int.MaxValue, criticalStarvedTicks: int.MaxValue,
            highGen2PerSec: 1e9, criticalGen2PerSec: 1e9);
        var baseline = new FakeBaseline();

        for (var tick = 0; tick < 60; tick++)
        {
            for (var i = 0; i < 100; i++)
            {
                sensor.RecordUpstreamDeviation(25.0 / baseline.GetExpectedMs("GET", "/img/{static}"));
            }
            for (var i = 0; i < 50; i++)
            {
                sensor.RecordUpstreamDeviation(275.0 / baseline.GetExpectedMs("GET", "/dashboard/entity/{slug}"));
            }
            sensor.TickOnce();
        }

        Assert.NotEqual(LoadBand.Low, sensor.CurrentBand);
        Assert.NotEqual(LoadBand.Normal, sensor.CurrentBand);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~StagingMixedWorkloadShedTests"
```

Expected: 2 passed, 0 failed. (This is a post-fix regression test; with the new sensor wiring in place the tests should pass on the first run.)

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.Test/Services/StagingMixedWorkloadShedTests.cs
git commit -m "test(load-shed): pin the 2026-06-25 staging mixed-workload regression"
```

---

## Task 10: Contract test for the never-shed-humans guarantee

**Files:**
- Create: `src/Mostlylucid.BotDetection.Test/Services/HumansNeverShedUnderCriticalTests.cs`

**Interfaces:**
- Consumes: `LoadShedDecision`, `VisitorClass`, `LoadShedOptions` (Tasks 1, 2, 7), `ILoadBandSource` (existing).
- Produces: a contract test that drives `LoadShedDecision` to Critical via a fake band source and verifies the human-protection guarantee holds across many requests.

- [ ] **Step 1: Write the failing test**

`src/Mostlylucid.BotDetection.Test/Services/HumansNeverShedUnderCriticalTests.cs`:

```csharp
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Contract pin: under sustained Critical pressure (any axis tripping),
///     a verified human visitor never sees a 503. Verified bots always get
///     shed. Unknown visitors shed at the configured fraction.
/// </summary>
public sealed class HumansNeverShedUnderCriticalTests
{
    private sealed class CriticalBandSource : ILoadBandSource
    {
        public LoadBand CurrentBand => LoadBand.Critical;
    }

    private static LoadShedDecision NewDecision() => new(new CriticalBandSource());

    [Fact]
    public void Human_class_passes_every_single_seed_at_critical()
    {
        var decision = NewDecision();
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 10_000; seed++)
            Assert.False(decision.ShouldShed(VisitorClass.Human, opts, requestSeed: seed));
    }

    [Fact]
    public void Bot_class_is_shed_every_single_seed_at_critical()
    {
        var decision = NewDecision();
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 10_000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Bot, opts, requestSeed: seed));
    }

    [Fact]
    public void Unknown_class_shed_fraction_matches_UnknownShedAtCritical()
    {
        var decision = NewDecision();
        var opts = new LoadShedOptions { UnknownShedAtCritical = 0.7 };
        var shed = 0;
        const int n = 10_000;
        for (var seed = 0; seed < n; seed++)
            if (decision.ShouldShed(VisitorClass.Unknown, opts, requestSeed: seed)) shed++;
        var observed = shed / (double)n;
        Assert.InRange(observed, 0.67, 0.73);
    }

    [Fact]
    public void Operator_override_can_shed_humans_at_critical()
    {
        // Confirms the gate is configurable, not hardcoded.
        var decision = NewDecision();
        var opts = new LoadShedOptions { HumanShedAtCritical = 0.5 };
        var shed = 0;
        const int n = 10_000;
        for (var seed = 0; seed < n; seed++)
            if (decision.ShouldShed(VisitorClass.Human, opts, requestSeed: seed)) shed++;
        var observed = shed / (double)n;
        Assert.InRange(observed, 0.47, 0.53);
    }
}
```

- [ ] **Step 2: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj --filter "FullyQualifiedName~HumansNeverShedUnderCriticalTests"
```

Expected: 4 passed, 0 failed.

- [ ] **Step 3: Run the full FOSS test suite to confirm no regressions**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/Mostlylucid.BotDetection.Test.csproj
```

Expected: all tests pass. Failures here indicate downstream consumers of the removed `ShedHint` API or the changed `LoadShedDecision.ShouldShed` signature; fix them in this commit before landing the suite.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.Test/Services/HumansNeverShedUnderCriticalTests.cs
git commit -m "test(load-shed): contract pin for never-shed-humans-by-default guarantee"
```

---

## After all tasks

- [ ] Push both repos to origin/main: `git -C /Users/scottgalloway/RiderProjects/stylobot push origin main`. (No commercial-repo changes; that push is unnecessary for this work.)
- [ ] Trigger Maxo build (`build-stack.ps1`) and follow the staged-deploy-flow memory for staging verification.
- [ ] Manual verification against the original 2026-06-25 staging bug repro: navigate `/dashboard/entity/{id}` in a browser, watch for any `x-stylobot-shed: 1` headers across 50 consecutive requests; expected zero shed events.
- [ ] Capture the deploy + verification outcome in a follow-up commit message on the FOSS repo.