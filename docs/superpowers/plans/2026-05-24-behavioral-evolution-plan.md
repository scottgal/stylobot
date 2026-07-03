# Behavioral Evolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the three separate Behavioral History / Behavioral Sessions / Behavioral Shape panels on the Stylobot signature-detail page with a single unified "Behavioral Evolution" card that overlays sessions as opacity-faded ghost polygons on a 12-axis clock radar.

**Architecture:** Server-side composes a 12-axis vector per session (8 semantic + 4 distilled Markov) and emits `clockAxes` on the existing `/api/sessions/signature/{id}` endpoint. A new Razor partial (`_BehavioralEvolution.cshtml`) renders one ApexCharts radar with multiple series (current + ghosts) and a vertical session card stack to the right. Every magic number lives on `BehavioralEvolutionOptions`, nested on `StyloBotDashboardOptions`.

**Tech Stack:** .NET 10 / ASP.NET Core middleware (FOSS), Razor partials, ApexCharts (already loaded by the dashboard), HTMX, SignalR, xUnit + Moq for tests. Branch policy: **commit on `main`, do not auto-branch**.

**Spec:** `docs/superpowers/specs/2026-05-24-behavioral-evolution-design.md`

---

## Pre-flight

- [ ] Confirm you are on `main` in `/Users/scottgalloway/RiderProjects/stylobot` (the FOSS repo). All commits in this plan go there.
- [ ] Run the baseline build to confirm a clean starting state:
  ```
  dotnet build /Users/scottgalloway/RiderProjects/stylobot/Mostlylucid.BotDetection.slnx -nologo
  ```
  Expected: build succeeds.
- [ ] Run the existing UI test suite to confirm baseline green:
  ```
  dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~UI" -nologo
  ```
  Expected: all tests pass.

---

## Phase 1: Options + projection helpers (TDD)

### Task 1: Add `BehavioralEvolutionOptions` and nest on dashboard options

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Configuration/BehavioralEvolutionOptions.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs`

- [ ] **Step 1.1: Create the options class**

Write `src/Mostlylucid.BotDetection.UI/Configuration/BehavioralEvolutionOptions.cs`:

```csharp
namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     Tuning knobs for the Behavioral Evolution panel on the signature-detail page.
///     Bound from <c>BotDetection:Dashboard:BehavioralEvolution</c>. Every magic number
///     the partial reads at render time lives here -- the partial emits the values into
///     <c>data-*</c> attributes which the inline script reads at boot.
/// </summary>
public sealed class BehavioralEvolutionOptions
{
    /// <summary>Most-recent sessions overlaid on the radar. Older sessions still appear in the right-column list but are not drawn.</summary>
    public int MaxOverlaySessions { get; set; } = 5;

    /// <summary>Half-life of ghost opacity in minutes. A session this many minutes old renders at half its peak intensity.</summary>
    public double HalfLifeMinutes { get; set; } = 240;

    public double MinGhostOpacity { get; set; } = 0.03;
    public double MaxGhostOpacity { get; set; } = 0.65;
    public double MinStrokeOpacity { get; set; } = 0.20;
    public double FocusFillOpacity { get; set; } = 0.20;
    public double FocusStrokeOpacity { get; set; } = 1.00;
    public double CurrentStrokeWidth { get; set; } = 2.5;
    public double GhostStrokeWidth { get; set; } = 1.0;

    /// <summary>Milliseconds between session focuses while Play is running.</summary>
    public int PlayIntervalMs { get; set; } = 1500;

    /// <summary>Number of concentric reference rings on the radar.</summary>
    public int RingCount { get; set; } = 4;

    /// <summary>Ghosts older than this shift from teal to slate-blue, signalling "different era".</summary>
    public double BlueShiftAfterMinutes { get; set; } = 720;

    public bool ShowQuadrantBackgrounds { get; set; } = true;
    public bool ShowAxisLegend { get; set; } = true;
    public bool ShowMetricsStrip { get; set; } = true;
}
```

- [ ] **Step 1.2: Nest the new options on `StyloBotDashboardOptions`**

Open `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs`. Find the end of the existing properties (just before the closing brace of the class) and add:

```csharp
    /// <summary>
    ///     Tuning for the Behavioral Evolution panel on the signature-detail page.
    /// </summary>
    public BehavioralEvolutionOptions BehavioralEvolution { get; set; } = new();
```

- [ ] **Step 1.3: Build to verify wiring**

Run:
```
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI -nologo
```
Expected: build succeeds, no warnings.

- [ ] **Step 1.4: Commit**

```
git add src/Mostlylucid.BotDetection.UI/Configuration/BehavioralEvolutionOptions.cs \
        src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs
git commit -m "feat(ui): add BehavioralEvolutionOptions for radar panel tuning"
```

---

### Task 2: `ClockProjection.ProjectMarkovTo4Axes` (TDD)

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Services/ClockProjection.cs`
- Create: `src/Mostlylucid.BotDetection.Test/UI/Primitives/ClockProjectionTests.cs`

Markov state-freq layout (matches the filmstrip axis labels in `_SessionFingerprints.cshtml`):
`0=Page 1=API 2=Asset 3=WS 4=SignalR 5=SSE 6=Form 7=Auth 8=404 9=Search`.

The 4-axis projection is:
- `Asset` = `s[2]`
- `Realtime` = `s[3] + s[4] + s[5]`
- `Form/Search` = `s[6] + s[9]`
- `404` = `s[8]`

Each result clamped to `[0, 1]`.

- [ ] **Step 2.1: Write the failing test file**

Write `src/Mostlylucid.BotDetection.Test/UI/Primitives/ClockProjectionTests.cs`:

