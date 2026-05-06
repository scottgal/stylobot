# Reaction Packs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Reaction Packs: YAML-defined adaptive protection modes that activate automatically when StyloBot observes upstream degradation signals (5xx errors, 429 rate-limits, latency spikes), applying stepped action policies with hysteresis-based escalation and de-escalation.

**Architecture:** A `DegradationAtom` records upstream response codes and latency into EMA-based rolling windows and exposes signal values. A `ReactionPackEngine` background service loads YAML-defined packs, evaluates hysteresis conditions every 5 seconds, and advances/retreats step levels, writing transitions to SQLite. `IReactionPackContext` is a zero-allocation interface queried by `BotDetectionMiddleware` to override the active action policy when a pack is active. Signal groups are a reusable YAML primitive that expand to lists of signal keys, usable in any YAML that references signals.

**Tech Stack:** .NET 10, xUnit, Microsoft.Data.Sqlite, YamlDotNet, NullLogger (tests), HTMX + ApexCharts (dashboard)

**Spec:** `docs/superpowers/specs/2026-05-06-reaction-packs-design.md`

---

## File Map

### New files

| File | Responsibility |
|------|---------------|
| `src/Mostlylucid.BotDetection/Models/SignalGroupDefinition.cs` | YAML deserialization model for a named signal group |
| `src/Mostlylucid.BotDetection/Services/ISignalGroupRegistry.cs` | Interface: resolves `$group-name` to `string[]` of signal keys |
| `src/Mostlylucid.BotDetection/Services/SignalGroupRegistry.cs` | Loads `*.signal-group.yaml` embedded resources; exposes group resolution |
| `src/Mostlylucid.BotDetection/Definitions/SignalGroups/upstream-health.signal-group.yaml` | Built-in group: error_rate_5xx, rate_429, latency_p95 |
| `src/Mostlylucid.BotDetection/Definitions/SignalGroups/checkout-health.signal-group.yaml` | Built-in group: endpoint-scoped versions of the above |
| `src/Mostlylucid.BotDetection/Services/DegradationAtom.cs` | EMA rolling windows per signal key; `RecordResponse(status, latencyMs, path)` |
| `src/Mostlylucid.BotDetection/Models/ReactionPackDefinition.cs` | YAML deserialization root model |
| `src/Mostlylucid.BotDetection/Models/ReactionPackStep.cs` | Per-step model: level, name, policy, activate, deactivate |
| `src/Mostlylucid.BotDetection/Models/ReactionConditionSet.cs` | `condition: any/all` + list of `ReactionRule` |
| `src/Mostlylucid.BotDetection/Models/ReactionRule.cs` | Single rule: signal/signal_group, above/below, for_seconds |
| `src/Mostlylucid.BotDetection/Definitions/ReactionPacks/error-spike-protection.reaction-pack.yaml` | Built-in pack: 5xx + 429 global |
| `src/Mostlylucid.BotDetection/Definitions/ReactionPacks/latency-protection.reaction-pack.yaml` | Built-in pack: p95 latency global |
| `src/Mostlylucid.BotDetection/Definitions/ReactionPacks/checkout-protection.reaction-pack.yaml` | Built-in pack: endpoint-scoped 429 + 5xx |
| `src/Mostlylucid.BotDetection/Services/HysteresisTracker.cs` | Tracks per-rule first-true timestamps; decides when `for_seconds` is satisfied |
| `src/Mostlylucid.BotDetection/Services/ReactionRuleEvaluator.cs` | Evaluates a `ReactionConditionSet` against current signal values using `HysteresisTracker` |
| `src/Mostlylucid.BotDetection/Services/IReactionPackContext.cs` | `string? GetOverridePolicy(string endpoint, string? currentPolicy)` |
| `src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs` | Thread-safe implementation; holds current active pack+level per scope |
| `src/Mostlylucid.BotDetection/Services/ReactionPackEngine.cs` | Background service; loads packs, drives evaluation timer, updates `ReactionPackContext` |
| `src/Mostlylucid.BotDetection/Data/ReactionPackTransitionStore.cs` | SQLite persistence for transition events |
| `src/Mostlylucid.BotDetection.Test/Services/DegradationAtomTests.cs` | Unit tests |
| `src/Mostlylucid.BotDetection.Test/Services/SignalGroupRegistryTests.cs` | Unit tests |
| `src/Mostlylucid.BotDetection.Test/Services/HysteresisTrackerTests.cs` | Unit tests |
| `src/Mostlylucid.BotDetection.Test/Services/ReactionRuleEvaluatorTests.cs` | Unit tests |
| `src/Mostlylucid.BotDetection.Test/Services/ReactionPackEngineTests.cs` | Unit tests |
| `src/Mostlylucid.BotDetection.Test/Services/ReactionPackTransitionStoreTests.cs` | Unit tests |

### Modified files

| File | Change |
|------|--------|
| `src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj` | Add `<EmbeddedResource>` globs for `Definitions/SignalGroups/*.yaml` and `Definitions/ReactionPacks/*.yaml` |
| `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` | Add `response.*` signal key constants to the `SignalKeys` class |
| `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs` | Add optional `DegradationAtom?` + `IReactionPackContext?` constructor params; wrap `_next()` in try/finally for `RecordResponse`; override policy name in `HandlePostDetectionActionsAsync` |
| `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` | Register all new services |
| `src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs` | Add `reaction_pack_transitions` table to schema block |

---

## Task 1: Signal group models

**Files:**
- Create: `src/Mostlylucid.BotDetection/Models/SignalGroupDefinition.cs`
- Create: `src/Mostlylucid.BotDetection/Services/ISignalGroupRegistry.cs`
- Create: `src/Mostlylucid.BotDetection/Services/SignalGroupRegistry.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/SignalGroupRegistryTests.cs`

- [ ] **Step 1.1: Write the failing test**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/SignalGroupRegistryTests.cs
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class SignalGroupRegistryTests
{
    [Fact]
    public void Resolve_KnownGroup_ReturnsSignalKeys()
    {
        var groups = new List<SignalGroupDefinition>
        {
            new() { Name = "upstream-health", Signals = ["response.error_rate_5xx", "response.rate_429"] }
        };
        var registry = new SignalGroupRegistry(groups);

        var keys = registry.Resolve("$upstream-health");

        Assert.Equal(new[] { "response.error_rate_5xx", "response.rate_429" }, keys);
    }

    [Fact]
    public void Resolve_UnknownGroup_ReturnsEmpty()
    {
        var registry = new SignalGroupRegistry([]);
        var keys = registry.Resolve("$nonexistent");
        Assert.Empty(keys);
    }

    [Fact]
    public void Resolve_NonGroupReference_ReturnsEmpty()
    {
        var registry = new SignalGroupRegistry([]);
        var keys = registry.Resolve("response.error_rate_5xx");
        Assert.Empty(keys);
    }

    [Fact]
    public void TryGetGroup_ExistingName_ReturnsTrue()
    {
        var groups = new List<SignalGroupDefinition>
        {
            new() { Name = "test-group", Signals = ["a", "b"] }
        };
        var registry = new SignalGroupRegistry(groups);

        var found = registry.TryGetGroup("test-group", out var signals);

        Assert.True(found);
        Assert.Equal(new[] { "a", "b" }, signals);
    }

    [Fact]
    public void TryGetGroup_MissingName_ReturnsFalse()
    {
        var registry = new SignalGroupRegistry([]);
        var found = registry.TryGetGroup("missing", out _);
        Assert.False(found);
    }
}
```

- [ ] **Step 1.2: Run test to verify it fails**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~SignalGroupRegistryTests" 2>&1 | tail -20
```
Expected: FAIL with type-not-found errors.

- [ ] **Step 1.3: Create `SignalGroupDefinition`**

```csharp
// src/Mostlylucid.BotDetection/Models/SignalGroupDefinition.cs
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class SignalGroupDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "signals")]
    public List<string> Signals { get; set; } = [];
}
```

- [ ] **Step 1.4: Create `ISignalGroupRegistry`**

```csharp
// src/Mostlylucid.BotDetection/Services/ISignalGroupRegistry.cs
namespace Mostlylucid.BotDetection.Services;

public interface ISignalGroupRegistry
{
    /// <summary>
    /// Resolves a "$group-name" reference to its signal keys.
    /// Returns empty if the argument does not start with '$' or the group is not found.
    /// </summary>
    IReadOnlyList<string> Resolve(string groupReference);

    bool TryGetGroup(string groupName, out IReadOnlyList<string> signals);
}
```

- [ ] **Step 1.5: Create `SignalGroupRegistry`**

```csharp
// src/Mostlylucid.BotDetection/Services/SignalGroupRegistry.cs
using System.Collections.Frozen;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

public sealed class SignalGroupRegistry : ISignalGroupRegistry
{
    private readonly FrozenDictionary<string, IReadOnlyList<string>> _groups;

    public SignalGroupRegistry(IEnumerable<SignalGroupDefinition> definitions)
    {
        _groups = definitions
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .ToFrozenDictionary(
                d => d.Name,
                d => (IReadOnlyList<string>)d.Signals.AsReadOnly(),
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Resolve(string groupReference)
    {
        if (!groupReference.StartsWith('$'))
            return [];

        var name = groupReference[1..];
        return _groups.TryGetValue(name, out var signals) ? signals : [];
    }

    public bool TryGetGroup(string groupName, out IReadOnlyList<string> signals)
    {
        if (_groups.TryGetValue(groupName, out var found))
        {
            signals = found;
            return true;
        }
        signals = [];
        return false;
    }
}
```

