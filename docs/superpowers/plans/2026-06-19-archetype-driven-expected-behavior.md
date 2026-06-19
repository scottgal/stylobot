# Archetype-Driven Expected Behavior Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `IdentityArchetype` YAML to encode per-dimension expected behavior (tolerance + drift role), evaluate alignment per request, surface deviations on the signature detail page, and feed identity-continuity into Multi-Factor Signatures. Supersedes the immediate-fix clamp at `a2913eed`.

**Architecture:** Long-form dimension blocks land on the existing archetype YAML. A new `ArchetypeAlignmentEvaluator` reads (Tier 1 bot-type defaults ▸ Tier 2 specific archetype ▸ Tier 3 per-signature pin), emits alignment signals onto the blackboard, and existing consumers (`SignatureRiskVerdictComposer`, `MultiFactorSignatures`, UI) read those signals. No parallel data path.

**Tech Stack:** .NET 10, VYaml, xUnit + FluentAssertions, Razor view components, sqlite + postgres (Dapper), existing `IFingerprintStore` LFU façade.

**Spec:** [docs/superpowers/specs/2026-06-19-archetype-driven-expected-behavior-design.md](../specs/2026-06-19-archetype-driven-expected-behavior-design.md)

---

## File structure

**New (FOSS):**
- `src/Mostlylucid.BotDetection/Risk/DimensionRule.cs` — per-dim record (slot, expected, tolerance, drift_role, weight)
- `src/Mostlylucid.BotDetection/Risk/DimensionTolerance.cs` — discriminated record + `ToleranceShape` enum
- `src/Mostlylucid.BotDetection/Risk/DimensionVerdict.cs` — per-dim alignment result
- `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentResult.cs` — full alignment record
- `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentEvaluator.cs` — sealed evaluator
- `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentOptions.cs` — tunables
- `src/Mostlylucid.BotDetection/Identity/ComposedArchetype.cs` — Tier1+Tier2+Tier3 composition
- `src/Mostlylucid.BotDetection/Storage/SignaturePin.cs` — pin record
- `src/Mostlylucid.BotDetection/Definitions/IdentityArchetypes/_bot_type/{search-engine,ai-bot,social-media,monitoring,tool,good-bot,internal}.yaml` (7 files)