```csharp
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class ClockProjectionTests
{
    [Fact]
    public void ProjectMarkov_zero_input_returns_four_zeros()
    {
        var result = ClockProjection.ProjectMarkovTo4Axes(new float[10]);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, result);
    }

    [Fact]
    public void ProjectMarkov_null_input_returns_four_zeros()
    {
        var result = ClockProjection.ProjectMarkovTo4Axes(null!);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, result);
    }

    [Fact]
    public void ProjectMarkov_short_input_returns_four_zeros()
    {
        var result = ClockProjection.ProjectMarkovTo4Axes(new float[3]);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 0.0 }, result);
    }

    [Fact]
    public void ProjectMarkov_isolates_asset_share()
    {
        var freqs = new float[10];
        freqs[2] = 0.4f;
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.4, result[0], 5);
        Assert.Equal(0.0, result[1], 5);
        Assert.Equal(0.0, result[2], 5);
        Assert.Equal(0.0, result[3], 5);
    }

    [Fact]
    public void ProjectMarkov_sums_realtime_channels()
    {
        var freqs = new float[10];
        freqs[3] = 0.2f;   // WS
        freqs[4] = 0.10f;  // SignalR
        freqs[5] = 0.05f;  // SSE
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.35, result[1], 5);
    }

    [Fact]
    public void ProjectMarkov_sums_form_and_search()
    {
        var freqs = new float[10];
        freqs[6] = 0.3f;   // Form
        freqs[9] = 0.2f;   // Search
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.5, result[2], 5);
    }

    [Fact]
    public void ProjectMarkov_passes_404_share_through()
    {
        var freqs = new float[10];
        freqs[8] = 0.7f;
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(0.7, result[3], 5);
    }

    [Fact]
    public void ProjectMarkov_clamps_realtime_to_one()
    {
        var freqs = new float[10];
        freqs[3] = 0.7f;
        freqs[4] = 0.7f; // 1.4 → should clamp to 1.0
        var result = ClockProjection.ProjectMarkovTo4Axes(freqs);
        Assert.Equal(1.0, result[1], 5);
    }
}
```

- [ ] **Step 2.2: Run test to verify it fails (no `ClockProjection` type yet)**

```
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test \
  --filter "FullyQualifiedName~ClockProjectionTests" -nologo
```
Expected: build error - `The type or namespace name 'ClockProjection' could not be found`. That is the failing state we want.

- [ ] **Step 2.3: Write minimal implementation**

Write `src/Mostlylucid.BotDetection.UI/Services/ClockProjection.cs`:

```csharp
namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds the 12-axis "clock" vector used by the Behavioral Evolution panel.
///     <para>
///     The clock interleaves the 8-axis semantic projection (existing
///     <c>VectorRadarProjection</c> output) with 4 distilled Markov state shares
///     so a single radar can show both "what the detectors are saying" and
///     "what the visitor is fetching" without two charts.
///     </para>
/// </summary>
public static class ClockProjection
{
    // State-freq index layout matches the filmstrip axis labels:
    // 0=Page 1=API 2=Asset 3=WS 4=SignalR 5=SSE 6=Form 7=Auth 8=404 9=Search
    private const int IdxAsset    = 2;
    private const int IdxWs       = 3;
    private const int IdxSignalR  = 4;
    private const int IdxSse      = 5;
    private const int IdxForm     = 6;
    private const int Idx404      = 8;
    private const int IdxSearch   = 9;

    /// <summary>
    ///     Returns <c>[ Asset, Realtime, Form/Search, 404 ]</c>, each clamped to [0,1].
    ///     Returns four zeros when <paramref name="stateFreqs"/> is null or shorter than 10.
    /// </summary>
    public static double[] ProjectMarkovTo4Axes(float[] stateFreqs)
    {
        if (stateFreqs is null || stateFreqs.Length < 10)
            return new[] { 0.0, 0.0, 0.0, 0.0 };

        var asset    = Clamp01(stateFreqs[IdxAsset]);
        var realtime = Clamp01(stateFreqs[IdxWs] + stateFreqs[IdxSignalR] + stateFreqs[IdxSse]);
        var forms    = Clamp01(stateFreqs[IdxForm] + stateFreqs[IdxSearch]);
        var notFound = Clamp01(stateFreqs[Idx404]);

        return new[] { asset, realtime, forms, notFound };
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
}
```

- [ ] **Step 2.4: Run test to verify it passes**

```
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test \
  --filter "FullyQualifiedName~ClockProjectionTests" -nologo
```
Expected: all 8 tests pass.

- [ ] **Step 2.5: Commit**

```
git add src/Mostlylucid.BotDetection.UI/Services/ClockProjection.cs \
        src/Mostlylucid.BotDetection.Test/UI/Primitives/ClockProjectionTests.cs
git commit -m "feat(ui): ClockProjection.ProjectMarkovTo4Axes"
```

---

### Task 3: `ClockProjection.Compose12Axes` (TDD)

Clock-hour to source-index mapping:

| Hour | Index | Source |
|------|-------|--------|
| 12 | 0 | `semantic[0]` Browsing |
| 1 | 1 | `semantic[1]` API Activity |
| 2 | 2 | `markov[0]` Asset |
| 3 | 3 | `markov[1]` Realtime |
| 4 | 4 | `markov[2]` Form/Search |
| 5 | 5 | `semantic[3]` Auth Pressure |
| 6 | 6 | `semantic[5]` Burst Speed |
| 7 | 7 | `semantic[4]` Timing |
| 8 | 8 | `semantic[7]` Path Diversity |
| 9 | 9 | `markov[3]` 404 |
| 10 | 10 | `semantic[2]` Scan/Probe |
| 11 | 11 | `semantic[6]` Fingerprint |

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/ClockProjection.cs`
- Modify: `src/Mostlylucid.BotDetection.Test/UI/Primitives/ClockProjectionTests.cs`

- [ ] **Step 3.1: Append failing tests**

Append to `ClockProjectionTests.cs`:

```csharp
    [Fact]
    public void Compose12Axes_places_each_source_at_its_clock_hour()
    {
        // Distinct values per slot so any swap shows up clearly.
        var semantic = new[] { 0.10, 0.11, 0.12, 0.13, 0.14, 0.15, 0.16, 0.17 };
        var markov   = new[] { 0.21, 0.22, 0.23, 0.24 };

        var clock = ClockProjection.Compose12Axes(semantic, markov);

        Assert.Equal(12, clock.Length);
        Assert.Equal(0.10, clock[0],  5); // 12 Browsing       ← semantic[0]
        Assert.Equal(0.11, clock[1],  5); //  1 API Activity   ← semantic[1]
        Assert.Equal(0.21, clock[2],  5); //  2 Asset          ← markov[0]
        Assert.Equal(0.22, clock[3],  5); //  3 Realtime       ← markov[1]
        Assert.Equal(0.23, clock[4],  5); //  4 Form/Search    ← markov[2]
        Assert.Equal(0.13, clock[5],  5); //  5 Auth Pressure  ← semantic[3]
        Assert.Equal(0.15, clock[6],  5); //  6 Burst Speed    ← semantic[5]
        Assert.Equal(0.14, clock[7],  5); //  7 Timing         ← semantic[4]
        Assert.Equal(0.17, clock[8],  5); //  8 Path Diversity ← semantic[7]
        Assert.Equal(0.24, clock[9],  5); //  9 404 Share      ← markov[3]
        Assert.Equal(0.12, clock[10], 5); // 10 Scan/Probe     ← semantic[2]
        Assert.Equal(0.16, clock[11], 5); // 11 Fingerprint    ← semantic[6]
    }

    [Fact]
    public void Compose12Axes_null_semantic_yields_zero_for_semantic_hours_only()
    {
        var markov = new[] { 0.5, 0.5, 0.5, 0.5 };
        var clock = ClockProjection.Compose12Axes(null!, markov);

        Assert.Equal(0.0, clock[0]);   // 12 semantic
        Assert.Equal(0.5, clock[2]);   //  2 markov
        Assert.Equal(0.0, clock[5]);   //  5 semantic
        Assert.Equal(0.5, clock[9]);   //  9 markov
    }

    [Fact]
    public void Compose12Axes_null_markov_yields_zero_for_markov_hours_only()
    {
        var semantic = new[] { 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };
        var clock = ClockProjection.Compose12Axes(semantic, null!);

        Assert.Equal(0.5, clock[0]);   // 12 semantic
        Assert.Equal(0.0, clock[2]);   //  2 markov
        Assert.Equal(0.5, clock[5]);   //  5 semantic
        Assert.Equal(0.0, clock[9]);   //  9 markov
    }

    [Fact]
    public void Compose12Axes_clamps_inputs_to_zero_one()
    {
        var semantic = new[] { 1.5, -0.2, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5 };
        var markov   = new[] { 2.0, 0.5, 0.5, -1.0 };
        var clock = ClockProjection.Compose12Axes(semantic, markov);

        Assert.Equal(1.0, clock[0]);   // semantic[0] = 1.5 → 1.0
        Assert.Equal(0.0, clock[10]);  // semantic[2] = -0.2 → 0.0
        Assert.Equal(1.0, clock[2]);   // markov[0]   = 2.0 → 1.0
        Assert.Equal(0.0, clock[9]);   // markov[3]   = -1.0 → 0.0
    }