- [ ] **Step 1.6: Run tests to verify they pass**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~SignalGroupRegistryTests" 2>&1 | tail -10
```
Expected: 5 passed.

- [ ] **Step 1.7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/SignalGroupDefinition.cs \
        src/Mostlylucid.BotDetection/Services/ISignalGroupRegistry.cs \
        src/Mostlylucid.BotDetection/Services/SignalGroupRegistry.cs \
        src/Mostlylucid.BotDetection.Test/Services/SignalGroupRegistryTests.cs
git commit -m "feat(reaction-packs): signal group model and registry"
```

---

## Task 2: Signal group YAML files

**Files:**
- Create: `src/Mostlylucid.BotDetection/Definitions/SignalGroups/upstream-health.signal-group.yaml`
- Create: `src/Mostlylucid.BotDetection/Definitions/SignalGroups/checkout-health.signal-group.yaml`
- Modify: `src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj`

- [ ] **Step 2.1: Create the upstream-health group YAML**

```yaml
# src/Mostlylucid.BotDetection/Definitions/SignalGroups/upstream-health.signal-group.yaml
name: upstream-health
description: Core upstream response health signals
signals:
  - response.error_rate_5xx
  - response.rate_429
  - response.latency_p95
```

- [ ] **Step 2.2: Create the checkout-health group YAML**

```yaml
# src/Mostlylucid.BotDetection/Definitions/SignalGroups/checkout-health.signal-group.yaml
name: checkout-health
description: Checkout endpoint health signals (endpoint-scoped)
signals:
  - response.error_rate_5xx:/api/checkout
  - response.rate_429:/api/checkout
  - response.latency_p95:/api/checkout
```

- [ ] **Step 2.3: Add embedded resource globs to `.csproj`**

In `src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj`, find the `<ItemGroup>` block with the other `<EmbeddedResource>` lines (around line 98) and add after the last existing entry:

```xml
<EmbeddedResource Include="Definitions\SignalGroups\*.yaml" />
<EmbeddedResource Include="Definitions\ReactionPacks\*.yaml" />
```

- [ ] **Step 2.4: Write a test that proves the embedded resources are found**

Add to `src/Mostlylucid.BotDetection.Test/Services/SignalGroupRegistryTests.cs`:

```csharp
[Fact]
public void LoadFromEmbeddedResources_FindsBuiltInGroups()
{
    var assembly = typeof(SignalGroupRegistry).Assembly;
    var resourceNames = assembly.GetManifestResourceNames()
        .Where(n => n.Contains("SignalGroups") && n.EndsWith(".yaml"))
        .ToList();

    Assert.NotEmpty(resourceNames);
    Assert.Contains(resourceNames, r => r.Contains("upstream-health"));
    Assert.Contains(resourceNames, r => r.Contains("checkout-health"));
}
```

- [ ] **Step 2.5: Run test**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~SignalGroupRegistryTests.LoadFromEmbeddedResources" 2>&1 | tail -10
```
Expected: 1 passed.

- [ ] **Step 2.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Definitions/SignalGroups/ \
        src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj \
        src/Mostlylucid.BotDetection.Test/Services/SignalGroupRegistryTests.cs
git commit -m "feat(reaction-packs): built-in signal group YAML files"
```

---

## Task 3: Signal key constants and DegradationAtom

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs`
- Create: `src/Mostlylucid.BotDetection/Services/DegradationAtom.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/DegradationAtomTests.cs`

- [ ] **Step 3.1: Write failing tests**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/DegradationAtomTests.cs
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class DegradationAtomTests : IDisposable
{
    private readonly DegradationAtom _atom;

    public DegradationAtomTests()
    {
        _atom = new DegradationAtom(windowSeconds: 60, emaAlpha: 0.5);
    }

    public void Dispose() => _atom.Dispose();

    [Fact]
    public void GetSignalValue_NoRequests_ReturnsZero()
    {
        Assert.Equal(0.0, _atom.GetSignalValue("response.error_rate_5xx"), precision: 6);
    }

    [Fact]
    public void RecordResponse_500_IncrementsErrorRate()
    {
        _atom.RecordResponse(500, 50, "/api/test");
        _atom.RecordResponse(200, 50, "/api/test");

        var rate = _atom.GetSignalValue("response.error_rate_5xx");
        Assert.True(rate > 0, $"Expected error rate > 0, got {rate}");
    }

    [Fact]
    public void RecordResponse_429_IncrementsWith429Rate()
    {
        _atom.RecordResponse(429, 50, "/api/test");

        var rate = _atom.GetSignalValue("response.rate_429");
        Assert.True(rate > 0, $"Expected 429 rate > 0, got {rate}");
    }

    [Fact]
    public void RecordResponse_UpdatesEndpointScopedSignal()
    {
        _atom.RecordResponse(500, 50, "/api/checkout");

        var globalRate = _atom.GetSignalValue("response.error_rate_5xx");
        var scopedRate = _atom.GetSignalValue("response.error_rate_5xx:/api/checkout");

        Assert.True(globalRate > 0);
        Assert.True(scopedRate > 0);
    }

    [Fact]
    public void RecordResponse_200_DoesNotIncrementErrorRates()
    {
        _atom.RecordResponse(200, 50, "/api/test");
        _atom.RecordResponse(200, 50, "/api/test");

        Assert.Equal(0.0, _atom.GetSignalValue("response.error_rate_5xx"), precision: 6);
        Assert.Equal(0.0, _atom.GetSignalValue("response.rate_429"), precision: 6);
    }

    [Fact]
    public void RecordResponse_LatencyTracked()
    {
        _atom.RecordResponse(200, 1500, "/api/test");

        var latency = _atom.GetSignalValue("response.latency_p95");
        Assert.True(latency > 0, $"Expected latency > 0, got {latency}");
    }

    [Fact]
    public void GetAvailableSignalKeys_IncludesBuiltInKeys()
    {
        var keys = _atom.GetAvailableSignalKeys();
        Assert.Contains("response.error_rate_5xx", keys);
        Assert.Contains("response.rate_429", keys);
        Assert.Contains("response.latency_p95", keys);
    }
}
```

- [ ] **Step 3.2: Run to verify fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~DegradationAtomTests" 2>&1 | tail -10
```
Expected: FAIL with type-not-found.

- [ ] **Step 3.3: Add signal key constants to `DetectionContext.cs`**

Find the `SignalKeys` static class inside `DetectionContext.cs` and add a new section at the end:

```csharp
// ---- Degradation / Upstream Response Signals ----
public const string ResponseErrorRate5Xx = "response.error_rate_5xx";
public const string ResponseRate429 = "response.rate_429";
public const string ResponseLatencyP95 = "response.latency_p95";
```

- [ ] **Step 3.4: Create `DegradationAtom`**

```csharp
// src/Mostlylucid.BotDetection/Services/DegradationAtom.cs
using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
/// Observes upstream response codes and latency from the middleware finally block.
/// Maintains EMA-based error rates and a running latency approximation per signal key.
/// Signal keys follow "response.{metric}" (global) or "response.{metric}:{path}" (endpoint-scoped).
/// </summary>
public sealed class DegradationAtom : IDisposable
{
    private const string GlobalErrorRate5Xx = "response.error_rate_5xx";
    private const string GlobalRate429 = "response.rate_429";
    private const string GlobalLatencyP95 = "response.latency_p95";

    private readonly double _alpha;
    private readonly double _decayFactor;
    private readonly ConcurrentDictionary<string, double> _emaValues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _latencyEma = new(StringComparer.Ordinal);
    private readonly Timer _decayTimer;