**New (UI):**
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbExpectedBehavior/Default.cshtml`
- `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbExpectedBehavior/SbExpectedBehaviorViewComponent.cs`
- `src/Mostlylucid.BotDetection.UI/Models/ExpectedBehaviorViewModel.cs`

**Modified:**
- `Identity/IdentityArchetype.cs` — add `IReadOnlyList<DimensionRule>? Dimensions` + `string? InheritsFrom` + `IdentityContinuityRule? IdentityContinuity`
- `Identity/IdentityArchetypeRegistry.cs` — parse long-form, compose inheritance
- `Models/SignalKeys.cs` — add `archetype.alignment.*` constants
- `Risk/SignatureRiskInputs.cs` — add `IdentityHolds`, `BehaviorAligned`, `WeakDeviationScore`
- `Risk/SignatureRiskVerdictComposer.cs` — replace Internal-only branch with trusted-and-aligned clamp
- `Test/Risk/InternalRiskBandClampTests.cs` — migrate to new clamp semantics
- `Identity/MultiFactorSignatureService.cs` — read `archetype.alignment.break_action` signal
- `Storage/IFingerprintStore.cs` — add `GetSignaturePinsAsync`, `WriteSignaturePinAsync`, `DeleteSignaturePinAsync`
- `Storage/SqliteFingerprintStore.cs` + `Storage/PostgresFingerprintStore.cs` — implement pin methods
- `Migrations.Sqlite/Migrations/<n>_AddSignaturePins.cs` + `Migrations.Postgres/Migrations/<n>_AddSignaturePins.cs`
- `BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` — pin write endpoint at `/api/v1/signatures/{sig}/pin`
- `BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml` — mount SbExpectedBehavior above Detection Signals panel

---

## Phase 1 — Schema + loader + Tier 1 archetypes

### Task 1.1: DimensionRule + DimensionTolerance records

**Files:**
- Create: `src/Mostlylucid.BotDetection/Risk/DimensionTolerance.cs`
- Create: `src/Mostlylucid.BotDetection/Risk/DimensionRule.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Risk/DimensionToleranceTests.cs`

- [ ] **Step 1: Write failing tolerance match tests**

```csharp
// src/Mostlylucid.BotDetection.Test/Risk/DimensionToleranceTests.cs
using Mostlylucid.BotDetection.Risk;
using Xunit;
namespace Mostlylucid.BotDetection.Test.Risk;
public class DimensionToleranceTests
{
    [Fact]
    public void Exact_match_string_case_insensitive()
        => Assert.True(DimensionTolerance.Exact().IsAligned("Chrome", "chrome"));
    [Fact]
    public void Range_inclusive_bounds()
    {
        var t = DimensionTolerance.Range(5, 500);
        Assert.True(t.IsAligned(60d, null!));
        Assert.True(t.IsAligned(5d, null!));
        Assert.True(t.IsAligned(500d, null!));
        Assert.False(t.IsAligned(4d, null!));
        Assert.False(t.IsAligned(501d, null!));
    }
    [Fact]
    public void OneOf_membership()
    {
        var t = DimensionTolerance.OneOf(new object[] { "GB", "US" });
        Assert.True(t.IsAligned("GB", null!));
        Assert.False(t.IsAligned("FR", null!));
    }
    [Fact]
    public void NumericDelta_absolute()
    {
        var t = DimensionTolerance.NumericDelta(0.1);
        Assert.True(t.IsAligned(0.93, 0.95));
        Assert.False(t.IsAligned(0.80, 0.95));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~DimensionToleranceTests"
```
Expected: FAIL with "DimensionTolerance not defined".

- [ ] **Step 3: Implement DimensionTolerance + ToleranceShape**

```csharp
// src/Mostlylucid.BotDetection/Risk/DimensionTolerance.cs
namespace Mostlylucid.BotDetection.Risk;

public enum ToleranceShape { Exact, Range, OneOf, NumericDelta }

public sealed record DimensionTolerance
{
    public required ToleranceShape Shape { get; init; }
    public double? Lower { get; init; }
    public double? Upper { get; init; }
    public double? Delta { get; init; }
    public IReadOnlyList<object>? List { get; init; }

    public static DimensionTolerance Exact() => new() { Shape = ToleranceShape.Exact };
    public static DimensionTolerance Range(double lower, double upper) => new() { Shape = ToleranceShape.Range, Lower = lower, Upper = upper };
    public static DimensionTolerance OneOf(IReadOnlyList<object> values) => new() { Shape = ToleranceShape.OneOf, List = values };
    public static DimensionTolerance NumericDelta(double delta) => new() { Shape = ToleranceShape.NumericDelta, Delta = delta };

    public bool IsAligned(object observed, object? expected) => Shape switch
    {
        ToleranceShape.Exact when expected is string es && observed is string os
            => string.Equals(es, os, StringComparison.OrdinalIgnoreCase),
        ToleranceShape.Exact => Equals(observed, expected),
        ToleranceShape.Range when observed is double d
            => d >= (Lower ?? double.MinValue) && d <= (Upper ?? double.MaxValue),
        ToleranceShape.OneOf => List?.Any(v => Equals(v, observed)) == true
                                || (observed is string os && List?.Any(v => v is string vs && string.Equals(vs, os, StringComparison.OrdinalIgnoreCase)) == true),
        ToleranceShape.NumericDelta when observed is double od && expected is double ed
            => Math.Abs(od - ed) <= (Delta ?? 0),
        _ => false,
    };

    public double NormalizedDistance(object observed, object? expected)
    {
        if (IsAligned(observed, expected)) return 0.0;
        return Shape switch
        {
            ToleranceShape.Range when observed is double d => DistanceFromRange(d),
            ToleranceShape.NumericDelta when observed is double od && expected is double ed
                => Math.Min(1.0, Math.Abs(od - ed) / Math.Max(Delta ?? 1.0, 1e-9) - 1.0),
            _ => 1.0,
        };
    }
    private double DistanceFromRange(double d)
    {
        var lo = Lower ?? double.MinValue;
        var hi = Upper ?? double.MaxValue;
        var span = Math.Max(hi - lo, 1e-9);
        if (d < lo) return Math.Min(1.0, (lo - d) / span);
        if (d > hi) return Math.Min(1.0, (d - hi) / span);
        return 0.0;
    }
}
```

```csharp
// src/Mostlylucid.BotDetection/Risk/DimensionRule.cs
namespace Mostlylucid.BotDetection.Risk;

public enum DriftRole { Identity, Behavior, Weak }

public sealed record DimensionRule
{
    public required string Slot { get; init; }
    public required object Expected { get; init; }
    public required DimensionTolerance Tolerance { get; init; }
    public required DriftRole DriftRole { get; init; }
    public double Weight { get; init; } = 1.0;
}
```

- [ ] **Step 4: Run tests; expect 4 passing**

```
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~DimensionToleranceTests"
```
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Risk/DimensionTolerance.cs src/Mostlylucid.BotDetection/Risk/DimensionRule.cs src/Mostlylucid.BotDetection.Test/Risk/DimensionToleranceTests.cs
git commit -m "feat(risk): DimensionTolerance + DimensionRule records"
```

### Task 1.2: Extend IdentityArchetype + YAML loader (long-form)

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Identity/ArchetypeLongFormLoaderTests.cs`

- [ ] **Step 1: Add `IdentityContinuityRule` record + `Dimensions` / `InheritsFrom` fields**

```csharp
// append to src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs
public sealed record IdentityContinuityRule
{
    public required IReadOnlyList<string> Required { get; init; }
    public required TimeSpan TimeWindow { get; init; }
    public string BreakAction { get; init; } = "split";
}
// add to IdentityArchetype record:
//   public IReadOnlyList<Risk.DimensionRule>? Dimensions { get; init; }
//   public string? InheritsFrom { get; init; }
//   public IdentityContinuityRule? IdentityContinuity { get; init; }
```

- [ ] **Step 2: Write failing loader test**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/ArchetypeLongFormLoaderTests.cs
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Risk;
using Xunit;
namespace Mostlylucid.BotDetection.Test.Identity;
public class ArchetypeLongFormLoaderTests
{
    [Fact]
    public void Long_form_dimension_parses_with_range_tolerance()
    {
        var yaml = """
archetype_id: test-tier1
name: Test Tier 1
archetype_kind: tool
dimensions:
  session.request_count:
    expected: 60
    tolerance:
      shape: range
      lower: 5
      upper: 500
    drift_role: behavior
    weight: 0.8
""";
        var arch = ArchetypeYamlLoader.ParseSingle(yaml);
        Assert.NotNull(arch.Dimensions);
        var d = Assert.Single(arch.Dimensions);
        Assert.Equal("session.request_count", d.Slot);
        Assert.Equal(60.0, Convert.ToDouble(d.Expected));
        Assert.Equal(ToleranceShape.Range, d.Tolerance.Shape);
        Assert.Equal(5.0, d.Tolerance.Lower);
        Assert.Equal(500.0, d.Tolerance.Upper);
        Assert.Equal(DriftRole.Behavior, d.DriftRole);
    }

    [Fact]
    public void Shorthand_dimension_treated_as_exact_behavior()
    {
        var yaml = """
archetype_id: test-shorthand
name: Test Shorthand
archetype_kind: tool
dimensions:
  hdr.ua_family:
    value: "Chrome"
    confidence: 0.95
""";
        var arch = ArchetypeYamlLoader.ParseSingle(yaml);
        Assert.Null(arch.Dimensions); // shorthand still loaded into centroid path; Dimensions empty until long-form used
    }

    [Fact]
    public void InheritsFrom_parses()
    {
        var yaml = """
archetype_id: googlebot
name: Googlebot
archetype_kind: tool
inherits_from: _bot_type/search-engine
""";
        var arch = ArchetypeYamlLoader.ParseSingle(yaml);
        Assert.Equal("_bot_type/search-engine", arch.InheritsFrom);
    }
}
```

- [ ] **Step 3: Run test (expect fail — `ArchetypeYamlLoader.ParseSingle` not defined)**

```
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ArchetypeLongFormLoaderTests"
```

- [ ] **Step 4: Implement loader**

```csharp
// src/Mostlylucid.BotDetection/Identity/ArchetypeYamlLoader.cs (new)
using VYaml.Serialization;
using Mostlylucid.BotDetection.Risk;

namespace Mostlylucid.BotDetection.Identity;

public static class ArchetypeYamlLoader
{
    public static IdentityArchetype ParseSingle(string yaml)
    {
        var raw = YamlSerializer.Deserialize<RawArchetypeYaml>(System.Text.Encoding.UTF8.GetBytes(yaml));
        return Compile(raw);
    }

    internal static IdentityArchetype Compile(RawArchetypeYaml raw)
    {
        var dimensions = raw.Dimensions?
            .Where(kv => IsLongForm(kv.Value))
            .Select(kv => ParseDimensionRule(kv.Key, kv.Value))
            .ToList();
        return new IdentityArchetype
        {
            ArchetypeId = raw.ArchetypeId!,
            Name = raw.Name!,
            Description = raw.Description,
            ArchetypeKind = raw.ArchetypeKind ?? "tool",
            Centroid = Array.Empty<float>(),
            DimensionMask = Array.Empty<float>(),
            Dimensions = dimensions is { Count: > 0 } ? dimensions : null,
            InheritsFrom = raw.InheritsFrom,
            IdentityContinuity = raw.IdentityContinuity is null ? null : new IdentityContinuityRule
            {
                Required = raw.IdentityContinuity.Required ?? Array.Empty<string>(),
                TimeWindow = ParseTimeSpan(raw.IdentityContinuity.TimeWindow ?? "24h"),
                BreakAction = raw.IdentityContinuity.BreakAction ?? "split",
            },
        };
    }

    private static bool IsLongForm(Dictionary<string, object>? d)
        => d is not null && (d.ContainsKey("expected") || d.ContainsKey("drift_role"));

    private static DimensionRule ParseDimensionRule(string slot, Dictionary<string, object>? d)
    {
        var expected = d!["expected"];
        var tolerance = ParseTolerance(d.GetValueOrDefault("tolerance") ?? "exact");
        var driftRole = Enum.Parse<DriftRole>(d.GetValueOrDefault("drift_role")?.ToString() ?? "Behavior", ignoreCase: true);
        var weight = Convert.ToDouble(d.GetValueOrDefault("weight") ?? 1.0);
        return new DimensionRule { Slot = slot, Expected = expected, Tolerance = tolerance, DriftRole = driftRole, Weight = weight };
    }

    private static DimensionTolerance ParseTolerance(object raw)
    {
        if (raw is string s) return s.ToLowerInvariant() switch
        {
            "exact" => DimensionTolerance.Exact(),
            _ => DimensionTolerance.Exact(),
        };
        if (raw is Dictionary<string, object> d)
        {
            var shape = d["shape"].ToString()!.ToLowerInvariant();
            return shape switch
            {
                "range" => DimensionTolerance.Range(Convert.ToDouble(d["lower"]), Convert.ToDouble(d["upper"])),
                "oneof" => DimensionTolerance.OneOf((d["values"] as IEnumerable<object>)?.ToList() ?? throw new InvalidOperationException("oneof requires values")),
                "numeric_delta" => DimensionTolerance.NumericDelta(Convert.ToDouble(d["delta"])),
                _ => DimensionTolerance.Exact(),
            };
        }
        return DimensionTolerance.Exact();
    }

    private static TimeSpan ParseTimeSpan(string s)
    {
        if (s.EndsWith("h")) return TimeSpan.FromHours(double.Parse(s[..^1]));
        if (s.EndsWith("m")) return TimeSpan.FromMinutes(double.Parse(s[..^1]));
        if (s.EndsWith("d")) return TimeSpan.FromDays(double.Parse(s[..^1]));
        return TimeSpan.Parse(s);
    }
}

[YamlObject]
internal partial class RawArchetypeYaml
{
    public string? ArchetypeId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ArchetypeKind { get; set; }
    public string? InheritsFrom { get; set; }
    public Dictionary<string, Dictionary<string, object>?>? Dimensions { get; set; }
    public RawContinuityYaml? IdentityContinuity { get; set; }
}

[YamlObject]
internal partial class RawContinuityYaml
{
    public List<string>? Required { get; set; }
    public string? TimeWindow { get; set; }
    public string? BreakAction { get; set; }
}
```

- [ ] **Step 5: Run tests; expect 3 passing**

```
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~ArchetypeLongFormLoaderTests"
```

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/IdentityArchetype.cs src/Mostlylucid.BotDetection/Identity/ArchetypeYamlLoader.cs src/Mostlylucid.BotDetection.Test/Identity/ArchetypeLongFormLoaderTests.cs
git commit -m "feat(archetype): long-form YAML loader + InheritsFrom + identity_continuity"
```

### Task 1.3: ComposedArchetype + inheritance composer

**Files:**
- Create: `src/Mostlylucid.BotDetection/Identity/ComposedArchetype.cs`
- Modify: `src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Identity/ArchetypeInheritanceTests.cs`

- [ ] **Step 1: Write failing inheritance test**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/ArchetypeInheritanceTests.cs
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Risk;
using Xunit;
namespace Mostlylucid.BotDetection.Test.Identity;
public class ArchetypeInheritanceTests
{
    [Fact]
    public void Child_overrides_parent_at_dimension_level()
    {
        var parent = new IdentityArchetype { ArchetypeId="_bot_type/search-engine", Name="Search Engine", ArchetypeKind="tool",
            Centroid=Array.Empty<float>(), DimensionMask=Array.Empty<float>(),
            Dimensions = new[]{
                new DimensionRule{ Slot="session.request_count", Expected=5.0, Tolerance=DimensionTolerance.Range(0,20), DriftRole=DriftRole.Behavior },
                new DimensionRule{ Slot="network.country_code",  Expected=new object[]{"US"}, Tolerance=DimensionTolerance.OneOf(new object[]{"US"}), DriftRole=DriftRole.Weak },
            }};
        var child = new IdentityArchetype { ArchetypeId="googlebot", Name="Googlebot", ArchetypeKind="tool",
            Centroid=Array.Empty<float>(), DimensionMask=Array.Empty<float>(),
            InheritsFrom="_bot_type/search-engine",
            Dimensions = new[]{
                new DimensionRule{ Slot="session.request_count", Expected=10.0, Tolerance=DimensionTolerance.Range(0,30), DriftRole=DriftRole.Behavior },
            }};
        var composed = ComposedArchetype.Compose(child, parent, pin: null);
        var rate = composed.Dimensions.Single(d => d.Slot == "session.request_count");
        Assert.Equal(10.0, Convert.ToDouble(rate.Expected));
        Assert.Equal(20.0, rate.Tolerance.Upper); // child wins
        // parent-only dim still inherited
        Assert.Contains(composed.Dimensions, d => d.Slot == "network.country_code");
    }

    [Fact]
    public void Pin_overrides_child_and_parent()
    {
        var parent = MakeArch("p", ("session.request_count", 5.0, 0, 20));
        var child = MakeArch("c", ("session.request_count", 10.0, 0, 30), inheritsFrom: "p");
        var pin = new DimensionRule { Slot="session.request_count", Expected=200.0,
            Tolerance=DimensionTolerance.Range(50, 500), DriftRole=DriftRole.Behavior };
        var composed = ComposedArchetype.Compose(child, parent, new[] { pin });
        var rate = composed.Dimensions.Single();
        Assert.Equal(200.0, Convert.ToDouble(rate.Expected));
        Assert.Equal(500.0, rate.Tolerance.Upper);
    }

    private static IdentityArchetype MakeArch(string id, (string slot, double expected, double lo, double hi) d, string? inheritsFrom = null)
        => new() { ArchetypeId=id, Name=id, ArchetypeKind="tool", Centroid=Array.Empty<float>(), DimensionMask=Array.Empty<float>(),
            InheritsFrom = inheritsFrom,
            Dimensions = new[]{ new DimensionRule{ Slot=d.slot, Expected=d.expected,
                Tolerance=DimensionTolerance.Range(d.lo, d.hi), DriftRole=DriftRole.Behavior } } };
}
```

- [ ] **Step 2: Run; expect compile fail (ComposedArchetype undefined)**

- [ ] **Step 3: Implement ComposedArchetype**

```csharp
// src/Mostlylucid.BotDetection/Identity/ComposedArchetype.cs
using Mostlylucid.BotDetection.Risk;
namespace Mostlylucid.BotDetection.Identity;

public sealed record ComposedArchetype
{
    public required string ArchetypeId { get; init; }
    public required IReadOnlyList<DimensionRule> Dimensions { get; init; }
    public IdentityContinuityRule? IdentityContinuity { get; init; }

    public static ComposedArchetype Compose(
        IdentityArchetype tier2,
        IdentityArchetype? tier1,
        IReadOnlyList<DimensionRule>? pin)
    {
        var bySlot = new Dictionary<string, DimensionRule>(StringComparer.Ordinal);
        foreach (var d in tier1?.Dimensions ?? Array.Empty<DimensionRule>()) bySlot[d.Slot] = d;
        foreach (var d in tier2.Dimensions ?? Array.Empty<DimensionRule>()) bySlot[d.Slot] = d;
        foreach (var d in pin ?? Array.Empty<DimensionRule>()) bySlot[d.Slot] = d;
        return new ComposedArchetype
        {
            ArchetypeId = tier2.ArchetypeId,
            Dimensions = bySlot.Values.ToList(),
            IdentityContinuity = tier2.IdentityContinuity ?? tier1?.IdentityContinuity,
        };
    }
}
```

- [ ] **Step 4: Run tests; expect 2 passing**

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Identity/ComposedArchetype.cs src/Mostlylucid.BotDetection.Test/Identity/ArchetypeInheritanceTests.cs
git commit -m "feat(archetype): ComposedArchetype Tier1/Tier2/Tier3 inheritance"
```

### Task 1.4: Author 7 Tier 1 bot-type YAML files

**Files:**
- Create: `src/Mostlylucid.BotDetection/Definitions/IdentityArchetypes/_bot_type/{search-engine,ai-bot,social-media,monitoring,tool,good-bot,internal}.yaml`
- Test: `src/Mostlylucid.BotDetection.Test/Identity/BotTypeDefaultsTests.cs`

- [ ] **Step 1: Write failing test that all 7 load**

```csharp
// src/Mostlylucid.BotDetection.Test/Identity/BotTypeDefaultsTests.cs
using Mostlylucid.BotDetection.Identity;
using Xunit;
namespace Mostlylucid.BotDetection.Test.Identity;
public class BotTypeDefaultsTests
{
    [Theory]
    [InlineData("_bot_type/search-engine")]
    [InlineData("_bot_type/ai-bot")]
    [InlineData("_bot_type/social-media")]
    [InlineData("_bot_type/monitoring")]
    [InlineData("_bot_type/tool")]
    [InlineData("_bot_type/good-bot")]
    [InlineData("_bot_type/internal")]
    public void BotTypeDefault_loads_with_expected_dimensions(string id)
    {
        var registry = TestHost.MakeRegistry();
        var arch = registry.TryGetById(id);
        Assert.NotNull(arch);
        Assert.NotNull(arch.Dimensions);
        Assert.Contains(arch.Dimensions, d => d.Slot == "session.request_count");
    }
}
// TestHost.MakeRegistry helper builds a minimal ILoggerFactory + IdentityVectorEncoder.
```

- [ ] **Step 2: Run; expect FAIL (files don't exist)**

- [ ] **Step 3: Author the 7 YAML files**

Example: `src/Mostlylucid.BotDetection/Definitions/IdentityArchetypes/_bot_type/internal.yaml`

```yaml
archetype_id: _bot_type/internal
name: Internal (default)
description: Default behavior expectations for BotType.Internal -- network-trusted internal clients.
archetype_kind: tool
dimensions:
  hdr.ua_family:
    expected: "StyloBot.Internal"
    tolerance: exact
    drift_role: identity
    weight: 1.0
  session.request_count:
    expected: 60
    tolerance: { shape: range, lower: 5, upper: 500 }
    drift_role: behavior
    weight: 0.8
  session.frequency_periodicity_score:
    expected: 0.95
    tolerance: { shape: numeric_delta, delta: 0.1 }
    drift_role: behavior
identity_continuity:
  required: [hdr.ua_family, transport.tls_ja4]
  time_window: 24h
  break_action: split
```

Remaining 6 files follow the seed table in the spec (search-engine, ai-bot, social-media, monitoring, tool, good-bot). All have `drift_role: behavior` unless noted, and at minimum `session.request_count` + `session.frequency_periodicity_score`.

- [ ] **Step 4: Modify registry to glob `_bot_type/*.yaml` as embedded resources**

```csharp
// in IdentityArchetypeRegistry.LoadFromEmbeddedResources, ensure the resource name
// filter accepts the `_bot_type` sub-folder (VYaml + Assembly.GetManifestResourceNames
// flattens paths; verify the resource id contains `_bot_type` segment so they load).
```

- [ ] **Step 5: Run tests; expect 7 passing**

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Definitions/IdentityArchetypes/_bot_type/ src/Mostlylucid.BotDetection/Identity/IdentityArchetypeRegistry.cs src/Mostlylucid.BotDetection.Test/Identity/BotTypeDefaultsTests.cs
git commit -m "feat(archetype): 7 Tier-1 bot-type default archetypes"
```

---

## Phase 2 — Evaluator

### Task 2.1: ArchetypeAlignmentResult + evaluator skeleton

**Files:**
- Create: `src/Mostlylucid.BotDetection/Risk/DimensionVerdict.cs`
- Create: `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentResult.cs`
- Create: `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentOptions.cs`
- Create: `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentEvaluator.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Risk/ArchetypeAlignmentEvaluatorTests.cs`

- [ ] **Step 1: Write failing alignment tests (one per shape × one per role)**

```csharp
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Risk;
using Xunit;
namespace Mostlylucid.BotDetection.Test.Risk;
public class ArchetypeAlignmentEvaluatorTests
{
    private static ComposedArchetype Composed(params DimensionRule[] dims)
        => new() { ArchetypeId = "t", Dimensions = dims };

    [Fact]
    public void Aligned_when_observed_within_range()
    {
        var c = Composed(new() { Slot="session.request_count", Expected=60.0,
            Tolerance=DimensionTolerance.Range(5, 500), DriftRole=DriftRole.Behavior });
        var observed = new Dictionary<string, object> { ["session.request_count"] = 58.0 };
        var r = new ArchetypeAlignmentEvaluator(new()).Evaluate(c, observed);
        Assert.True(r.BehaviorAligned);
        Assert.True(r.IdentityHolds);
    }
    [Fact]
    public void BehaviorAligned_false_when_observed_above_range()
    {
        var c = Composed(new() { Slot="session.request_count", Expected=60.0,
            Tolerance=DimensionTolerance.Range(5, 500), DriftRole=DriftRole.Behavior });
        var observed = new Dictionary<string, object> { ["session.request_count"] = 1000.0 };
        var r = new ArchetypeAlignmentEvaluator(new()).Evaluate(c, observed);
        Assert.False(r.BehaviorAligned);
        Assert.Equal("session.request_count", Assert.Single(r.Dimensions, d => !d.Aligned).Slot);
    }
    [Fact]
    public void IdentityHolds_false_when_identity_slot_misaligned()
    {
        var c = Composed(new() { Slot="hdr.ua_family", Expected="StyloBot.Internal",
            Tolerance=DimensionTolerance.Exact(), DriftRole=DriftRole.Identity });
        var observed = new Dictionary<string, object> { ["hdr.ua_family"] = "curl" };
        var r = new ArchetypeAlignmentEvaluator(new()).Evaluate(c, observed);
        Assert.False(r.IdentityHolds);
    }
    [Fact]
    public void WeakDeviation_accumulates_but_doesnt_break_alignment()
    {
        var c = Composed(new() { Slot="network.country_code", Expected=new object[]{"GB"},
            Tolerance=DimensionTolerance.OneOf(new object[]{"GB"}), DriftRole=DriftRole.Weak });
        var observed = new Dictionary<string, object> { ["network.country_code"] = "FR" };
        var r = new ArchetypeAlignmentEvaluator(new()).Evaluate(c, observed);
        Assert.True(r.IdentityHolds);
        Assert.True(r.BehaviorAligned);
        Assert.True(r.WeakDeviationScore > 0);
    }
}
```

- [ ] **Step 2: Run (expect FAIL — types undefined)**

- [ ] **Step 3: Implement records + evaluator**

```csharp
// src/Mostlylucid.BotDetection/Risk/DimensionVerdict.cs
namespace Mostlylucid.BotDetection.Risk;
public sealed record DimensionVerdict
{
    public required string Slot { get; init; }
    public required object Expected { get; init; }
    public required object? Observed { get; init; }
    public required bool Aligned { get; init; }
    public required DriftRole DriftRole { get; init; }
    public required double Distance { get; init; }
    public string? Label { get; init; }
}
// src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentResult.cs
public sealed record ArchetypeAlignmentResult
{
    public required string ArchetypeId { get; init; }
    public required IReadOnlyList<DimensionVerdict> Dimensions { get; init; }
    public required bool IdentityHolds { get; init; }
    public required bool BehaviorAligned { get; init; }
    public required double WeakDeviationScore { get; init; }
    public string? BreakAction { get; init; }
}
// src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentOptions.cs
public sealed class ArchetypeAlignmentOptions
{
    public bool EvaluatorEnabled { get; set; } = true;
    public double DefaultWeakWeight { get; set; } = 0.3;
    public int IdentityContinuityMinViolations { get; set; } = 2;
    public double WeakDeviationLowGate { get; set; } = 0.3;
    public double WeakDeviationMediumGate { get; set; } = 0.6;
}
// src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentEvaluator.cs
using Mostlylucid.BotDetection.Identity;
namespace Mostlylucid.BotDetection.Risk;
public sealed class ArchetypeAlignmentEvaluator
{
    private readonly ArchetypeAlignmentOptions _opts;
    public ArchetypeAlignmentEvaluator(ArchetypeAlignmentOptions opts) => _opts = opts;
    public ArchetypeAlignmentResult Evaluate(ComposedArchetype archetype, IReadOnlyDictionary<string, object> observed)
    {
        var verdicts = new List<DimensionVerdict>(archetype.Dimensions.Count);
        bool identityHolds = true, behaviorAligned = true;
        double weakSum = 0; int weakCount = 0;
        foreach (var d in archetype.Dimensions)
        {
            observed.TryGetValue(d.Slot, out var obs);
            var aligned = obs is not null && d.Tolerance.IsAligned(obs, d.Expected);
            var distance = obs is null ? 1.0 : d.Tolerance.NormalizedDistance(obs, d.Expected);
            verdicts.Add(new DimensionVerdict
            {
                Slot = d.Slot, Expected = d.Expected, Observed = obs,
                Aligned = aligned, DriftRole = d.DriftRole, Distance = distance,
                Label = aligned ? null : LabelFor(d.Slot),
            });
            if (!aligned)
            {
                if (d.DriftRole == DriftRole.Identity) identityHolds = false;
                else if (d.DriftRole == DriftRole.Behavior) behaviorAligned = false;
                else { weakSum += distance * d.Weight; weakCount++; }
            }
        }
        var weak = weakCount == 0 ? 0.0 : Math.Min(1.0, weakSum / weakCount);
        return new ArchetypeAlignmentResult
        {
            ArchetypeId = archetype.ArchetypeId, Dimensions = verdicts,
            IdentityHolds = identityHolds, BehaviorAligned = behaviorAligned,
            WeakDeviationScore = weak, BreakAction = null,
        };
    }
    private static string LabelFor(string slot) => slot switch
    {
        "session.request_count" => "rate shift",
        "session.frequency_periodicity_score" => "periodicity shift",
        "network.country_code" => "geo shift",
        "hdr.ua_family" => "UA family shift",
        "transport.tls_ja4" => "TLS shift",
        _ => "drift",
    };
}
```

- [ ] **Step 4: Run; expect 4 passing**

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection/Risk/DimensionVerdict.cs src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentResult.cs src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentOptions.cs src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentEvaluator.cs src/Mostlylucid.BotDetection.Test/Risk/ArchetypeAlignmentEvaluatorTests.cs
git commit -m "feat(risk): ArchetypeAlignmentEvaluator + result records"
```

### Task 2.2: Identity-continuity sliding window + `break_action`

**Files:**
- Create: `src/Mostlylucid.BotDetection/Risk/IdentityContinuityTracker.cs`
- Modify: `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentEvaluator.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Risk/IdentityContinuityTrackerTests.cs`

- [ ] **Step 1: Write failing window test**

```csharp
public class IdentityContinuityTrackerTests
{
    [Fact]
    public void Two_consecutive_violations_in_window_emits_split()
    {
        var rule = new IdentityContinuityRule { Required = new[]{"hdr.ua_family"},
            TimeWindow = TimeSpan.FromHours(1), BreakAction = "split" };
        var t = new IdentityContinuityTracker(minViolations: 2);
        Assert.Null(t.Observe("sig1", rule, identityHolds: false, now: DateTime.UtcNow));
        Assert.Equal("split", t.Observe("sig1", rule, identityHolds: false, now: DateTime.UtcNow.AddSeconds(5)));
    }
    [Fact]
    public void Aligned_observation_clears_streak()
    {
        var rule = new IdentityContinuityRule { Required = new[]{"hdr.ua_family"},
            TimeWindow = TimeSpan.FromHours(1), BreakAction = "split" };
        var t = new IdentityContinuityTracker(minViolations: 2);
        t.Observe("sig1", rule, identityHolds: false, now: DateTime.UtcNow);
        t.Observe("sig1", rule, identityHolds: true,  now: DateTime.UtcNow.AddSeconds(1));
        Assert.Null(t.Observe("sig1", rule, identityHolds: false, now: DateTime.UtcNow.AddSeconds(2)));
    }
}
```

- [ ] **Step 2: Run; expect FAIL**

- [ ] **Step 3: Implement tracker (ConcurrentDictionary<sig, deque of (timestamp, holds)>)**

```csharp
// src/Mostlylucid.BotDetection/Risk/IdentityContinuityTracker.cs
using System.Collections.Concurrent;
using Mostlylucid.BotDetection.Identity;
namespace Mostlylucid.BotDetection.Risk;
public sealed class IdentityContinuityTracker
{
    private readonly int _minViolations;
    private readonly ConcurrentDictionary<string, List<(DateTime At, bool Holds)>> _state = new();
    public IdentityContinuityTracker(int minViolations) => _minViolations = minViolations;
    public string? Observe(string sig, IdentityContinuityRule rule, bool identityHolds, DateTime now)
    {
        var entry = _state.GetOrAdd(sig, _ => new());
        lock (entry)
        {
            entry.RemoveAll(e => now - e.At > rule.TimeWindow);
            entry.Add((now, identityHolds));
            var streak = 0;
            for (int i = entry.Count - 1; i >= 0; i--)
            {
                if (entry[i].Holds) break;
                streak++;
            }
            return streak >= _minViolations ? rule.BreakAction : null;
        }
    }
}
```

- [ ] **Step 4: Wire tracker into evaluator: add new constructor param + return BreakAction**

```csharp
// modify ArchetypeAlignmentEvaluator constructor + Evaluate to take primarySignature + DateTime now,
// call tracker if archetype.IdentityContinuity is not null, set result.BreakAction.
```

- [ ] **Step 5: Run; expect 2 passing**

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Risk/IdentityContinuityTracker.cs src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentEvaluator.cs src/Mostlylucid.BotDetection.Test/Risk/IdentityContinuityTrackerTests.cs
git commit -m "feat(risk): identity-continuity sliding window + break_action"
```

### Task 2.3: Emit alignment signals onto blackboard

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/SignalKeys.cs`
- Modify: orchestrator wave that runs after archetype match (likely `Orchestration/Waves/IdentityWave.cs` — locate via grep `IdentityArchetypeRegistry.FindNearest`)
- Test: `src/Mostlylucid.BotDetection.Test/Risk/AlignmentSignalEmissionTests.cs`

- [ ] **Step 1: Add SignalKeys constants**

```csharp
// append to src/Mostlylucid.BotDetection/Models/SignalKeys.cs
public const string ArchetypeAlignmentIdentityHolds = "archetype.alignment.identity_holds";
public const string ArchetypeAlignmentBehaviorAligned = "archetype.alignment.behavior_aligned";
public const string ArchetypeAlignmentWeakDeviation = "archetype.alignment.weak_deviation";
public const string ArchetypeAlignmentBreakAction = "archetype.alignment.break_action";
public const string ArchetypeAlignmentDeviatedSlots = "archetype.alignment.deviated_slots";
```

- [ ] **Step 2: Write failing test — orchestrator wave emits signals**

```csharp
// AlignmentSignalEmissionTests: build a minimal BlackboardState with a
// known archetype anchor, run the wave, assert state.Signals contains
// the four ArchetypeAlignment* keys.
```

- [ ] **Step 3: Run; expect FAIL**

- [ ] **Step 4: Wire evaluator into the wave (Read existing wave that calls FindNearest; append evaluator call + state.WriteSignals(...))**

- [ ] **Step 5: Run; expect PASS**

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(orchestration): emit archetype alignment signals to blackboard"
```

---

## Phase 3 — Composer wiring + supersede the immediate-fix clamp

### Task 3.1: Extend SignatureRiskInputs

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Risk/SignatureRiskVerdict.cs`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/DetectionLedgerExtensions.cs` (populator)
- Test: `src/Mostlylucid.BotDetection.Test/Risk/TrustedAndAlignedClampTests.cs`

- [ ] **Step 1: Append 3 fields to SignatureRiskInputs**

```csharp
public bool IdentityHolds { get; init; } = true;     // default true so omission means "no archetype data — assume aligned"
public bool BehaviorAligned { get; init; } = true;
public double WeakDeviationScore { get; init; } = 0.0;
```

- [ ] **Step 2: Populate from blackboard in DetectionLedgerExtensions**

```csharp
// in the SignatureRiskInputs build call, read signals[SignalKeys.ArchetypeAlignment*]
// and pass into the inputs record. Falls back to default-true when absent.
```

- [ ] **Step 3: Commit**

```bash
git commit -am "feat(risk): SignatureRiskInputs carries archetype alignment"
```

### Task 3.2: Replace Internal-only clamp with trusted-and-aligned clamp

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Risk/SignatureRiskVerdictComposer.cs`
- Modify: `src/Mostlylucid.BotDetection.Test/Risk/InternalRiskBandClampTests.cs` — update assertions

- [ ] **Step 1: Write failing test that documents new semantics**

```csharp
// src/Mostlylucid.BotDetection.Test/Risk/TrustedAndAlignedClampTests.cs
public class TrustedAndAlignedClampTests
{
    [Fact]
    public void Internal_with_alignment_clamps_to_Low()
    {
        var inputs = NewInputs(botType: nameof(BotType.Internal), identityHolds: true, behaviorAligned: true);
        var v = SignatureRiskVerdictComposer.Compose(inputs);
        Assert.True(v.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, v.RiskBand);
    }
    [Fact]
    public void Internal_with_behavior_deviation_does_NOT_clamp()
    {
        var inputs = NewInputs(botType: nameof(BotType.Internal), identityHolds: true, behaviorAligned: false);
        var v = SignatureRiskVerdictComposer.Compose(inputs);
        Assert.False(v.FriendlyPinFired);
    }
    [Fact]
    public void Internal_with_identity_break_does_NOT_clamp()
    {
        var inputs = NewInputs(botType: nameof(BotType.Internal), identityHolds: false, behaviorAligned: true);
        var v = SignatureRiskVerdictComposer.Compose(inputs);
        Assert.False(v.FriendlyPinFired);
    }
    private static SignatureRiskInputs NewInputs(string botType, bool identityHolds, bool behaviorAligned) => new()
    {
        PrimarySignature = "t", BotProbability = 1.0, Confidence = 1.0,
        RawThreatScore = 0, FriendlyVerified = false, ConfirmedBad = false,
        DeclaredBot = false, BotType = botType,
        IdentityHolds = identityHolds, BehaviorAligned = behaviorAligned,
    };
}
```

- [ ] **Step 2: Run; expect compile fail (IdentityHolds/BehaviorAligned not yet on inputs ⇒ Task 3.1 covers, then logic fail)**

- [ ] **Step 3: Replace clamp branch in Compose**

```csharp
// REMOVE the immediate-fix branch added at a2913eed:
//   else if (inputs.BotType == nameof(BotType.Internal)) { ... }
// REPLACE with:
else if ((inputs.BotType == nameof(BotType.Internal) || inputs.BotType == nameof(BotType.VerifiedBot))
         && inputs.IdentityHolds
         && inputs.BehaviorAligned)
{
    friendlyPin = true;
    friendlyWhy = $"{inputs.BotType}: identity holds + behavior aligned";
    reasons.Add($"trusted_and_aligned: {inputs.BotType}");
}
```

- [ ] **Step 4: Update `InternalRiskBandClampTests` — the 4 surviving tests get IdentityHolds=true, BehaviorAligned=true defaults so they still pass; rename file to `TrustedAndAlignedClampTests.cs` (delete old, keep new)**

- [ ] **Step 5: Run full test suite; expect 0 fails**

```
dotnet test src/Mostlylucid.BotDetection.Test
```

- [ ] **Step 6: Commit**

```bash
git commit -am "fix(risk): trusted-and-aligned clamp supersedes a2913eed Internal-only branch"
```

---

## Phase 4 — MultiFactorSignatures integration

### Task 4.1: MFS reads `break_action` as split bias

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Identity/MultiFactorSignatureService.cs` (locate via grep)
- Test: `src/Mostlylucid.BotDetection.Test/Identity/MfsBreakActionBiasTests.cs`

- [ ] **Step 1: Write failing test — `break_action="split"` biases match toward split**

```csharp
public class MfsBreakActionBiasTests
{
    [Fact]
    public void Break_action_split_returns_split_decision()
    {
        var ctx = MakeMatchContext(signal: SignalKeys.ArchetypeAlignmentBreakAction, value: "split");
        var decision = sut.Match(ctx);
        Assert.Equal(MatchDecision.Split, decision);
    }
    [Fact]
    public void No_break_action_preserves_existing_partial_match()
    {
        var ctx = MakeMatchContext(signal: null, value: null);
        var decision = sut.Match(ctx);
        Assert.Equal(MatchDecision.PartialRescue, decision);
    }
}
```

- [ ] **Step 2: Run; expect FAIL**

- [ ] **Step 3: Add break-action read in match path**

```csharp
// in MFS Match method, before invoking partial-rescue logic:
if (signals.TryGetValue(SignalKeys.ArchetypeAlignmentBreakAction, out var b) && (b as string) == "split")
    return MatchDecision.Split;
```

- [ ] **Step 4: Run; expect PASS**

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(mfs): consume archetype.alignment.break_action as split bias"
```

---

## Phase 5 — Per-signature pins

### Task 5.1: signature_pins schema (sqlite + postgres migrations)

**Files:**
- Create: `src/Mostlylucid.BotDetection.Migrations.Sqlite/Migrations/202606200001_AddSignaturePins.cs`
- Create: `src/Mostlylucid.BotDetection.Migrations.Postgres/Migrations/202606200001_AddSignaturePins.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Storage/SignaturePinsMigrationTests.cs`

- [ ] **Step 1: Write failing test — table exists after migration**

```csharp
public class SignaturePinsMigrationTests
{
    [Fact]
    public async Task Sqlite_migration_creates_table()
    {
        using var conn = await SqliteTestHost.OpenAsync();
        await SqliteMigrationRunner.RunAsync(conn);
        var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='signature_pins'");
        Assert.Equal(1, count);
    }
}
```

- [ ] **Step 2: Run; expect FAIL**

- [ ] **Step 3: Write migration SQL (CREATE TABLE signature_pins with composite PK + indexes)**

```sql
-- sqlite
CREATE TABLE IF NOT EXISTS signature_pins (
    primary_signature TEXT NOT NULL,
    dimension_slot    TEXT NOT NULL,
    expected_json     TEXT NOT NULL,
    tolerance_json    TEXT NOT NULL,
    drift_role        TEXT NOT NULL,
    weight            REAL NOT NULL DEFAULT 1.0,
    created_at        TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by        TEXT,
    expires_at        TEXT,
    PRIMARY KEY (primary_signature, dimension_slot)
);
CREATE INDEX IF NOT EXISTS ix_pins_expires ON signature_pins(expires_at);
```

Postgres analogue uses `JSONB` columns and `TIMESTAMPTZ`.

- [ ] **Step 4: Run tests; expect PASS for both backends**

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(storage): signature_pins schema (sqlite + postgres)"
```

### Task 5.2: IFingerprintStore pin methods + impls

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Storage/IFingerprintStore.cs`
- Create: `src/Mostlylucid.BotDetection/Storage/SignaturePin.cs`
- Modify: `src/Mostlylucid.BotDetection/Storage/SqliteFingerprintStore.cs` + `PostgresFingerprintStore.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Storage/SignaturePinReadWriteTests.cs`

- [ ] **Step 1: Define SignaturePin record + interface methods**

```csharp
public sealed record SignaturePin
{
    public required string PrimarySignature { get; init; }
    public required string DimensionSlot { get; init; }
    public required string ExpectedJson { get; init; }
    public required string ToleranceJson { get; init; }
    public required string DriftRole { get; init; }
    public double Weight { get; init; } = 1.0;
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ExpiresAt { get; init; }
}

// IFingerprintStore additions:
Task<IReadOnlyList<SignaturePin>> GetSignaturePinsAsync(string primarySignature, CancellationToken ct);
Task WriteSignaturePinAsync(SignaturePin pin, CancellationToken ct);
Task DeleteSignaturePinAsync(string primarySignature, string dimensionSlot, CancellationToken ct);
```

- [ ] **Step 2: Write failing roundtrip test**

```csharp
[Fact]
public async Task Roundtrip_pin_write_then_read()
{
    var store = SqliteTestHost.NewStore();
    var pin = new SignaturePin { PrimarySignature="sig1", DimensionSlot="session.request_count",
        ExpectedJson="60", ToleranceJson="{\"shape\":\"range\",\"lower\":5,\"upper\":500}",
        DriftRole="behavior", CreatedAt=DateTime.UtcNow };
    await store.WriteSignaturePinAsync(pin, default);
    var read = await store.GetSignaturePinsAsync("sig1", default);
    Assert.Single(read);
    Assert.Equal("session.request_count", read[0].DimensionSlot);
}
```

- [ ] **Step 3: Run; expect FAIL**

- [ ] **Step 4: Implement Dapper UPSERT + SELECT in both stores**

- [ ] **Step 5: Run; expect PASS**

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(storage): IFingerprintStore pin read/write/delete"
```

### Task 5.3: Wire pin reads into evaluator composition

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Risk/ArchetypeAlignmentEvaluator.cs` or its caller wave
- Test: extend `ArchetypeAlignmentEvaluatorTests` with a Tier 3 override case

- [ ] **Step 1: Write failing test — Tier 3 pin wins over Tier 2 archetype**

```csharp
[Fact]
public async Task Pin_overrides_archetype_dimension()
{
    var arch = Composed(new() { Slot="session.request_count", Expected=10.0,
        Tolerance=DimensionTolerance.Range(0, 30), DriftRole=DriftRole.Behavior });
    var pin = new SignaturePin { ... ExpectedJson="200", ToleranceJson="{shape:range, lower:50, upper:500}", ... };
    // store the pin, then run the wave for sig1, expect behavior aligned at observed=200
}
```

- [ ] **Step 2: Run; expect FAIL**

- [ ] **Step 3: In the wave's archetype composition call, fetch pins from `IFingerprintStore.GetSignaturePinsAsync` and pass to `ComposedArchetype.Compose`**

- [ ] **Step 4: Run; expect PASS**

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(risk): Tier-3 signature_pins layered into ComposedArchetype"
```

### Task 5.4: Dashboard pin write endpoint

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`
- Test: `src/Mostlylucid.BotDetection.Test/UI/PinWriteEndpointTests.cs`

- [ ] **Step 1: Write failing test — POST `/api/v1/signatures/{sig}/pin` persists**

- [ ] **Step 2: Add route handler (admin-only, calls `WriteSignaturePinAsync`)**

- [ ] **Step 3: Run; expect PASS**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(dashboard): POST /api/v1/signatures/{sig}/pin"
```

---

## Phase 6 — UI panel

### Task 6.1: SbExpectedBehavior view component

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Models/ExpectedBehaviorViewModel.cs`
- Create: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbExpectedBehavior/SbExpectedBehaviorViewComponent.cs`
- Create: `src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbExpectedBehavior/Default.cshtml`

- [ ] **Step 1: Define ViewModel**

```csharp
public sealed record ExpectedBehaviorViewModel
{
    public required string PrimarySignature { get; init; }
    public required IReadOnlyList<ExpectedBehaviorRow> Rows { get; init; }
    public required bool IdentityHolds { get; init; }
    public required bool BehaviorAligned { get; init; }
    public string? BreakAction { get; init; }
    public string? VendorDocsUrl { get; init; }
}
public sealed record ExpectedBehaviorRow
{
    public required string Slot { get; init; }
    public required string ExpectedDisplay { get; init; }
    public required string ObservedDisplay { get; init; }
    public required bool Aligned { get; init; }
    public required string DriftRole { get; init; }
}
```

- [ ] **Step 2: ViewComponent calls `IDashboardEventStore.GetExpectedBehaviorAsync(sig)` (new REST endpoint) and renders**

- [ ] **Step 3: Default.cshtml lays out the table per spec UI section (table with Slot / Expected / Observed / Delta / Status columns; identity row gets a lock icon; non-aligned rows get error color)**

- [ ] **Step 4: Visual smoke test — start dev server, load /dashboard/signature/<known-sig>, verify panel renders with expected vs observed values**

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbExpectedBehavior/ src/Mostlylucid.BotDetection.UI/Models/ExpectedBehaviorViewModel.cs
git commit -m "feat(ui): SbExpectedBehavior view component"
```

### Task 6.2: Mount in signature detail + browser verification

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml`

- [ ] **Step 1: Inject view component invocation above "Detection Signals" section**

```cshtml
@await Component.InvokeAsync("SbExpectedBehavior", new { signature = Model.SignatureId })
```

- [ ] **Step 2: Restart dev server; verify panel appears, expected/observed values render, identity-pinned dimensions show lock icon, deviated rows highlight**

- [ ] **Step 3: Run end-to-end Playwright assertion (extend HumanBotNameResolutionTests fixture or new file)**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(ui): mount SbExpectedBehavior on signature detail page"
```

### Task 6.3: Inline pin form (per-row pin action)

**Files:**
- Modify: `SbExpectedBehavior/Default.cshtml` — add HTMX form on each row
- Modify: dashboard endpoint to handle the form POST
- Test: `src/Mostlylucid.BotDetection.Test/UI/PinFormE2ETests.cs`

- [ ] **Step 1: Each row gets a "Pin this value" button that opens an inline HTMX form prefilled with observed**

- [ ] **Step 2: Form POSTs to `/api/v1/signatures/{sig}/pin`; success triggers HTMX swap to refresh the row showing pin badge**

- [ ] **Step 3: E2E test: click pin button, fill form, verify SQL row exists + UI shows pin badge**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(ui): inline per-row pin form on Expected Behavior panel"
```

---

## Final task: documentation + spec follow-through

### Task 7.1: Update architecture doc + delete legacy clamp

**Files:**
- Modify: `docs/architecture/signal-contracts.md` — append `archetype.alignment.*` signals
- Modify: `src/Mostlylucid.BotDetection/Risk/SignatureRiskVerdictComposer.cs` — delete `// Spec basis: section 3...` comment block from the immediate-fix branch (already removed in Task 3.2)
- Modify: docs/superpowers/specs/<this-spec>.md — append "Implemented in plan <date>; commits <range>"

- [ ] **Step 1: Append signal contract entries**
- [ ] **Step 2: Append spec implementation footer**
- [ ] **Step 3: Commit**

```bash
git commit -am "docs: archetype.alignment signal contracts + spec implementation footer"
```

---

## Acceptance criteria (per spec)

- [ ] StyloBot Internal on dashboard shows Risk Profile Low (replaced by VeryLow once the evaluator is consumed by the composer in Phase 3)
- [ ] Signature detail page has Expected Behavior panel showing per-slot expected vs observed
- [ ] Googlebot deviating from search-engine archetype lifts RiskBand and surfaces deviated slot in panel
- [ ] Per-signature pin written from dashboard composes correctly (Tier 3 ▸ Tier 2 ▸ Tier 1)
- [ ] All existing FOSS tests pass; MFS partial-match behavior is byte-identical when no `break_action` signal
- [ ] Verification script `/tmp/verify-staging.sh` (extended with archetype-alignment checks) reports 0 FAILs

---

## Notes for the executor

- Each phase produces working software. Commit after each task. Run the full FOSS test suite at every phase boundary (Phase 1 → 2, 2 → 3, etc.).
- Deploy to staging between Phase 3 and Phase 4 (composer wiring is the user-visible Risk fix; ship it). Then Phase 4–6 follow.
- Where a step says "locate via grep", use exact command: `grep -rn "<symbol>" --include="*.cs" src/` from the FOSS repo root.
- Pin form (Task 6.3) requires admin auth; the dashboard already gates `/api/v1/signatures/*` writes through `StyloBotDashboardOptions.AdminPolicy` — reuse, don't invent.
- After Phase 3 ships, the `a2913eed` immediate-fix clamp branch is deleted and its test renamed/extended (Task 3.2 step 4) — do not leave both clamps in the composer.