```

- [ ] **Step 3.2: Run tests to confirm they fail to compile**

```
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test \
  --filter "FullyQualifiedName~ClockProjectionTests" -nologo
```
Expected: build error - `'ClockProjection' does not contain a definition for 'Compose12Axes'`.

- [ ] **Step 3.3: Implement `Compose12Axes`**

Append inside the `ClockProjection` class in `Services/ClockProjection.cs`, above the `Clamp01` helper:

```csharp
    /// <summary>
    ///     Interleaves the 8-axis semantic projection with the 4-axis Markov projection
    ///     into the fixed 12-axis clock order. Hours are indexed 12 → 11 as positions 0 → 11.
    ///     Missing input arrays contribute zeros for their hours.
    /// </summary>
    public static double[] Compose12Axes(double[] semantic8, double[] markov4)
    {
        var v = new double[12];

        v[0]  = GetClamped(semantic8, 0);   // 12 Browsing
        v[1]  = GetClamped(semantic8, 1);   //  1 API Activity
        v[2]  = GetClamped(markov4,   0);   //  2 Asset Share
        v[3]  = GetClamped(markov4,   1);   //  3 Realtime Share
        v[4]  = GetClamped(markov4,   2);   //  4 Form / Search
        v[5]  = GetClamped(semantic8, 3);   //  5 Auth Pressure
        v[6]  = GetClamped(semantic8, 5);   //  6 Burst Speed
        v[7]  = GetClamped(semantic8, 4);   //  7 Timing
        v[8]  = GetClamped(semantic8, 7);   //  8 Path Diversity
        v[9]  = GetClamped(markov4,   3);   //  9 404 Share
        v[10] = GetClamped(semantic8, 2);   // 10 Scan / Probe
        v[11] = GetClamped(semantic8, 6);   // 11 Fingerprint

        return v;
    }

    private static double GetClamped(double[]? src, int i)
        => src is null || i >= src.Length ? 0.0 : Clamp01(src[i]);
```

- [ ] **Step 3.4: Run tests to verify pass**

```
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test \
  --filter "FullyQualifiedName~ClockProjectionTests" -nologo
```
Expected: all 12 tests pass (8 from Task 2 + 4 new).

- [ ] **Step 3.5: Commit**

```
git add src/Mostlylucid.BotDetection.UI/Services/ClockProjection.cs \
        src/Mostlylucid.BotDetection.Test/UI/Primitives/ClockProjectionTests.cs
git commit -m "feat(ui): ClockProjection.Compose12Axes interleaves semantic + markov"
```

---

### Task 4: Emit `clockAxes` on the sessions API

**File:** `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`

Three code paths in `ServeSignatureSessionsApiAsync` (starts at line 1834) build session entries:

1. **Finalised sessions** (around lines 1865–1894) - has `sessionVectors[idx]`, computes `radarAxes`.
2. **Live in-memory** (around lines 1909–1932) - has `liveVector`, computes `liveRadar`.
3. **Detection-fallback synthetic** (around lines 1987–2042) - no vector; builds `radarAxes` from detector contributions, or leaves null.

For each path the recipe is identical: derive a `stateFreqs10` slice (zeros if no vector) and a `semantic8` array (already computed locally), then call:
```csharp
var clockAxes = Mostlylucid.BotDetection.UI.Services.ClockProjection.Compose12Axes(
    semantic8,
    Mostlylucid.BotDetection.UI.Services.ClockProjection.ProjectMarkovTo4Axes(stateFreqs10));
```
and attach `clockAxes` to the anonymous entry.

A local helper added once at the top of the method keeps the slice DRY across paths.

- [ ] **Step 4.1: Add the `using` and a local slice helper at the top of `ServeSignatureSessionsApiAsync`**

Open `StyloBotDashboardMiddleware.cs`. Near the top of the file, in the existing usings, add:

```csharp
using Mostlylucid.BotDetection.UI.Services;
```
(Use `ClockProjection` unqualified in the method body if you prefer; otherwise leave fully qualified.)

Inside `ServeSignatureSessionsApiAsync`, just after the `var sessions = await sessionStore.GetSessionsAsync(...)` line (~line 1858), add a slice helper:

```csharp
        static float[] SliceStateFreqs(float[]? vector)
        {
            var sf = new float[10];
            if (vector is { Length: >= 110 })
                Array.Copy(vector, 100, sf, 0, 10);
            return sf;
        }
```

- [ ] **Step 4.2: Attach `clockAxes` in the finalised-sessions Select**

The Select projection (lines ~1860–1895) builds an anonymous object with `radarAxes` at the end. Modify the radar block to capture the value and compose the clock axes:

Replace:
```csharp
            // Radar projection for behavioral shape visualization
            radarAxes = s.Vector is { Length: > 0 }
                ? BotDetection.Analysis.VectorRadarProjection.Project(sessionVectors[idx]!)
                : null
        }).ToList<object>();