    public DegradationAtom(double windowSeconds = 60.0, double emaAlpha = 0.3)
    {
        _alpha = emaAlpha;
        _decayFactor = 1.0 - (emaAlpha * (1.0 / Math.Max(1.0, windowSeconds)));
        _decayTimer = new Timer(Decay, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Called from the middleware finally block after each request completes.
    /// Lock-free EMA update on the hot path.
    /// </summary>
    public void RecordResponse(int statusCode, long latencyMs, string path)
    {
        var is5Xx = statusCode >= 500 && statusCode < 600;
        var is429 = statusCode == 429;

        UpdateEma(GlobalErrorRate5Xx, is5Xx ? 1.0 : 0.0);
        UpdateEma(GlobalRate429, is429 ? 1.0 : 0.0);
        UpdateLatencyEma(GlobalLatencyP95, latencyMs);

        if (!string.IsNullOrEmpty(path) && path != "/")
        {
            UpdateEma($"{GlobalErrorRate5Xx}:{path}", is5Xx ? 1.0 : 0.0);
            UpdateEma($"{GlobalRate429}:{path}", is429 ? 1.0 : 0.0);
            UpdateLatencyEma($"{GlobalLatencyP95}:{path}", latencyMs);
        }
    }

    /// <summary>Returns the current EMA value for a signal key. Returns 0.0 if never seen.</summary>
    public double GetSignalValue(string signalKey)
    {
        if (_emaValues.TryGetValue(signalKey, out var rate))
            return rate;
        if (_latencyEma.TryGetValue(signalKey, out var latency))
            return latency;
        return 0.0;
    }

    public IReadOnlyList<string> GetAvailableSignalKeys()
    {
        var keys = new List<string> { GlobalErrorRate5Xx, GlobalRate429, GlobalLatencyP95 };
        keys.AddRange(_emaValues.Keys.Where(k => k != GlobalErrorRate5Xx && k != GlobalRate429));
        keys.AddRange(_latencyEma.Keys.Where(k => k != GlobalLatencyP95));
        return keys.Distinct().ToList();
    }

    public void Dispose() => _decayTimer.Dispose();

    private void UpdateEma(string key, double sample)
    {
        _emaValues.AddOrUpdate(key, sample, (_, prev) => _alpha * sample + (1.0 - _alpha) * prev);
    }

    private void UpdateLatencyEma(string key, long latencyMs)
    {
        _latencyEma.AddOrUpdate(key, latencyMs, (_, prev) => _alpha * latencyMs + (1.0 - _alpha) * prev);
    }

    private void Decay(object? _)
    {
        foreach (var key in _emaValues.Keys.ToList())
            _emaValues.AddOrUpdate(key, 0.0, (_, prev) => prev * _decayFactor);
        foreach (var key in _latencyEma.Keys.ToList())
            _latencyEma.AddOrUpdate(key, 0.0, (_, prev) => prev * _decayFactor);
    }
}
```

- [ ] **Step 3.5: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~DegradationAtomTests" 2>&1 | tail -10
```
Expected: all pass.

- [ ] **Step 3.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/DetectionContext.cs \
        src/Mostlylucid.BotDetection/Services/DegradationAtom.cs \
        src/Mostlylucid.BotDetection.Test/Services/DegradationAtomTests.cs
git commit -m "feat(reaction-packs): DegradationAtom rolling window signal emitter"
```

---

## Task 4: Reaction pack YAML models

**Files:**
- Create: `src/Mostlylucid.BotDetection/Models/ReactionPackDefinition.cs`
- Create: `src/Mostlylucid.BotDetection/Models/ReactionPackStep.cs`
- Create: `src/Mostlylucid.BotDetection/Models/ReactionConditionSet.cs`
- Create: `src/Mostlylucid.BotDetection/Models/ReactionRule.cs`

- [ ] **Step 4.1: Create `ReactionRule`**

```csharp
// src/Mostlylucid.BotDetection/Models/ReactionRule.cs
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionRule
{
    [YamlMember(Alias = "signal")]
    public string? Signal { get; set; }

    [YamlMember(Alias = "signal_group")]
    public string? SignalGroup { get; set; }

    [YamlMember(Alias = "above")]
    public double? Above { get; set; }

    [YamlMember(Alias = "below")]
    public double? Below { get; set; }

    [YamlMember(Alias = "for_seconds")]
    public double ForSeconds { get; set; } = 60.0;

    /// <summary>For signal_group rules: whether any or all signals must meet the threshold. Defaults to "any".</summary>
    [YamlMember(Alias = "group_condition")]
    public string GroupCondition { get; set; } = "any";
}
```

- [ ] **Step 4.2: Create `ReactionConditionSet`**

```csharp
// src/Mostlylucid.BotDetection/Models/ReactionConditionSet.cs
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionConditionSet
{
    [YamlMember(Alias = "condition")]
    public string Condition { get; set; } = "any";

    [YamlMember(Alias = "rules")]
    public List<ReactionRule> Rules { get; set; } = [];

    public bool IsAny => string.Equals(Condition, "any", StringComparison.OrdinalIgnoreCase);
    public bool IsAll => !IsAny;
}
```

- [ ] **Step 4.3: Create `ReactionPackStep`**

```csharp
// src/Mostlylucid.BotDetection/Models/ReactionPackStep.cs
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionPackStep
{
    [YamlMember(Alias = "level")]
    public int Level { get; set; }

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "policy")]
    public string Policy { get; set; } = string.Empty;

    [YamlMember(Alias = "activate")]
    public ReactionConditionSet? Activate { get; set; }

    [YamlMember(Alias = "deactivate")]
    public ReactionConditionSet? Deactivate { get; set; }
}
```

- [ ] **Step 4.4: Create `ReactionPackDefinition`**

```csharp
// src/Mostlylucid.BotDetection/Models/ReactionPackDefinition.cs
using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionPackDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "scope")]
    public string Scope { get; set; } = "global";

    [YamlMember(Alias = "priority")]
    public int Priority { get; set; }

    [YamlMember(Alias = "signals")]
    public List<string> Signals { get; set; } = [];

    [YamlMember(Alias = "steps")]
    public List<ReactionPackStep> Steps { get; set; } = [];

    public bool IsGlobal => string.Equals(Scope, "global", StringComparison.OrdinalIgnoreCase);

    /// <summary>Endpoint path when scope is "endpoint:/some/path". Returns null for global scope.</summary>
    public string? ScopedEndpoint => IsGlobal ? null : (Scope.Length > 9 ? Scope[9..] : null);
}
```

- [ ] **Step 4.5: Build to verify no compilation errors**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -10
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 4.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/ReactionRule.cs \
        src/Mostlylucid.BotDetection/Models/ReactionConditionSet.cs \
        src/Mostlylucid.BotDetection/Models/ReactionPackStep.cs \
        src/Mostlylucid.BotDetection/Models/ReactionPackDefinition.cs
git commit -m "feat(reaction-packs): YAML deserialization models"
```

---

## Task 5: Built-in reaction pack YAML files

**Files:**
- Create: `src/Mostlylucid.BotDetection/Definitions/ReactionPacks/error-spike-protection.reaction-pack.yaml`
- Create: `src/Mostlylucid.BotDetection/Definitions/ReactionPacks/latency-protection.reaction-pack.yaml`
- Create: `src/Mostlylucid.BotDetection/Definitions/ReactionPacks/checkout-protection.reaction-pack.yaml`

- [ ] **Step 5.1: Create `error-spike-protection.reaction-pack.yaml`**

```yaml
name: error-spike-protection
description: Activates when upstream 5xx error rate or 429 rate spikes globally
enabled: true
scope: global
priority: 0

signals:
  - $upstream-health

steps:
  - level: 1
    name: watch
    activate:
      condition: any
      rules:
        - signal: response.error_rate_5xx
          above: 0.05
          for_seconds: 60
        - signal: response.rate_429
          above: 0.03
          for_seconds: 30
    policy: throttle-gentle
    deactivate:
      condition: all
      rules:
        - signal: response.error_rate_5xx
          below: 0.02
          for_seconds: 120
        - signal: response.rate_429
          below: 0.01
          for_seconds: 120

  - level: 2
    name: protect
    activate:
      condition: any
      rules:
        - signal: response.error_rate_5xx
          above: 0.15
          for_seconds: 30
        - signal: response.rate_429
          above: 0.10
          for_seconds: 20
    policy: throttle-aggressive
    deactivate:
      condition: all
      rules:
        - signal: response.error_rate_5xx
          below: 0.05
          for_seconds: 180

  - level: 3
    name: critical
    activate:
      condition: any
      rules:
        - signal: response.error_rate_5xx
          above: 0.30
          for_seconds: 15
    policy: block-soft
    deactivate:
      condition: all
      rules:
        - signal: response.error_rate_5xx
          below: 0.10
          for_seconds: 300
```

- [ ] **Step 5.2: Create `latency-protection.reaction-pack.yaml`**

```yaml
name: latency-protection
description: Activates when upstream p95 latency is elevated
enabled: true
scope: global
priority: 0

signals:
  - response.latency_p95

steps:
  - level: 1
    name: watch
    activate:
      condition: any
      rules:
        - signal: response.latency_p95
          above: 500
          for_seconds: 60
    policy: throttle-gentle
    deactivate:
      condition: all
      rules:
        - signal: response.latency_p95
          below: 300
          for_seconds: 120

  - level: 2
    name: protect
    activate:
      condition: any
      rules:
        - signal: response.latency_p95
          above: 2000
          for_seconds: 30
    policy: throttle-moderate
    deactivate:
      condition: all
      rules:
        - signal: response.latency_p95
          below: 800
          for_seconds: 180
```

- [ ] **Step 5.3: Create `checkout-protection.reaction-pack.yaml`**