```

With:
```csharp
            // Radar projection for behavioral shape visualization
            radarAxes = s.Vector is { Length: > 0 }
                ? BotDetection.Analysis.VectorRadarProjection.Project(sessionVectors[idx]!)
                : null,
            // 12-axis clock: semantic projection + 4 Markov state-share projections.
            // Empty markov when no session vector → those 4 hours sit at the origin.
            clockAxes = ClockProjection.Compose12Axes(
                s.Vector is { Length: > 0 }
                    ? BotDetection.Analysis.VectorRadarProjection.Project(sessionVectors[idx]!)
                    : null!,
                ClockProjection.ProjectMarkovTo4Axes(SliceStateFreqs(sessionVectors[idx])))
        }).ToList<object>();
```

- [ ] **Step 4.3: Attach `clockAxes` to the live-in-memory entry**

In the live-session block (~line 1932), the anonymous object ends with `radarAxes = liveRadar`. Update it to:

```csharp
                radarAxes = liveRadar,
                clockAxes = ClockProjection.Compose12Axes(
                    liveRadar,
                    ClockProjection.ProjectMarkovTo4Axes(SliceStateFreqs(liveVector)))
            });
```

- [ ] **Step 4.4: Attach `clockAxes` to the detection-fallback synthetic entry**

In the detection-fallback synthetic-entry block (~line 2017–2042), the entry ends with `radarAxes`. Update it to:

```csharp
                        var entry = new
                        {
                            // ... (other fields unchanged) ...
                            paths,
                            radarAxes,
                            // No session vector yet: markov hours = zeros.
                            clockAxes = ClockProjection.Compose12Axes(
                                radarAxes ?? new double[8],
                                new double[] { 0, 0, 0, 0 })
                        };
```

(The exact line to insert before is `};` that closes the anonymous object - preserve every existing field.)

- [ ] **Step 4.5: Build**

```
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI -nologo
```
Expected: build succeeds.

- [ ] **Step 4.6: Smoke-test the API**

If you have a local dashboard running (FOSS standalone or the host app), curl the endpoint with a known signature id. Otherwise hit prod:
```
curl -s https://stylo.bot/_stylobot/api/sessions/signature/<known-sig-id> | jq '.[0] | {radarAxes: (.radarAxes|length), clockAxes: (.clockAxes|length)}'
```
Expected output:
```
{ "radarAxes": 8, "clockAxes": 12 }
```

- [ ] **Step 4.7: Commit**

```
git add src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "feat(ui): emit clockAxes[12] on /api/sessions/signature/{id}"
```

---

## Phase 2: View partial

### Task 5: `BehavioralEvolutionModel` partial model

**File:** Create `src/Mostlylucid.BotDetection.UI/Models/BehavioralEvolutionModel.cs`

This carries the signature id, base path, CSP nonce, and a flattened copy of the options into the view (so the partial does not depend on `IOptions<>` directly).

- [ ] **Step 5.1: Create the model**

```csharp
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for <c>_BehavioralEvolution.cshtml</c>. All numeric tuning
///     comes from <see cref="BehavioralEvolutionOptions"/> and is emitted
///     into <c>data-*</c> attributes on the root element.
/// </summary>
public sealed class BehavioralEvolutionModel
{
    public required string SignatureId { get; init; }
    public required string BasePath { get; init; }
    public required string CspNonce { get; init; }
    public required BehavioralEvolutionOptions Options { get; init; }
}
```

- [ ] **Step 5.2: Build + commit**

```
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI -nologo
git add src/Mostlylucid.BotDetection.UI/Models/BehavioralEvolutionModel.cs
git commit -m "feat(ui): BehavioralEvolutionModel view model"
```

---

### Task 6: `_BehavioralEvolution.cshtml` - shell + axis legend + metrics-strip skeleton

**File:** Create `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_BehavioralEvolution.cshtml`

The partial renders:
- Outer card with header (title, "N of M sessions overlaid", Play button)
- 2-column grid: radar slot (left, empty for now), session card stack slot (right, empty for now)
- Metrics strip with 6 cells
- 4-quadrant axis legend
- Root `<div id="behavioral-evolution" data-*>` with every options value the script reads

The inline script in the next task fills the radar and session stack. This task only renders the static skeleton.

- [ ] **Step 6.1: Write the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models
@model BehavioralEvolutionModel
@{
    var o = Model.Options;
}

<div id="behavioral-evolution"
     class="rounded-xl border p-4 mb-4"
     style="border-color: var(--sb-card-border); background: var(--sb-card-bg);"
     data-signature="@Model.SignatureId"
     data-base-path="@Model.BasePath"
     data-max-overlay="@o.MaxOverlaySessions"
     data-half-life-min="@o.HalfLifeMinutes"
     data-min-ghost-opacity="@o.MinGhostOpacity"
     data-max-ghost-opacity="@o.MaxGhostOpacity"
     data-min-stroke-opacity="@o.MinStrokeOpacity"
     data-focus-fill-opacity="@o.FocusFillOpacity"
     data-focus-stroke-opacity="@o.FocusStrokeOpacity"
     data-current-stroke="@o.CurrentStrokeWidth"
     data-ghost-stroke="@o.GhostStrokeWidth"
     data-play-interval-ms="@o.PlayIntervalMs"
     data-ring-count="@o.RingCount"
     data-blue-shift-min="@o.BlueShiftAfterMinutes">

    <!-- Header -->
    <div class="flex items-center justify-between mb-3">
        <h3 class="text-xs font-semibold text-base-content/70 uppercase">
            <i class="bx bx-shape-polygon text-sm" style="color: var(--sb-accent);"></i>
            Behavioral Evolution
        </h3>
        <div class="flex items-center gap-3 text-[10px] text-base-content/40">
            <span data-evolution-count>Loading sessions...</span>
            <button type="button"
                    class="btn btn-ghost btn-xs"
                    data-evolution-play
                    title="Animate through sessions">
                <i class="bx bx-play"></i>
            </button>
        </div>
    </div>

    <!-- Radar (left) + session stack (right) -->
    <div class="grid grid-cols-1 lg:grid-cols-[1fr_280px] gap-4">
        <div data-evolution-radar style="height: 420px;"></div>
        <div data-evolution-sessions class="text-xs">
            <div class="text-base-content/40 text-center py-4">Loading sessions...</div>
        </div>
    </div>

    @if (o.ShowMetricsStrip)
    {
        <!-- Focused-session metrics -->
        <div class="grid grid-cols-6 gap-3 mt-3 pt-3 border-t"
             style="border-color: var(--sb-card-divider);"
             data-evolution-metrics>
            @foreach (var label in new[] { "Duration", "Requests", "Dominant", "Bot Prob", "Maturity", "Entropy" })
            {
                <div>
                    <div class="text-[9px] uppercase tracking-wider text-base-content/40">@label</div>
                    <div class="text-xs font-mono text-base-content mt-0.5" data-metric="@label.ToLowerInvariant().Replace(' ', '-')">--</div>
                </div>
            }
        </div>
    }

    @if (o.ShowAxisLegend)
    {
        <!-- Axis legend, grouped by quadrant -->
        <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3 mt-3 pt-3 border-t text-[10px]"
             style="border-color: var(--sb-card-divider);">
            @{
                var quadrants = new (string Title, (int Hour, string Name, string Src)[] Axes)[]
                {
                    ("Footprint", new[] {
                        (12, "Browsing", "semantic"),
                        ( 1, "API Activity", "semantic"),
                        ( 2, "Asset Share", "markov")
                    }),
                    ("Surface", new[] {
                        ( 3, "Realtime Share", "markov"),
                        ( 4, "Form / Search", "markov"),
                        ( 5, "Auth Pressure", "semantic")
                    }),
                    ("Cadence", new[] {
                        ( 6, "Burst Speed", "semantic"),
                        ( 7, "Timing", "semantic"),
                        ( 8, "Path Diversity", "semantic")
                    }),
                    ("Signal", new[] {
                        ( 9, "404 Share", "markov"),
                        (10, "Scan / Probe", "semantic"),
                        (11, "Fingerprint", "semantic")
                    })
                };
            }
            @foreach (var q in quadrants)
            {
                <div>
                    <h4 class="text-[9px] uppercase tracking-wider mb-1" style="color: var(--sb-accent);">@q.Title</h4>
                    @foreach (var ax in q.Axes)
                    {
                        <div class="flex items-baseline gap-2 text-base-content/70">
                            <span class="font-mono text-base-content/30 w-4 text-right">@ax.Hour</span>
                            <span class="flex-1">@ax.Name</span>
                            <span class="text-base-content/30 text-[9px]">@ax.Src</span>
                        </div>
                    }
                </div>
            }
        </div>
    }
</div>
```