```yaml
name: checkout-protection
description: Activates when the /api/checkout endpoint shows elevated error or 429 rate
enabled: true
scope: endpoint:/api/checkout
priority: 10

signals:
  - $checkout-health

steps:
  - level: 1
    name: protect
    activate:
      condition: any
      rules:
        - signal: response.rate_429:/api/checkout
          above: 0.10
          for_seconds: 30
        - signal: response.error_rate_5xx:/api/checkout
          above: 0.10
          for_seconds: 30
    policy: challenge-pow
    deactivate:
      condition: all
      rules:
        - signal: response.rate_429:/api/checkout
          below: 0.03
          for_seconds: 180
        - signal: response.error_rate_5xx:/api/checkout
          below: 0.03
          for_seconds: 180

  - level: 2
    name: critical
    activate:
      condition: any
      rules:
        - signal: response.error_rate_5xx:/api/checkout
          above: 0.30
          for_seconds: 15
    policy: block-soft
    deactivate:
      condition: all
      rules:
        - signal: response.error_rate_5xx:/api/checkout
          below: 0.10
          for_seconds: 300
```

- [ ] **Step 5.4: Write a loading test for all three packs**

Create `src/Mostlylucid.BotDetection.Test/Services/ReactionPackLoadingTests.cs`:

```csharp
using Mostlylucid.BotDetection.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackLoadingTests
{
    private static ReactionPackDefinition LoadPack(string resourceFragment)
    {
        var assembly = typeof(ReactionPackDefinition).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            assembly.GetManifestResourceNames().Single(n => n.Contains(resourceFragment)));
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<ReactionPackDefinition>(reader.ReadToEnd());
    }

    [Fact]
    public void ErrorSpikeProtectionPack_DeserializesCorrectly()
    {
        var pack = LoadPack("error-spike-protection");

        Assert.Equal("error-spike-protection", pack.Name);
        Assert.True(pack.IsGlobal);
        Assert.Equal(3, pack.Steps.Count);
        Assert.All(pack.Steps, s => Assert.False(string.IsNullOrEmpty(s.Policy)));
        Assert.Equal("throttle-gentle", pack.Steps[0].Policy);
        Assert.Equal("block-soft", pack.Steps[2].Policy);
    }

    [Fact]
    public void LatencyProtectionPack_DeserializesCorrectly()
    {
        var pack = LoadPack("latency-protection");

        Assert.Equal("latency-protection", pack.Name);
        Assert.True(pack.IsGlobal);
        Assert.Equal(2, pack.Steps.Count);
    }

    [Fact]
    public void CheckoutProtectionPack_HasEndpointScope()
    {
        var pack = LoadPack("checkout-protection");

        Assert.False(pack.IsGlobal);
        Assert.Equal("/api/checkout", pack.ScopedEndpoint);
        Assert.Equal(10, pack.Priority);
        Assert.Equal("challenge-pow", pack.Steps[0].Policy);
    }
}
```

- [ ] **Step 5.5: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ReactionPackLoadingTests" 2>&1 | tail -10
```
Expected: 3 passed.

- [ ] **Step 5.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Definitions/ReactionPacks/ \
        src/Mostlylucid.BotDetection.Test/Services/ReactionPackLoadingTests.cs
git commit -m "feat(reaction-packs): built-in reaction pack YAML files"
```

---

## Task 6: Hysteresis evaluator

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/HysteresisTracker.cs`
- Create: `src/Mostlylucid.BotDetection/Services/ReactionRuleEvaluator.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/HysteresisTrackerTests.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/ReactionRuleEvaluatorTests.cs`

- [ ] **Step 6.1: Write failing tests for HysteresisTracker**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/HysteresisTrackerTests.cs
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class HysteresisTrackerTests
{
    [Fact]
    public void IsSatisfied_ConditionNotYetTrue_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: false, forSeconds: 30.0));
    }

    [Fact]
    public void IsSatisfied_ConditionJustBecameTrue_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }

    [Fact]
    public void IsSatisfied_ConditionTrueForLongEnough_ReturnsTrue()
    {
        var tracker = new HysteresisTracker();
        tracker.ForceFirstTrue("rule-1", DateTime.UtcNow.AddSeconds(-35));

        Assert.True(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }

    [Fact]
    public void IsSatisfied_ConditionFalseAfterBeingTrue_ResetsTimer()
    {
        var tracker = new HysteresisTracker();
        tracker.ForceFirstTrue("rule-1", DateTime.UtcNow.AddSeconds(-35));
        Assert.True(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));

        // Condition drops to false - timer resets
        tracker.IsSatisfied("rule-1", conditionTrue: false, forSeconds: 30.0);

        // Check again with condition true - timer just reset so not yet satisfied
        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }

    [Fact]
    public void Reset_ClearsAllTimers()
    {
        var tracker = new HysteresisTracker();
        tracker.ForceFirstTrue("rule-1", DateTime.UtcNow.AddSeconds(-35));
        tracker.Reset();

        Assert.False(tracker.IsSatisfied("rule-1", conditionTrue: true, forSeconds: 30.0));
    }
}
```

- [ ] **Step 6.2: Write failing tests for ReactionRuleEvaluator**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/ReactionRuleEvaluatorTests.cs
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionRuleEvaluatorTests
{
    private static SignalGroupRegistry EmptyRegistry() => new SignalGroupRegistry([]);

    private static Dictionary<string, double> Signals(params (string key, double val)[] pairs)
        => pairs.ToDictionary(p => p.key, p => p.val);

    [Fact]
    public void Evaluate_AboveRule_TimerJustStarted_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "any",
            Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 60.0 }]
        };

        var result = evaluator.Evaluate(conditionSet, Signals(("response.error_rate_5xx", 0.10)), tracker, "test:activate");
        Assert.False(result);
    }

    [Fact]
    public void Evaluate_AboveRule_BelowThreshold_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "any",
            Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 60.0 }]
        };
        tracker.ForceFirstTrue("test:activate:0", DateTime.UtcNow.AddSeconds(-70));

        // Signal is below threshold so condition is false; hysteresis irrelevant
        var result = evaluator.Evaluate(conditionSet, Signals(("response.error_rate_5xx", 0.01)), tracker, "test:activate");
        Assert.False(result);
    }

    [Fact]
    public void Evaluate_AboveRule_SatisfiedAfterHysteresis_ReturnsTrue()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "any",
            Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 60.0 }]
        };
        tracker.ForceFirstTrue("test:activate:0", DateTime.UtcNow.AddSeconds(-70));

        var result = evaluator.Evaluate(conditionSet, Signals(("response.error_rate_5xx", 0.10)), tracker, "test:activate");
        Assert.True(result);
    }

    [Fact]
    public void Evaluate_AllCondition_OneRuleNotSatisfied_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "all",
            Rules =
            [
                new ReactionRule { Signal = "response.error_rate_5xx", Below = 0.02, ForSeconds = 30.0 },
                new ReactionRule { Signal = "response.rate_429", Below = 0.01, ForSeconds = 30.0 }
            ]
        };
        tracker.ForceFirstTrue("test:deactivate:0", DateTime.UtcNow.AddSeconds(-35));
        // rate_429 is still above threshold so second rule not satisfied
        var result = evaluator.Evaluate(
            conditionSet,
            Signals(("response.error_rate_5xx", 0.01), ("response.rate_429", 0.05)),
            tracker, "test:deactivate");
        Assert.False(result);
    }

    [Fact]
    public void Evaluate_AllCondition_BothSatisfied_ReturnsTrue()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "all",
            Rules =
            [
                new ReactionRule { Signal = "response.error_rate_5xx", Below = 0.02, ForSeconds = 30.0 },
                new ReactionRule { Signal = "response.rate_429", Below = 0.01, ForSeconds = 30.0 }
            ]
        };
        tracker.ForceFirstTrue("test:deactivate:0", DateTime.UtcNow.AddSeconds(-35));
        tracker.ForceFirstTrue("test:deactivate:1", DateTime.UtcNow.AddSeconds(-35));
        var result = evaluator.Evaluate(
            conditionSet,
            Signals(("response.error_rate_5xx", 0.01), ("response.rate_429", 0.005)),
            tracker, "test:deactivate");
        Assert.True(result);
    }
}
```

- [ ] **Step 6.3: Run to verify fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~HysteresisTrackerTests|FullyQualifiedName~ReactionRuleEvaluatorTests" 2>&1 | tail -10
```

- [ ] **Step 6.4: Implement `HysteresisTracker`**

```csharp
// src/Mostlylucid.BotDetection/Services/HysteresisTracker.cs
using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
/// Tracks per-rule first-true timestamps for hysteresis evaluation.
/// A rule is "satisfied" only when it has been continuously true for at least forSeconds.
/// Resets the timer when the condition drops to false.
/// </summary>
public sealed class HysteresisTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _firstTrueAt = new(StringComparer.Ordinal);

    public bool IsSatisfied(string ruleKey, bool conditionTrue, double forSeconds)
    {
        if (!conditionTrue)
        {
            _firstTrueAt.TryRemove(ruleKey, out _);
            return false;
        }

        var now = DateTime.UtcNow;
        var firstTrue = _firstTrueAt.GetOrAdd(ruleKey, now);
        return (now - firstTrue).TotalSeconds >= forSeconds;
    }

    public void Reset() => _firstTrueAt.Clear();

    internal void ForceFirstTrue(string ruleKey, DateTime firstTrueAt)
    {
        _firstTrueAt[ruleKey] = firstTrueAt;
    }
}
```

- [ ] **Step 6.5: Implement `ReactionRuleEvaluator`**