- [ ] **Step 6.2: Build to confirm the partial compiles**

```
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI -nologo
```
Expected: build succeeds.

- [ ] **Step 6.3: Commit**

```
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_BehavioralEvolution.cshtml
git commit -m "feat(ui): _BehavioralEvolution partial shell + axis legend"
```

---

### Task 7: Radar + session-stack render script

The inline script reads the `data-*` attributes on the root, fetches the sessions API, then renders both surfaces. ApexCharts is already loaded by the dashboard host page (`Index.cshtml`/`_SignatureDetail.cshtml`); the partial relies on that.

Render strategy:
- One ApexCharts radar instance with one series per visible session. Colours, fill opacities, stroke widths, stroke dash arrays are all arrays parallel to `series`.
- Right column: card per session built with DOM methods (no use of `Element.innerHTML` with untrusted strings - matches existing security pattern at `_SignatureDetail.cshtml:739`). Use `textContent`, `createElement`, and `appendChild` only.
- Focus state lives in a single closure variable `focusedId`; every render reads it.

**File:** modify `Views/StyloBot/Dashboard/_BehavioralEvolution.cshtml`

- [ ] **Step 7.1: Append the script block**

Add at the bottom of `_BehavioralEvolution.cshtml`, after the closing `</div>` of the root:

```cshtml
<script nonce="@Model.CspNonce">
(function () {
    var root = document.getElementById('behavioral-evolution');
    if (!root || typeof ApexCharts === 'undefined') return;

    var bp    = root.dataset.basePath || '';
    var sig   = root.dataset.signature || '';
    var cfg   = {
        maxOverlay:       parseInt(root.dataset.maxOverlay,        10) || 5,
        halfLifeMin:      parseFloat(root.dataset.halfLifeMin)        || 240,
        minGhostOpacity:  parseFloat(root.dataset.minGhostOpacity)    || 0.03,
        maxGhostOpacity:  parseFloat(root.dataset.maxGhostOpacity)    || 0.65,
        minStrokeOpacity: parseFloat(root.dataset.minStrokeOpacity)   || 0.20,
        focusFillOpacity: parseFloat(root.dataset.focusFillOpacity)   || 0.20,
        focusStrokeOpacity: parseFloat(root.dataset.focusStrokeOpacity) || 1.0,
        currentStroke:    parseFloat(root.dataset.currentStroke)      || 2.5,
        ghostStroke:      parseFloat(root.dataset.ghostStroke)        || 1.0,
        playIntervalMs:   parseInt(root.dataset.playIntervalMs,    10) || 1500,
        blueShiftMin:     parseFloat(root.dataset.blueShiftMin)       || 720
    };

    var radarEl    = root.querySelector('[data-evolution-radar]');
    var listEl     = root.querySelector('[data-evolution-sessions]');
    var countEl    = root.querySelector('[data-evolution-count]');
    var playBtn    = root.querySelector('[data-evolution-play]');
    var metricsEl  = root.querySelector('[data-evolution-metrics]');

    var axisLabels = [
        'Browsing','API Activity','Asset','Realtime','Form/Search','Auth Pressure',
        'Burst Speed','Timing','Path Diversity','404','Scan/Probe','Fingerprint'
    ];

    var allSessions = [];   // entire fetched list, newest first
    var visible     = [];   // subset overlaid on the radar (cap maxOverlay)
    var focusedId   = null; // id of currently focused session
    var chart       = null;
    var playTimer   = null;

    var ACCENT_TEAL = '#5ba3a3';
    var BLUE        = '#60a5fa';

    function clearChildren(el) {
        while (el.firstChild) el.removeChild(el.firstChild);
    }
    function ageMinutes(s) {
        var t = Date.parse(s.startedAt);
        return isNaN(t) ? 0 : Math.max(0, (Date.now() - t) / 60000);
    }
    function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }
    function colorFor(s, isFocus) {
        if (isFocus) return ACCENT_TEAL;
        return ageMinutes(s) > cfg.blueShiftMin ? BLUE : ACCENT_TEAL;
    }
    function ghostOpacity(s) {
        var decay = cfg.maxGhostOpacity * Math.exp(-ageMinutes(s) / cfg.halfLifeMin);
        return clamp(decay, cfg.minGhostOpacity, cfg.maxGhostOpacity);
    }

    function buildSeries() {
        return visible.map(function (s) {
            return {
                name: s.label || String(s.id),
                data: (s.clockAxes || new Array(12).fill(0)).map(function (v) { return Math.round(v * 100); })
            };
        });
    }
    function buildColors()       { return visible.map(function (s) { return colorFor(s, s.id === focusedId); }); }
    function buildFillOpacity()  {
        return visible.map(function (s) {
            return s.id === focusedId ? cfg.focusFillOpacity : ghostOpacity(s);
        });
    }
    function buildStrokeWidth() {
        return visible.map(function (s) { return s.id === focusedId ? cfg.currentStroke : cfg.ghostStroke; });
    }
    function buildDashArray() {
        // Oldest visible ghost gets dashed.
        var oldestId = visible.length > 1 ? visible[visible.length - 1].id : null;
        return visible.map(function (s) { return (s.id !== focusedId && s.id === oldestId) ? 4 : 0; });
    }

    function showEmptyRadar() {
        clearChildren(radarEl);
        var msg = document.createElement('div');
        msg.style.cssText = 'height:100%;display:flex;align-items:center;justify-content:center;color:rgba(148,163,184,0.5);font-size:11px';
        msg.textContent = 'No sessions yet.';
        radarEl.appendChild(msg);
    }

    function renderRadar() {
        if (visible.length === 0) { showEmptyRadar(); return; }
        var isDark = document.documentElement.getAttribute('data-theme') !== 'sb-light';
        var fg     = isDark ? '#94a3b8' : '#475569';

        var options = {
            chart:   { type: 'radar', height: 420, toolbar: { show: false }, animations: { enabled: false }, background: 'transparent', fontFamily: 'Inter, sans-serif' },
            series:  buildSeries(),
            colors:  buildColors(),
            xaxis:   { categories: axisLabels, labels: { style: { colors: Array(12).fill(fg), fontSize: '9px' } } },
            yaxis:   { show: false, min: 0, max: 100 },
            fill:    { opacity: buildFillOpacity() },
            stroke:  { width: buildStrokeWidth(), dashArray: buildDashArray() },
            markers: { size: 0 },
            legend:  { show: false },
            plotOptions: { radar: { polygons: { strokeColors: isDark ? 'rgba(148,163,184,0.10)' : 'rgba(15,23,42,0.08)', connectorColors: isDark ? 'rgba(148,163,184,0.10)' : 'rgba(15,23,42,0.08)' } } },
            tooltip:    { theme: isDark ? 'dark' : 'light', y: { formatter: function (v) { return v + '%'; } } },
            dataLabels: { enabled: false }
        };

        if (!chart) {
            chart = new ApexCharts(radarEl, options);
            chart.render();
        } else {
            chart.updateOptions(options);
        }
    }

    function formatWhen(iso) {
        var d = new Date(iso); if (isNaN(d)) return '';
        var spanMin = (Date.now() - d.getTime()) / 60000;
        if (spanMin < 1)       return 'now';
        if (spanMin < 60)      return Math.floor(spanMin)        + 'm';
        if (spanMin < 60 * 24) return Math.floor(spanMin / 60)   + 'h';
        return Math.floor(spanMin / (60 * 24)) + 'd';
    }
    function riskClass(band) {
        if (band === 'VeryHigh' || band === 'High') return 'text-error';
        if (band === 'Elevated' || band === 'Medium') return 'text-warning';
        if (band === 'Low')                            return 'text-success';
        return 'text-base-content/40';
    }

    function renderList() {
        clearChildren(listEl);
        if (allSessions.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'text-base-content/40 text-center py-4';
            empty.textContent = 'No sessions yet.';
            listEl.appendChild(empty);
            return;
        }
        allSessions.forEach(function (s) {
            var row = document.createElement('button');
            row.type = 'button';
            row.className = 'w-full text-left grid grid-cols-[10px_1fr_auto_auto] gap-2 items-center px-2 py-1.5 rounded hover:bg-base-200/50 ' +
                            (s.id === focusedId ? 'bg-base-200/70 border-l-2 border-primary pl-[6px]' : '');
            row.dataset.id = String(s.id);

            var swatch = document.createElement('span');
            swatch.style.width = '10px';
            swatch.style.height = '10px';
            swatch.style.borderRadius = '2px';
            swatch.style.background = colorFor(s, s.id === focusedId);
            swatch.style.opacity = String(s.id === focusedId ? 1 : ghostOpacity(s));

            var middle = document.createElement('div');
            var when = document.createElement('div');
            when.className = 'text-base-content/70';
            when.textContent = formatWhen(s.startedAt) + ' · ' + ((s.durationMinutes || 0).toFixed(1)) + 'm';
            var meta = document.createElement('div');
            meta.className = 'text-[10px] text-base-content/40 font-mono truncate';
            var firstPath = (s.paths && s.paths.length > 0) ? s.paths[0] : '';
            meta.textContent = ((s.requestCount || 0) + ' req · ' + firstPath);
            middle.appendChild(when);
            middle.appendChild(meta);

            var pct = document.createElement('div');
            pct.className = riskClass(s.riskBand) + ' font-semibold text-[11px]';
            pct.textContent = Math.round((s.avgBotProbability || 0) * 100) + '%';

            var band = document.createElement('div');
            band.className = riskClass(s.riskBand) + ' text-[10px]';
            band.textContent = s.riskBand || '--';

            row.appendChild(swatch);
            row.appendChild(middle);
            row.appendChild(pct);
            row.appendChild(band);

            row.addEventListener('click',      function () { focus(s.id); });
            row.addEventListener('mouseenter', function () { preview(s.id, true);  });
            row.addEventListener('mouseleave', function () { preview(s.id, false); });
            listEl.appendChild(row);
        });
    }

    function renderMetrics() {
        if (!metricsEl) return;
        var s = allSessions.find(function (x) { return x.id === focusedId; });
        function set(key, val) {
            var el = metricsEl.querySelector('[data-metric="' + key + '"]');
            if (el) el.textContent = (val === undefined || val === null) ? '--' : val;
        }
        if (!s) {
            ['duration','requests','dominant','bot-prob','maturity','entropy'].forEach(function (k) { set(k, '--'); });
            return;
        }
        set('duration', (s.durationMinutes || 0).toFixed(1) + 'm');
        set('requests', s.requestCount || 0);
        set('dominant', s.dominantState || '--');
        set('bot-prob', Math.round((s.avgBotProbability || 0) * 100) + '%');
        set('maturity', Math.round((s.maturity || 0) * 100) + '%');
        set('entropy', (s.timingEntropy || 0).toFixed(3));
    }

    function focus(id) {
        focusedId = id;
        renderRadar();
        renderList();
        renderMetrics();
        try {
            history.replaceState(null, '', '#session=' + encodeURIComponent(String(id)));
        } catch (e) { /* ignore */ }
    }

    var previewOriginal = null;
    function preview(id, on) {
        if (!chart) return;
        if (on) {
            previewOriginal = { fill: buildFillOpacity(), stroke: buildStrokeWidth() };
            var idx = visible.findIndex(function (s) { return s.id === id; });
            if (idx < 0) return;
            var fill = previewOriginal.fill.slice();
            var stroke = previewOriginal.stroke.slice();
            fill[idx]   = cfg.focusFillOpacity;
            stroke[idx] = cfg.currentStroke;
            chart.updateOptions({ fill: { opacity: fill }, stroke: { width: stroke, dashArray: buildDashArray() } });
        } else if (previewOriginal) {
            chart.updateOptions({ fill: { opacity: previewOriginal.fill }, stroke: { width: previewOriginal.stroke, dashArray: buildDashArray() } });
            previewOriginal = null;
        }
    }

    function stopPlay() {
        if (playTimer) { clearInterval(playTimer); playTimer = null; }
        var icon = playBtn && playBtn.querySelector('i');
        if (icon) icon.className = 'bx bx-play';
    }
    if (playBtn) {
        playBtn.addEventListener('click', function () {
            if (playTimer) { stopPlay(); return; }
            if (visible.length === 0) return;
            var icon = playBtn.querySelector('i');
            if (icon) icon.className = 'bx bx-pause';
            // Start at oldest, advance to newest
            var order = visible.slice().reverse();
            var i = 0;
            focus(order[0].id);
            playTimer = setInterval(function () {
                i++;
                if (i >= order.length) { stopPlay(); return; }
                focus(order[i].id);
            }, cfg.playIntervalMs);
        });
    }

    document.addEventListener('keydown', function (e) {
        if (!root.contains(document.activeElement) && document.activeElement !== document.body) return;
        if (visible.length === 0) return;
        var idx = visible.findIndex(function (s) { return s.id === focusedId; });
        if (e.key === 'ArrowLeft'  && idx < visible.length - 1) focus(visible[idx + 1].id);
        if (e.key === 'ArrowRight' && idx > 0)                  focus(visible[idx - 1].id);
    });

    fetch(bp + '/api/sessions/signature/' + encodeURIComponent(sig))
        .then(function (r) { return r.json(); })
        .then(function (data) {
            allSessions = Array.isArray(data) ? data : [];
            allSessions.forEach(function (s) { s.label = formatWhen(s.startedAt); });
            visible = allSessions.slice(0, cfg.maxOverlay);
            var hashMatch = (window.location.hash || '').match(/session=([^&]+)/);
            var hashId    = hashMatch ? decodeURIComponent(hashMatch[1]) : null;
            var preferred = hashId && allSessions.find(function (s) { return String(s.id) === hashId; });
            focusedId = preferred ? preferred.id : (visible[0] && visible[0].id) || null;

            if (countEl) countEl.textContent = visible.length + ' of ' + allSessions.length + ' sessions overlaid';
            renderRadar();
            renderList();
            renderMetrics();
        })
        .catch(function () {
            clearChildren(listEl);
            var fail = document.createElement('div');
            fail.className = 'text-base-content/40 text-center py-4 text-[11px]';
            fail.textContent = 'Failed to load sessions.';
            listEl.appendChild(fail);
        });
})();
</script>
```