```csharp
// src/Mostlylucid.BotDetection/Services/ReactionRuleEvaluator.cs
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
/// Evaluates a ReactionConditionSet against current signal values using a HysteresisTracker.
/// The trackingPrefix scopes hysteresis timers to a specific pack+step+side combination.
/// </summary>
public sealed class ReactionRuleEvaluator(ISignalGroupRegistry groupRegistry)
{
    public bool Evaluate(
        ReactionConditionSet conditionSet,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string trackingPrefix)
    {
        var results = conditionSet.Rules
            .Select((rule, i) => EvaluateRule(rule, signals, tracker, $"{trackingPrefix}:{i}"))
            .ToList();

        return conditionSet.IsAll ? results.All(r => r) : results.Any(r => r);
    }

    private bool EvaluateRule(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string ruleKey)
    {
        if (!string.IsNullOrEmpty(rule.SignalGroup))
            return EvaluateGroupRule(rule, signals, tracker, ruleKey);

        if (string.IsNullOrEmpty(rule.Signal))
            return false;

        var conditionMet = EvaluateThreshold(rule, signals, rule.Signal);
        return tracker.IsSatisfied(ruleKey, conditionMet, rule.ForSeconds);
    }

    private bool EvaluateGroupRule(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string ruleKey)
    {
        var groupSignals = groupRegistry.Resolve(rule.SignalGroup!);
        if (groupSignals.Count == 0)
            return false;

        var results = groupSignals.Select((sig, i) =>
        {
            var conditionMet = EvaluateThreshold(rule, signals, sig);
            return tracker.IsSatisfied($"{ruleKey}:grp{i}", conditionMet, rule.ForSeconds);
        }).ToList();

        return string.Equals(rule.GroupCondition, "all", StringComparison.OrdinalIgnoreCase)
            ? results.All(r => r)
            : results.Any(r => r);
    }

    private static bool EvaluateThreshold(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        string signalKey)
    {
        if (!signals.TryGetValue(signalKey, out var value))
            return false;

        if (rule.Above.HasValue && rule.Below.HasValue)
            return value > rule.Above.Value && value < rule.Below.Value;
        if (rule.Above.HasValue)
            return value > rule.Above.Value;
        if (rule.Below.HasValue)
            return value < rule.Below.Value;
        return false;
    }
}
```

- [ ] **Step 6.6: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~HysteresisTrackerTests|FullyQualifiedName~ReactionRuleEvaluatorTests" 2>&1 | tail -10
```
Expected: all pass.

- [ ] **Step 6.7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/HysteresisTracker.cs \
        src/Mostlylucid.BotDetection/Services/ReactionRuleEvaluator.cs \
        src/Mostlylucid.BotDetection.Test/Services/HysteresisTrackerTests.cs \
        src/Mostlylucid.BotDetection.Test/Services/ReactionRuleEvaluatorTests.cs
git commit -m "feat(reaction-packs): hysteresis tracker and rule evaluator"
```

---

## Task 7: ReactionPackEngine and IReactionPackContext

**Files:**
- Create: `src/Mostlylucid.BotDetection/Services/IReactionPackContext.cs`
- Create: `src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs`
- Create: `src/Mostlylucid.BotDetection/Services/ReactionPackEngine.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/ReactionPackEngineTests.cs`

- [ ] **Step 7.1: Write failing tests**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/ReactionPackEngineTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackEngineTests : IDisposable
{
    private readonly ReactionPackContext _context = new();
    private readonly DegradationAtom _atom = new(windowSeconds: 60, emaAlpha: 0.5);
    private readonly SignalGroupRegistry _groupRegistry = new([]);

    public void Dispose() => _atom.Dispose();

    private ReactionRuleEvaluator Evaluator() => new(_groupRegistry);

    private static ReactionPackDefinition ImmediatePack(string policyName = "throttle-gentle") =>
        new()
        {
            Name = "test-pack",
            Enabled = true,
            Scope = "global",
            Steps =
            [
                new ReactionPackStep
                {
                    Level = 1,
                    Name = "watch",
                    Policy = policyName,
                    Activate = new ReactionConditionSet
                    {
                        Condition = "any",
                        Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 0.0 }]
                    },
                    Deactivate = new ReactionConditionSet
                    {
                        Condition = "all",
                        Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Below = 0.02, ForSeconds = 0.0 }]
                    }
                }
            ]
        };

    [Fact]
    public void GetOverridePolicy_NoPack_ReturnsNull()
    {
        Assert.Null(_context.GetOverridePolicy("/api/test", "block"));
    }

    [Fact]
    public void GetOverridePolicy_ActiveGlobalPack_ReturnsPackPolicy()
    {
        _context.SetActiveLevel("test-pack", 1, "throttle-gentle", "global");
        Assert.Equal("throttle-gentle", _context.GetOverridePolicy("/api/anything", "block"));
    }

    [Fact]
    public void GetOverridePolicy_ActiveEndpointPack_OnlyMatchesEndpoint()
    {
        _context.SetActiveLevel("checkout-pack", 1, "challenge-pow", "/api/checkout");
        Assert.Equal("challenge-pow", _context.GetOverridePolicy("/api/checkout", "block"));
        Assert.Null(_context.GetOverridePolicy("/api/users", "block"));
    }

    [Fact]
    public void GetOverridePolicy_DeactivatedPack_ReturnsNull()
    {
        _context.SetActiveLevel("test-pack", 1, "throttle-gentle", "global");
        _context.Deactivate("test-pack");
        Assert.Null(_context.GetOverridePolicy("/api/test", "block"));
    }

    [Fact]
    public void GetOverridePolicy_ConflictingPacks_HigherPriorityWins()
    {
        _context.SetActiveLevel("low-priority", 1, "throttle-gentle", "global", priority: 0);
        _context.SetActiveLevel("high-priority", 1, "block-soft", "global", priority: 10);
        Assert.Equal("block-soft", _context.GetOverridePolicy("/api/test", "block"));
    }

    [Fact]
    public void EvaluatePack_EscalatesToLevel1_WhenConditionSatisfied()
    {
        var pack = ImmediatePack();
        var engine = new ReactionPackEngine(
            [pack], _atom, _context, Evaluator(),
            NullLogger<ReactionPackEngine>.Instance);

        // With emaAlpha=0.5, a single 500 gives value=0.5 which is >0.05
        _atom.RecordResponse(500, 50, "/test");
        engine.EvaluateNow();

        Assert.Equal("throttle-gentle", _context.GetOverridePolicy("/api/anything", null));
    }

    [Fact]
    public void EvaluatePack_DeescalatesWhenSignalDrops()
    {
        var pack = ImmediatePack();
        var engine = new ReactionPackEngine(
            [pack], _atom, _context, Evaluator(),
            NullLogger<ReactionPackEngine>.Instance);

        _atom.RecordResponse(500, 50, "/test");
        engine.EvaluateNow();
        Assert.NotNull(_context.GetOverridePolicy("/api/anything", null));

        // Flood with 200s; with alpha=0.5 after enough 200s the EMA drops below 0.02
        for (var i = 0; i < 10; i++) _atom.RecordResponse(200, 50, "/test");
        engine.EvaluateNow();

        Assert.Null(_context.GetOverridePolicy("/api/anything", null));
    }
}
```

- [ ] **Step 7.2: Run to verify fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ReactionPackEngineTests" 2>&1 | tail -10
```

- [ ] **Step 7.3: Create `IReactionPackContext`**

```csharp
// src/Mostlylucid.BotDetection/Services/IReactionPackContext.cs
namespace Mostlylucid.BotDetection.Services;

public interface IReactionPackContext
{
    /// <summary>
    /// Returns the policy name override for the given endpoint from any active reaction pack.
    /// Returns null when no pack is active; caller uses its default policy unchanged.
    /// Zero allocation on the happy path.
    /// </summary>
    string? GetOverridePolicy(string endpoint, string? currentPolicy);
}
```

- [ ] **Step 7.4: Create `ReactionPackContext`**

```csharp
// src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs
using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

public sealed class ReactionPackContext : IReactionPackContext
{
    private sealed record ActivePackState(string PackName, int Level, string PolicyName, string Scope, int Priority);

    private readonly ConcurrentDictionary<string, ActivePackState> _active = new(StringComparer.Ordinal);

    public string? GetOverridePolicy(string endpoint, string? currentPolicy)
    {
        if (_active.IsEmpty)
            return null;

        ActivePackState? best = null;
        foreach (var state in _active.Values)
        {
            if (!Matches(state.Scope, endpoint))
                continue;
            if (best == null
                || state.Priority > best.Priority
                || (state.Priority == best.Priority && state.Level > best.Level))
                best = state;
        }
        return best?.PolicyName;
    }

    public void SetActiveLevel(string packName, int level, string policyName, string scope, int priority = 0)
    {
        _active[packName] = new ActivePackState(packName, level, policyName, scope, priority);
    }

    public void Deactivate(string packName) => _active.TryRemove(packName, out _);

    public IReadOnlyList<(string PackName, int Level, string PolicyName, string Scope)> GetActiveStates() =>
        _active.Values.Select(s => (s.PackName, s.Level, s.PolicyName, s.Scope)).ToList();

    private static bool Matches(string scope, string endpoint)
    {
        if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
            return true;
        return endpoint.StartsWith(scope, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 7.5: Create `ReactionPackEngine`**

```csharp
// src/Mostlylucid.BotDetection/Services/ReactionPackEngine.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