- [ ] **Step 7.2: Build to confirm Razor compiles the partial with the script block**

```
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI -nologo
```
Expected: build succeeds.

- [ ] **Step 7.3: Commit**

```
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_BehavioralEvolution.cshtml
git commit -m "feat(ui): _BehavioralEvolution radar + session stack render"
```

---

## Phase 3: Swap-in

### Task 8: Replace the three panels on `_SignatureDetail.cshtml`

**File:** `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml`

Two regions are removed:
1. The three behavioral panels at lines **193–240** (the BEHAVIORAL HISTORY / Behavioral Sessions / Behavioral Shape blocks).
2. The inline radar script at lines **695–832** (the `// Behavioral Shape Radar Chart` IIFE).

Both regions are replaced with a single partial invocation that constructs and passes the model.

- [ ] **Step 8.1: Remove lines 193–240 (the three panel blocks)**

Open `_SignatureDetail.cshtml`. Delete the entire span starting with the comment `<!-- Behavioral Fingerprint History - lazy-loaded via HTMX -->` (line 193) through and including the closing `</div>` of the Behavioral Shape Radar block (line 240, the line `    </div>` immediately followed by the blank line before `<!-- Fingerprint Profile -->`).

Replace that span with:

```cshtml
    <!-- Behavioral Evolution: unified Behavioral History + Behavioral Sessions + Behavioral Shape -->
    @{
        var evoModel = new Mostlylucid.BotDetection.UI.Models.BehavioralEvolutionModel
        {
            SignatureId = Model.SignatureId,
            BasePath    = Model.BasePath,
            CspNonce    = Model.CspNonce,
            Options     = (Context.RequestServices.GetService(typeof(Microsoft.Extensions.Options.IOptions<Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions>))
                            as Microsoft.Extensions.Options.IOptions<Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions>)?.Value.BehavioralEvolution
                            ?? new Mostlylucid.BotDetection.UI.Configuration.BehavioralEvolutionOptions()
        };
    }
    @await Html.PartialAsync("_BehavioralEvolution", evoModel)
```

- [ ] **Step 8.2: Remove lines 695–832 (the inline radar script)**