public sealed class ReactionPackEngine : BackgroundService
{
    private readonly IReadOnlyList<ReactionPackDefinition> _packs;
    private readonly DegradationAtom _atom;
    private readonly ReactionPackContext _context;
    private readonly ReactionRuleEvaluator _evaluator;
    private readonly ILogger<ReactionPackEngine> _logger;
    private readonly double _evaluationIntervalSeconds;

    private readonly Dictionary<string, Dictionary<int, (HysteresisTracker Activate, HysteresisTracker Deactivate)>> _trackers = [];
    private readonly Dictionary<string, int> _currentLevel = [];

    public ReactionPackEngine(
        IEnumerable<ReactionPackDefinition> packs,
        DegradationAtom atom,
        ReactionPackContext context,
        ReactionRuleEvaluator evaluator,
        ILogger<ReactionPackEngine> logger,
        double evaluationIntervalSeconds = 5.0)
    {
        _packs = packs.Where(p => p.Enabled).ToList();
        _atom = atom;
        _context = context;
        _evaluator = evaluator;
        _logger = logger;
        _evaluationIntervalSeconds = evaluationIntervalSeconds;

        foreach (var pack in _packs)
        {
            _trackers[pack.Name] = pack.Steps.ToDictionary(
                s => s.Level,
                _ => (new HysteresisTracker(), new HysteresisTracker()));
            _currentLevel[pack.Name] = 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_evaluationIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);
            EvaluateNow();
        }
    }

    public void EvaluateNow()
    {
        var signals = _atom.GetAvailableSignalKeys()
            .ToDictionary(k => k, k => _atom.GetSignalValue(k));

        foreach (var pack in _packs)
            EvaluatePack(pack, signals);
    }

    private void EvaluatePack(ReactionPackDefinition pack, Dictionary<string, double> signals)
    {
        var current = _currentLevel[pack.Name];
        var scope = pack.IsGlobal ? "global" : (pack.ScopedEndpoint ?? "global");

        // Try escalate: check if next level activates
        var nextLevel = current + 1;
        var nextStep = pack.Steps.FirstOrDefault(s => s.Level == nextLevel);
        if (nextStep?.Activate != null)
        {
            var nextTrackers = _trackers[pack.Name][nextLevel];
            if (_evaluator.Evaluate(nextStep.Activate, signals, nextTrackers.Activate, $"{pack.Name}:L{nextLevel}:activate"))
            {
                _logger.LogInformation("Reaction pack '{Pack}' escalating {From} -> {To} (policy: {Policy})",
                    pack.Name, current, nextLevel, nextStep.Policy);
                _currentLevel[pack.Name] = nextLevel;
                _context.SetActiveLevel(pack.Name, nextLevel, nextStep.Policy, scope, pack.Priority);
                return;
            }
        }

        if (current <= 0)
            return;

        // Try de-escalate: check current step deactivate conditions
        var currentStep = pack.Steps.FirstOrDefault(s => s.Level == current);
        if (currentStep?.Deactivate == null)
            return;

        var currentTrackers = _trackers[pack.Name][current];
        if (!_evaluator.Evaluate(currentStep.Deactivate, signals, currentTrackers.Deactivate, $"{pack.Name}:L{current}:deactivate"))
            return;

        // Find highest lower level whose deactivate conditions are NOT yet met
        var newLevel = 0;
        for (var l = current - 1; l >= 1; l--)
        {
            var lowerStep = pack.Steps.FirstOrDefault(s => s.Level == l);
            if (lowerStep?.Deactivate == null) { newLevel = l; break; }
            var lowerTrackers = _trackers[pack.Name][l];
            if (!_evaluator.Evaluate(lowerStep.Deactivate, signals, lowerTrackers.Deactivate, $"{pack.Name}:L{l}:deactivate"))
            {
                newLevel = l;
                break;
            }
        }

        _logger.LogInformation("Reaction pack '{Pack}' de-escalating {From} -> {To}", pack.Name, current, newLevel);
        _currentLevel[pack.Name] = newLevel;

        if (newLevel == 0)
            _context.Deactivate(pack.Name);
        else
        {
            var newStep = pack.Steps.First(s => s.Level == newLevel);
            _context.SetActiveLevel(pack.Name, newLevel, newStep.Policy, scope, pack.Priority);
        }
    }
}
```

- [ ] **Step 7.6: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ReactionPackEngineTests" 2>&1 | tail -15
```
Expected: all pass.

- [ ] **Step 7.7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Services/IReactionPackContext.cs \
        src/Mostlylucid.BotDetection/Services/ReactionPackContext.cs \
        src/Mostlylucid.BotDetection/Services/ReactionPackEngine.cs \
        src/Mostlylucid.BotDetection.Test/Services/ReactionPackEngineTests.cs
git commit -m "feat(reaction-packs): ReactionPackEngine state machine and IReactionPackContext"
```

---

## Task 8: SQLite persistence

**Files:**
- Create: `src/Mostlylucid.BotDetection/Data/ReactionPackTransitionStore.cs`
- Modify: `src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Services/ReactionPackTransitionStoreTests.cs`

- [ ] **Step 8.1: Add `reaction_pack_transitions` table to `SqliteSessionStore`**

In `SqliteSessionStore.cs`, in the `InitializeAsync` method, find the last `CREATE TABLE IF NOT EXISTS` statement and add after it:

```sql
CREATE TABLE IF NOT EXISTS reaction_pack_transitions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    pack_name TEXT NOT NULL,
    from_level INTEGER NOT NULL,
    to_level INTEGER NOT NULL,
    triggered_by TEXT NOT NULL,
    signal_value REAL NOT NULL,
    occurred_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_rpt_pack_name ON reaction_pack_transitions(pack_name);
CREATE INDEX IF NOT EXISTS idx_rpt_occurred_at ON reaction_pack_transitions(occurred_at);
```

- [ ] **Step 8.2: Write failing tests**

```csharp
// src/Mostlylucid.BotDetection.Test/Services/ReactionPackTransitionStoreTests.cs
using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackTransitionStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ReactionPackTransitionStore _store;

    public ReactionPackTransitionStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE reaction_pack_transitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pack_name TEXT NOT NULL,
                from_level INTEGER NOT NULL,
                to_level INTEGER NOT NULL,
                triggered_by TEXT NOT NULL,
                signal_value REAL NOT NULL,
                occurred_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _store = new ReactionPackTransitionStore(_conn);
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();

    [Fact]
    public async Task RecordTransition_InsertsRow()
    {
        await _store.RecordTransitionAsync("test-pack", fromLevel: 0, toLevel: 1,
            triggeredBy: "response.error_rate_5xx", signalValue: 0.12);

        var transitions = await _store.GetRecentTransitionsAsync("test-pack", limit: 10);
        Assert.Single(transitions);
        Assert.Equal("test-pack", transitions[0].PackName);
        Assert.Equal(0, transitions[0].FromLevel);
        Assert.Equal(1, transitions[0].ToLevel);
        Assert.Equal("response.error_rate_5xx", transitions[0].TriggeredBy);
        Assert.Equal(0.12, transitions[0].SignalValue, precision: 6);
    }

    [Fact]
    public async Task GetRecentTransitions_ReturnsLatestFirst()
    {
        await _store.RecordTransitionAsync("pack-a", 0, 1, "signal.a", 0.1);
        await _store.RecordTransitionAsync("pack-a", 1, 2, "signal.b", 0.2);

        var transitions = await _store.GetRecentTransitionsAsync("pack-a", limit: 10);
        Assert.Equal(2, transitions.Count);
        Assert.Equal(2, transitions[0].ToLevel);
    }

    [Fact]
    public async Task GetLatestActiveLevel_NoTransitions_ReturnsZero()
    {
        Assert.Equal(0, await _store.GetLatestActiveLevelAsync("nonexistent-pack"));
    }

    [Fact]
    public async Task GetLatestActiveLevel_AfterEscalation_ReturnsCurrentLevel()
    {
        await _store.RecordTransitionAsync("my-pack", 0, 1, "signal.x", 0.15);
        await _store.RecordTransitionAsync("my-pack", 1, 2, "signal.x", 0.35);
        Assert.Equal(2, await _store.GetLatestActiveLevelAsync("my-pack"));
    }
}
```

- [ ] **Step 8.3: Run to verify fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ReactionPackTransitionStoreTests" 2>&1 | tail -10
```

- [ ] **Step 8.4: Create `ReactionPackTransitionStore`**

```csharp
// src/Mostlylucid.BotDetection/Data/ReactionPackTransitionStore.cs
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

public sealed record ReactionPackTransition(
    string PackName,
    int FromLevel,
    int ToLevel,
    string TriggeredBy,
    double SignalValue,
    DateTimeOffset OccurredAt);

public sealed class ReactionPackTransitionStore
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _existingConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ReactionPackTransitionStore(IOptions<BotDetectionOptions> options)
    {
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        _connectionString = $"Data Source={Path.Combine(basePath, "sessions.db")};Cache=Shared";
    }

    internal ReactionPackTransitionStore(SqliteConnection existingConnection)
    {
        _connectionString = existingConnection.ConnectionString;
        _existingConnection = existingConnection;
    }

    public async Task RecordTransitionAsync(
        string packName, int fromLevel, int toLevel,
        string triggeredBy, double signalValue,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var (conn, owned) = GetConnection();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO reaction_pack_transitions
                        (pack_name, from_level, to_level, triggered_by, signal_value, occurred_at)
                    VALUES (@pack, @from, @to, @by, @val, @at)
                    """;
                cmd.Parameters.AddWithValue("@pack", packName);
                cmd.Parameters.AddWithValue("@from", fromLevel);
                cmd.Parameters.AddWithValue("@to", toLevel);
                cmd.Parameters.AddWithValue("@by", triggeredBy);
                cmd.Parameters.AddWithValue("@val", signalValue);
                cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally { if (owned) await conn.DisposeAsync(); }
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<ReactionPackTransition>> GetRecentTransitionsAsync(
        string packName, int limit = 50, CancellationToken ct = default)
    {
        var (conn, owned) = GetConnection();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT pack_name, from_level, to_level, triggered_by, signal_value, occurred_at
                FROM reaction_pack_transitions
                WHERE pack_name = @pack
                ORDER BY occurred_at DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@pack", packName);
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<ReactionPackTransition>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(new ReactionPackTransition(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetDouble(4),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
            return results;
        }
        finally { if (owned) await conn.DisposeAsync(); }
    }

    public async Task<int> GetLatestActiveLevelAsync(string packName, CancellationToken ct = default)
    {
        var (conn, owned) = GetConnection();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT to_level FROM reaction_pack_transitions
                WHERE pack_name = @pack
                ORDER BY occurred_at DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@pack", packName);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? (int)l : 0;
        }
        finally { if (owned) await conn.DisposeAsync(); }
    }

    private (SqliteConnection conn, bool owned) GetConnection()
    {
        if (_existingConnection != null)
            return (_existingConnection, false);
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return (conn, true);
    }
}
```

- [ ] **Step 8.5: Run tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ReactionPackTransitionStoreTests" 2>&1 | tail -10
```
Expected: 4 passed.

- [ ] **Step 8.6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Data/ReactionPackTransitionStore.cs \
        src/Mostlylucid.BotDetection/Data/SqliteSessionStore.cs \
        src/Mostlylucid.BotDetection.Test/Services/ReactionPackTransitionStoreTests.cs
git commit -m "feat(reaction-packs): SQLite transition persistence"
```

---

## Task 9: DI registration

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 9.1: Add all reaction pack services to `AddBotDetection`**

In `ServiceCollectionExtensions.cs`, in the `AddBotDetection` method, find the "Register core services" comment block. After the existing registrations, add:

```csharp
// Signal groups (loaded from embedded YAML at startup)
services.AddSingleton<ISignalGroupRegistry>(sp =>
{
    var assembly = typeof(ServiceCollectionExtensions).Assembly;
    var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    var groups = assembly.GetManifestResourceNames()
        .Where(n => n.Contains("SignalGroups") && n.EndsWith(".yaml"))
        .Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return deserializer.Deserialize<Models.SignalGroupDefinition>(reader.ReadToEnd());
        })
        .Where(g => !string.IsNullOrWhiteSpace(g.Name))
        .ToList();

    return new Services.SignalGroupRegistry(groups);
});

// Reaction pack YAML definitions (loaded from embedded resources)
services.AddSingleton<IEnumerable<Models.ReactionPackDefinition>>(sp =>
{
    var assembly = typeof(ServiceCollectionExtensions).Assembly;
    var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
        .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    return assembly.GetManifestResourceNames()
        .Where(n => n.Contains("ReactionPacks") && n.EndsWith(".yaml"))
        .Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return deserializer.Deserialize<Models.ReactionPackDefinition>(reader.ReadToEnd());
        })
        .Where(p => p.Enabled)
        .ToList();
});

// Reaction pack runtime services
services.AddSingleton<Services.DegradationAtom>();
services.AddSingleton<Services.ReactionPackContext>();
services.AddSingleton<Services.IReactionPackContext>(sp => sp.GetRequiredService<Services.ReactionPackContext>());
services.AddSingleton<Services.ReactionRuleEvaluator>();
services.AddSingleton<Data.ReactionPackTransitionStore>();
services.AddHostedService<Services.ReactionPackEngine>();
```

- [ ] **Step 9.2: Build the solution**

```bash
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj 2>&1 | tail -15
```
Expected: Build succeeded.

- [ ] **Step 9.3: Run full test suite**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ 2>&1 | tail -20
```
Expected: all existing tests still pass.

- [ ] **Step 9.4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(reaction-packs): DI registration for all reaction pack services"
```

---

## Task 10: Middleware integration

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs`

- [ ] **Step 10.1: Add `DegradationAtom` and `IReactionPackContext` as optional constructor parameters**

In `BotDetectionMiddleware.cs`, add to the primary constructor parameter list (after `loadSensor`):

```csharp
Services.DegradationAtom? degradationAtom = null,
Services.IReactionPackContext? reactionPackContext = null
```

Add fields:

```csharp
private readonly Services.DegradationAtom? _degradationAtom = degradationAtom;
private readonly Services.IReactionPackContext? _reactionPackContext = reactionPackContext;
```

- [ ] **Step 10.2: Wrap the core middleware flow in try/finally for response recording**

In `InvokeAsync`, after all the early-return guards (api key checks, skip paths) and just before the main detection pipeline begins, wrap the remaining code in a try/finally block:

```csharp
var sw = System.Diagnostics.Stopwatch.StartNew();
try
{
    // ... existing code through _next(context) ...
}
finally
{
    sw.Stop();
    _degradationAtom?.RecordResponse(
        context.Response.StatusCode,
        sw.ElapsedMilliseconds,
        context.Request.Path.Value ?? "/");
}
```

The `finally` fires on all paths including early returns from action policies, so every response is recorded.

- [ ] **Step 10.3: Apply policy override in `HandlePostDetectionActionsAsync`**

In `HandlePostDetectionActionsAsync` (the private method at the bottom of the file), find the line:

```csharp
var actionPolicy = actionPolicyRegistry.GetPolicy(aggregatedResult.TriggeredActionPolicyName);
```

Replace with:

```csharp
var effectivePolicyName = _reactionPackContext?.GetOverridePolicy(
    context.Request.Path.Value ?? "/",
    aggregatedResult.TriggeredActionPolicyName)
    ?? aggregatedResult.TriggeredActionPolicyName;
var actionPolicy = actionPolicyRegistry.GetPolicy(effectivePolicyName);
```

Also find the fallback resolution section (near `resolvedPolicyName ??= _options.DefaultActionPolicyName;`) and add after it:

```csharp
var packOverride = _reactionPackContext?.GetOverridePolicy(
    context.Request.Path.Value ?? "/", resolvedPolicyName);
if (!string.IsNullOrEmpty(packOverride))
    resolvedPolicyName = packOverride;
```

- [ ] **Step 10.4: Build and run tests**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | tail -10
dotnet test src/Mostlylucid.BotDetection.Test/ 2>&1 | tail -20
```
Expected: build succeeds, all tests pass.

- [ ] **Step 10.5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs
git commit -m "feat(reaction-packs): middleware response recording and policy override"
```

---

## Task 11: Dashboard service and API endpoint

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Services/ReactionPackDashboardService.cs`
- Modify: the file that registers dashboard API routes in `BotDetection.UI`

- [ ] **Step 11.1: Find the dashboard API route file**

```bash
grep -rl "MapGet\|/_stylobot/api" src/Mostlylucid.BotDetection.UI/ --include="*.cs" | head -5
```

- [ ] **Step 11.2: Create `ReactionPackDashboardService`**

```csharp
// src/Mostlylucid.BotDetection.UI/Services/ReactionPackDashboardService.cs
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed record ActivePackSummary(
    string PackName, int Level, string PolicyName, string Scope, string LevelName);

public sealed record ReactionPackDashboardData(
    IReadOnlyList<ActivePackSummary> ActivePacks,
    IReadOnlyList<string> AllPackNames,
    IReadOnlyDictionary<string, double> SignalSnapshot,
    IReadOnlyList<ReactionPackTransition> RecentTransitions);

public sealed class ReactionPackDashboardService(
    ReactionPackContext packContext,
    DegradationAtom atom,
    IEnumerable<ReactionPackDefinition> allPacks,
    ReactionPackTransitionStore transitionStore)
{
    public async Task<ReactionPackDashboardData> GetDashboardDataAsync(CancellationToken ct = default)
    {
        var packList = allPacks.ToList();
        var activeStates = packContext.GetActiveStates();

        var activePacks = activeStates
            .Select(s => new ActivePackSummary(
                s.PackName, s.Level, s.PolicyName, s.Scope,
                LevelNameFor(packList, s.PackName, s.Level)))
            .ToList();

        var signalKeys = packList
            .SelectMany(p => p.Signals)
            .Where(s => !s.StartsWith('$'))
            .Distinct()
            .ToList();
        var signalSnapshot = signalKeys.ToDictionary(k => k, k => atom.GetSignalValue(k));

        var allTransitions = new List<ReactionPackTransition>();
        foreach (var pack in packList)
        {
            var transitions = await transitionStore.GetRecentTransitionsAsync(pack.Name, limit: 20, ct);
            allTransitions.AddRange(transitions);
        }

        return new ReactionPackDashboardData(
            activePacks,
            packList.Select(p => p.Name).ToList(),
            signalSnapshot,
            allTransitions.OrderByDescending(t => t.OccurredAt).Take(50).ToList());
    }

    private static string LevelNameFor(List<ReactionPackDefinition> packs, string packName, int level) =>
        packs.FirstOrDefault(p => p.Name == packName)?.Steps.FirstOrDefault(s => s.Level == level)?.Name
        ?? level.ToString();
}
```