In the same file, scroll to the comment `<!-- Behavioral Shape Radar Chart -->` (originally at line 695; after Step 8.1's ~48-line deletion it sits around line ~647 - search by the comment string to be safe) and delete the entire `<script nonce="@Model.CspNonce"> ... </script>` block that follows it, including the comment. That script's role moved into `_BehavioralEvolution.cshtml`.

- [ ] **Step 8.3: Build**

```
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI -nologo
```
Expected: build succeeds.

- [ ] **Step 8.4: Commit**

```
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml
git commit -m "feat(ui): swap three behavioral panels for _BehavioralEvolution partial"
```

---

## Phase 4: Verify (HARD GATE)

### Task 9: Interaction-verify against prod via chrome-devtools-mcp

Per repo memory: UI tasks are not complete until a real click / keypress has been driven in chrome-devtools (or playwright) and the resulting state observed. DOM-existence checks and API-direct-fetch checks DO NOT count. Verify against **prod (`stylo.bot`) only** - staging lacks the session history needed to read the panel.

First confirm the change reaches prod. The repo memory has the canonical deploy flow:
- Maxo (`.15`) builds via `C:\build\build-gateway.ps1` → registry `192.168.0.89:5000` → staging on `.15` (`stylobot-test` compose, `staging.stylobot.net`) → WAIT FOR APPROVAL → prod on `.89` (`stylobot` compose, `stylo.bot`).
- Never rsync. Never skip staging. `git branch --show-current` before every commit.

This task only fires *after* the build has been promoted to prod via that flow.

- [ ] **Step 9.1: Pick a real signature id from prod**

```
curl -s https://stylo.bot/_stylobot/api/top-bots\?count\=1 | jq -r '.[0].signatureId'
```
Note the value - call it `$SIG`.

- [ ] **Step 9.2: Confirm the API now returns `clockAxes`**

```
curl -s "https://stylo.bot/_stylobot/api/sessions/signature/$(printf %s "$SIG" | jq -sRr @uri)" \
  | jq '.[0] | {radarLen: (.radarAxes|length), clockLen: (.clockAxes|length)}'
```
Expected:
```
{ "radarLen": 8, "clockLen": 12 }
```
If `clockLen` is missing or ≠ 12, the deploy has not promoted - stop here.

- [ ] **Step 9.3: Open the signature page in chrome-devtools**

Use `mcp__plugin_chrome-devtools-mcp_chrome-devtools__new_page` with URL `https://stylo.bot/_stylobot/signature/<urlencode($SIG)>`. Wait for the page (no fixed sleep - use `wait_for` on a selector emitted by the new partial, e.g. text `Behavioral Evolution`).

- [ ] **Step 9.4: Wait for the radar to render**

`wait_for` selector `#behavioral-evolution [data-evolution-radar] svg` with a 10s timeout.

- [ ] **Step 9.5: Take a baseline screenshot**

`take_screenshot` and confirm visually:
- Single card titled "Behavioral Evolution".
- A radar polygon drawn in teal at full intensity (the focused/current session).
- One or more fainter ghost polygons.
- A vertical session card stack on the right, with the topmost row outlined in teal.
- Metrics strip at the bottom with non-`--` values for the focused session.

If any of these are absent, stop and reproduce locally - do not patch in prod.

- [ ] **Step 9.6: Click a non-focused session row and observe focus migrate**

Use `take_snapshot` to find the second session card's element id (the row whose `data-id` differs from the active one). Then `click` on that element id.

`wait_for` the previously inactive row to gain the `border-primary` class (or, equivalently, for the metrics strip's `data-metric="bot-prob"` text to change).

Take a second screenshot and confirm:
- The clicked session is now drawn solid teal.
- The previous focus is now a ghost.
- The metrics strip values match the newly-focused session (different request count / duration / bot prob from the baseline).

- [ ] **Step 9.7: Click Play and observe focus advance**

Click the element matching `[data-evolution-play]`. Use `wait_for` (selector on a different `data-id` becoming active) rather than a fixed sleep.

After the timer fires once, take a third screenshot. Confirm the active row has advanced.

Click Play again to stop. Confirm the play icon reverts (`bx-play` not `bx-pause`).

- [ ] **Step 9.8: If anything looks wrong**

Per repo memory ("repro first then fix"): reproduce against prod, do not patch from `curl` output or "should work" guesses. If a visual bug is real, file the repro screenshot in the task and fix the root cause before re-running this gate.

- [ ] **Step 9.9: Mark complete only after Steps 9.5–9.7 all pass**

No commit on this task - verification only.

---

## Self-review

After every task above has its checkboxes ticked, sweep the spec one more time:

- [ ] **Spec coverage** - every section of `docs/superpowers/specs/2026-05-24-behavioral-evolution-design.md` is implemented:
  - 12-axis clock layout ✔ Tasks 2-3, 6
  - Axis legend ✔ Task 6
  - Quadrant labels - rendered as text-only quadrant headings inside the axis legend (Task 6). The spec's "subtle background washes" aren't supported natively by ApexCharts radar; they are not implemented in this plan. `ShowQuadrantBackgrounds` remains on the options record so a follow-up can wire an SVG overlay if the user wants the washes back.
  - Component layout ✔ Task 6
  - Data flow ✔ Tasks 2-4
  - Interaction model ✔ Task 7
  - Opacity & stroke curves ✔ Task 7 (`ghostOpacity`, `colorFor`, dash array on oldest)
  - Files (new + modified + kept) ✔ Tasks 1, 2, 5, 6, 8
  - Configurable settings ✔ Task 1
- [ ] **Placeholder scan** - no TBD/TODO strings appear in the produced code.
- [ ] **Type consistency** - `BehavioralEvolutionOptions` properties referenced from the partial (`MaxOverlaySessions`, `HalfLifeMinutes`, `MinGhostOpacity`, `MaxGhostOpacity`, `MinStrokeOpacity`, `FocusFillOpacity`, `FocusStrokeOpacity`, `CurrentStrokeWidth`, `GhostStrokeWidth`, `PlayIntervalMs`, `RingCount`, `BlueShiftAfterMinutes`, `ShowQuadrantBackgrounds`, `ShowAxisLegend`, `ShowMetricsStrip`) match the names in Task 1.

### Test-coverage decisions

The spec's **Testing** section lists four categories. Coverage and decisions:

| Spec test | Plan coverage |
|-----------|---------------|
| `ClockProjectionTests` (unit) | Tasks 2 + 3 - 12 tests on the projection helpers |
| `SignatureSessionsApiTests` (API contract) | Step 4.6 (curl + jq) asserts `clockAxes` length 12 alongside `radarAxes` against a local dashboard or prod. Step 9.2 re-asserts after deploy. The middleware composition glue is three calls to `Compose12Axes(semantic, ProjectMarkov(slice))` per branch - each is runtime-verified by Task 9's chrome-devtools gate. A `WebApplicationFactory<Program>` integration test is **not added**: the UI test directory does not host one, adding it is separate infra work, and the live verification is already automated and required by the gate. |
| `BehavioralEvolutionPartialTests` (partial render) | **Not added.** The chrome-devtools gate (Steps 9.4–9.7) renders the partial against real prod data and drives real clicks/keypresses - that's the meaningful behavior, not the static HTML structure that a server-side render test could check. |
| Browser interaction (chrome-devtools-mcp) | Task 9 - the HARD GATE |

If a future change ever splits the middleware composition into multiple deployables or branches behavior between the three session paths in ways the gate can't cover, add the WebApplicationFactory fixture then.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-05-24-behavioral-evolution-plan.md`. Two execution options:**

**1. Subagent-Driven (recommended)** - A fresh subagent per task, review between tasks, fast iteration. Best for this plan because each task has narrow file scope.

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**