- [ ] **Step 11.3: Register the service and add API endpoint**

In the DI setup for the UI project (wherever other dashboard services are registered), add:

```csharp
services.AddSingleton<ReactionPackDashboardService>();
```

In the route registration file found in Step 11.1, add:

```csharp
app.MapGet("/_stylobot/api/reaction-packs", async (
    ReactionPackDashboardService svc,
    CancellationToken ct) =>
{
    var data = await svc.GetDashboardDataAsync(ct);
    return Results.Ok(data);
}).RequireAuthorization("StylobotDashboard");
```

- [ ] **Step 11.4: Build UI project**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/ 2>&1 | tail -10
```
Expected: Build succeeded.

- [ ] **Step 11.5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/ReactionPackDashboardService.cs
git commit -m "feat(reaction-packs): dashboard service and API endpoint"
```

---

## Task 12: Dashboard UI tab

**Files:**
- Create: partial view file for the Reaction Packs tab content
- Modify: main dashboard layout to add the tab

- [ ] **Step 12.1: Find existing tab navigation and partial view files**

```bash
grep -rl "tab-button\|hx-get.*partials" src/Mostlylucid.BotDetection.UI/ --include="*.cshtml" --include="*.html" --include="*.razor" | head -5
```

- [ ] **Step 12.2: Add the Reaction Packs tab button**

In the tab navigation file, add a new button following the exact HTML pattern of existing tab buttons:

```html
<button class="tab-button" data-tab="reaction-packs"
        hx-get="/_stylobot/partials/reaction-packs"
        hx-target="#tab-content"
        hx-swap="innerHTML">
    Reaction Packs
</button>
```

- [ ] **Step 12.3: Create the partial view**

In the same directory as other dashboard partials, create `_ReactionPacksTab.cshtml` (or the appropriate extension for this project):

```html
<div id="reaction-packs-tab">
    <div class="grid grid-cols-1 gap-6">

        <div class="card">
            <h3 class="card-title">Active Packs</h3>
            <div id="active-packs-list"
                 hx-get="/_stylobot/api/reaction-packs"
                 hx-trigger="load, every 10s"
                 hx-swap="none">
                Loading...
            </div>
        </div>

        <div class="card">
            <h3 class="card-title">Current Signal Values</h3>
            <div id="reaction-signals"></div>
        </div>

        <div class="card">
            <h3 class="card-title">Recent Transitions</h3>
            <div id="transition-timeline"></div>
        </div>

    </div>
</div>

<script>
(function () {
    function esc(str) {
        var d = document.createElement('div');
        d.textContent = String(str);
        return d.innerHTML;
    }

    document.body.addEventListener('htmx:afterRequest', function (e) {
        if (!e.detail.requestConfig || !e.detail.requestConfig.path) return;
        if (!e.detail.requestConfig.path.includes('reaction-packs')) return;
        try {
            var data = JSON.parse(e.detail.xhr.response);
            renderActivePacks(data.activePacks);
            renderSignals(data.signalSnapshot);
            renderTransitions(data.recentTransitions);
        } catch (_) {}
    });

    function renderActivePacks(packs) {
        var el = document.getElementById('active-packs-list');
        if (!el) return;
        if (!packs || packs.length === 0) {
            el.textContent = 'No packs currently active';
            return;
        }
        var html = '';
        for (var i = 0; i < packs.length; i++) {
            var p = packs[i];
            html += '<div class="pack-badge">'
                + '<span class="font-mono">' + esc(p.packName) + '</span> '
                + '<span class="level-badge">' + esc(p.levelName) + '</span> '
                + '<span class="policy-tag">' + esc(p.policyName) + '</span>'
                + '</div>';
        }
        el.innerHTML = html;
    }

    function renderSignals(signals) {
        var el = document.getElementById('reaction-signals');
        if (!el || !signals) return;
        var html = '';
        for (var key in signals) {
            if (!Object.prototype.hasOwnProperty.call(signals, key)) continue;
            html += '<div class="signal-row">'
                + '<span class="signal-key">' + esc(key) + '</span>'
                + '<span class="signal-value">' + esc((signals[key] * 100).toFixed(2)) + '%</span>'
                + '</div>';
        }
        el.innerHTML = html;
    }

    function renderTransitions(transitions) {
        var el = document.getElementById('transition-timeline');
        if (!el) return;
        if (!transitions || transitions.length === 0) {
            el.textContent = 'No transitions recorded';
            return;
        }
        var html = '';
        for (var i = 0; i < transitions.length; i++) {
            var t = transitions[i];
            html += '<div class="transition-row">'
                + '<span class="pack-name">' + esc(t.packName) + '</span> '
                + '<span class="arrow">' + esc(t.fromLevel) + ' \u2192 ' + esc(t.toLevel) + '</span> '
                + '<span class="trigger">' + esc(t.triggeredBy) + ' = ' + esc((t.signalValue * 100).toFixed(2)) + '%</span> '
                + '<span class="time">' + esc(new Date(t.occurredAt * 1000).toLocaleTimeString()) + '</span>'
                + '</div>';
        }
        el.innerHTML = html;
    }
}());
</script>
```

Note: all dynamic content is escaped through the `esc()` helper before insertion into innerHTML.

- [ ] **Step 12.4: Add the HTMX partial route**

In the same file where other partial routes are registered, add:

```csharp
app.MapGet("/_stylobot/partials/reaction-packs", () =>
    Results.Content(
        """<div id="reaction-packs-partial-loaded"
                  hx-get="/_stylobot/api/reaction-packs"
                  hx-trigger="load"
                  hx-swap="none">Loading reaction pack data...</div>""",
        "text/html"))
    .RequireAuthorization("StylobotDashboard");
```

- [ ] **Step 12.5: Build and run demo to verify tab appears**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | tail -10
dotnet run --project src/Mostlylucid.BotDetection.Demo &
# Open http://localhost:5080/_stylobot
# Verify the Reaction Packs tab is visible in the tab bar
# Verify it loads without JS errors
```

- [ ] **Step 12.6: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/
git commit -m "feat(reaction-packs): dashboard UI tab with signal gauges and transition timeline"
```

---

## Final: Full solution build and test

- [ ] **Step F.1: Run all tests**

```bash
dotnet test mostlylucid.stylobot.sln 2>&1 | tail -30
```
Expected: all pass, no regressions.

- [ ] **Step F.2: Release build**

```bash
dotnet build mostlylucid.stylobot.sln -c Release 2>&1 | tail -10
```
Expected: Build succeeded.

- [ ] **Step F.3: Final commit if any cleanup**

```bash
git status
# Commit any remaining changes
```

---

## Self-Review

- [x] Signal groups: `ISignalGroupRegistry`, `SignalGroupRegistry`, 2 YAML files, embedded resource glob - Tasks 1-2
- [x] DegradationAtom: rolling windows, global + endpoint-scoped signals, decay timer - Task 3
- [x] YAML models: `ReactionPackDefinition`, `ReactionPackStep`, `ReactionConditionSet`, `ReactionRule` - Task 4
- [x] Built-in packs: 3 YAML packs, all thresholds from YAML with no magic numbers in code - Task 5
- [x] Hysteresis: `HysteresisTracker` + `ReactionRuleEvaluator`, signal group expansion via registry - Task 6
- [x] Engine + context: `ReactionPackEngine` (BackgroundService), `ReactionPackContext`, `IReactionPackContext` - Task 7
- [x] Escalation sequential (only tries current+1), de-escalation direct to highest non-satisfied lower level - Task 7
- [x] Pack conflict: higher priority wins, then higher level, in `ReactionPackContext.GetOverridePolicy` - Task 7
- [x] SQLite persistence: `ReactionPackTransitionStore`, schema migration in `SqliteSessionStore` - Task 8
- [x] DI registration: all services registered including `IHostedService` - Task 9
- [x] Middleware: `DegradationAtom.RecordResponse()` in finally block, `IReactionPackContext` override at both policy resolution sites - Task 10
- [x] Dashboard: `ReactionPackDashboardService`, API endpoint, HTMX tab with XSS-safe rendering via `esc()` helper - Tasks 11-12
- [x] No magic numbers: all thresholds/windows/delays come from YAML `for_seconds`, `above`, `below` fields
