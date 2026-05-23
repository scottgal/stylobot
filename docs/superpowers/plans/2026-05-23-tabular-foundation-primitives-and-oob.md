# Tabular Foundation — Primitives + InnerHTML OOB Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the eight shared primitive partials (icons, bars, sparkline shell, country flag, time-ago, toolbar, pagination) AND switch the SignalR OOB swap from outerHTML-on-widget-root to innerHTML-on-data-region. No widget is migrated to use them in this plan — that's the next plans, one per widget.

**Architecture:** Pure additive Razor partials under `Views/StyloBot/Dashboard/_Primitives/` plus a one-method change to `WidgetRenderHelpers.InjectOobAttribute`. After this lands, no visible behaviour changes (no partial yet emits `data-sb-data-region`, so the InjectOobAttribute fallback path keeps the old behaviour). The next plan migrates `SbTopBots` and the user sees flicker-free updates.

**Tech Stack:** ASP.NET Core MVC + Razor + HTMX 2.0 + Boxicons + xUnit. No new dependencies. No client-side JS frameworks (the user has explicitly forbidden React/Vue/Alpine state stores for this work — partials emit final HTML).

**Spec:** `docs/superpowers/specs/2026-05-23-tabular-data-foundation-design.md`

---

### Task 1: Primitive model records (one file, eight records)

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Models/Primitives/PrimitiveModels.cs`
- Test: `src/Mostlylucid.BotDetection.Test/UI/Primitives/PrimitiveModelsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/UI/Primitives/PrimitiveModelsTests.cs`:

```csharp
using Mostlylucid.BotDetection.UI.Models.Primitives;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class PrimitiveModelsTests
{
    [Fact]
    public void ThreatIconModel_defaults_are_safe()
    {
        var m = new ThreatIconModel(Band: null, BotProbability: 0.0);
        Assert.Null(m.Band);
        Assert.Equal(0.0, m.BotProbability);
    }

    [Fact]
    public void IntentIconModel_preserves_intent_string()
    {
        var m = new IntentIconModel(Intent: "Scraper");
        Assert.Equal("Scraper", m.Intent);
    }

    [Fact]
    public void SparklineModel_empty_arrays_have_correct_shape()
    {
        var m = new SparklineModel(BotTrend: System.Array.Empty<int>(),
                                   HumanTrend: System.Array.Empty<int>(),
                                   WindowMinutes: 60);
        Assert.Empty(m.BotTrend);
        Assert.Empty(m.HumanTrend);
        Assert.Equal(60, m.WindowMinutes);
    }

    [Fact]
    public void TableToolbarModel_accepts_chip_list()
    {
        var chips = new[]
        {
            new FilterChip(Key: "all", Label: "All", Count: 12, Url: "/?filter=all"),
            new FilterChip(Key: "bots", Label: "Bots", Count: 4, Url: "/?filter=bots"),
        };
        var windows = new[]
        {
            new TimeWindowOption(Key: "1h", Label: "1h", Url: "/?window=1h"),
            new TimeWindowOption(Key: "6h", Label: "6h", Url: "/?window=6h"),
        };
        var m = new TableToolbarModel(
            TargetId: "live-activity-list",
            Chips: chips,
            ActiveFilter: "all",
            ShowSearch: true,
            SearchUrl: "/?",
            TimeWindowOptions: windows,
            ActiveTimeWindow: "1h");
        Assert.Equal(2, m.Chips.Count);
        Assert.Equal("all", m.ActiveFilter);
        Assert.Equal(2, m.TimeWindowOptions!.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~PrimitiveModelsTests" --no-restore 2>&1 | tail -20
```
Expected: compile failure — `Mostlylucid.BotDetection.UI.Models.Primitives` namespace does not exist.

- [ ] **Step 3: Create the model records**

Create `src/Mostlylucid.BotDetection.UI/Models/Primitives/PrimitiveModels.cs`:

```csharp
namespace Mostlylucid.BotDetection.UI.Models.Primitives;

/// <summary>Icon + colour + tooltip for a threat band cell. Replaces text columns.</summary>
public sealed record ThreatIconModel(string? Band, double BotProbability);

/// <summary>Icon + tooltip for an intent/bot-type cell. Replaces text columns.</summary>
public sealed record IntentIconModel(string? Intent);

/// <summary>5-segment severity bar for bot-probability cells. Replaces "78%" text.</summary>
public sealed record RiskBarModel(double Probability, string? Band);

/// <summary>
///     SSR'd inline SVG sparkline. Server emits the path string -- zero client JS,
///     zero extra fetches. Bot and human trend arrays must be the same length;
///     y-axis auto-scales to max(bot, human).
/// </summary>
public sealed record SparklineModel(int[] BotTrend, int[] HumanTrend, int WindowMinutes);

/// <summary>Flag SVG + country-name tooltip.</summary>
public sealed record CountryFlagModel(string? Code, string? Name);

/// <summary>Relative time string with absolute timestamp in title attr.</summary>
public sealed record TimeAgoModel(System.DateTime Utc, string? RelativeText);

/// <summary>Filter chip in the table toolbar.</summary>
public sealed record FilterChip(string Key, string Label, int Count, string Url);

/// <summary>Time-window pill option in the table toolbar.</summary>
public sealed record TimeWindowOption(string Key, string Label, string Url);

/// <summary>
///     Table toolbar: filter chips, optional search, optional time-window pills.
///     The caller pre-builds every URL — the partial does no URL construction so the
///     route/widget naming convention stays the caller's concern.
/// </summary>
public sealed record TableToolbarModel(
    string TargetId,
    System.Collections.Generic.IReadOnlyList<FilterChip> Chips,
    string? ActiveFilter,
    bool ShowSearch,
    string? SearchUrl,
    System.Collections.Generic.IReadOnlyList<TimeWindowOption>? TimeWindowOptions,
    string? ActiveTimeWindow);

/// <summary>Table pagination footer: page-size, numbered pages, total.</summary>
public sealed record TablePaginationModel(
    int Page,
    int PageSize,
    int TotalCount,
    System.Func<int, string> PageUrl,
    System.Func<int, string> PageSizeUrl,
    string TargetId)
{
    public int TotalPages => PageSize > 0 ? (TotalCount + PageSize - 1) / PageSize : 1;
    public int FirstItem => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastItem => System.Math.Min(Page * PageSize, TotalCount);
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~PrimitiveModelsTests" --no-restore 2>&1 | tail -10
```
Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Models/Primitives/PrimitiveModels.cs \
  src/Mostlylucid.BotDetection.Test/UI/Primitives/PrimitiveModelsTests.cs
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): primitive models for shared table chrome"
```

---

### Task 2: `_ThreatIcon` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_ThreatIcon.cshtml`

This partial has no logic worth unit-testing (pure markup mapping); behaviour is verified later via the per-widget integration tests.

- [ ] **Step 1: Create the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@model ThreatIconModel
@{
    var band = Model.Band ?? "None";
    var probPct = (Model.BotProbability * 100).ToString("F0");
    var (icon, colorClass, label) = band switch
    {
        "Critical" => ("bx-shield-x", "text-error", "Critical"),
        "VeryHigh" => ("bx-shield-x", "text-error", "Very High"),
        "High"     => ("bx-shield-quarter", "text-error", "High"),
        "Elevated" => ("bx-shield-quarter", "text-warning", "Elevated"),
        "Medium"   => ("bx-shield-quarter", "text-warning", "Medium"),
        "Low"      => ("bx-shield", "text-success", "Low"),
        _          => ("bx-shield", "text-base-content/30", "None")
    };
    var title = $"{label} -- {probPct}% bot probability";
}
<i class="bx @icon @colorClass text-base" title="@title" aria-label="@title"></i>
```

- [ ] **Step 2: Smoke-build the project**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_ThreatIcon.cshtml
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _ThreatIcon primitive partial"
```

---

### Task 3: `_IntentIcon` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_IntentIcon.cshtml`

- [ ] **Step 1: Create the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@model IntentIconModel
@{
    var intent = Model.Intent ?? "Unknown";
    var (icon, label) = intent switch
    {
        "Scraper" or "Scanner"  => ("bx-spider", "Scraper"),
        "Probe"                 => ("bx-search-alt", "Probe"),
        "AI" or "AiBot"         => ("bx-brain", "AI"),
        "Tool"                  => ("bx-wrench", "Tool"),
        "Browser"               => ("bx-globe", "Browser"),
        "Crawler"               => ("bx-network-chart", "Crawler"),
        "Automated"             => ("bx-bot", "Automated"),
        "MonitoringBot"         => ("bx-pulse", "Monitor"),
        _                       => ("bx-help-circle", intent)
    };
}
<i class="bx @icon text-base-content/60 text-base" title="@label" aria-label="@label"></i>
```

- [ ] **Step 2: Smoke-build the project**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_IntentIcon.cshtml
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _IntentIcon primitive partial"
```

---

### Task 4: `_RiskBar` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_RiskBar.cshtml`

- [ ] **Step 1: Create the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@model RiskBarModel
@{
    // 5-segment bar; each segment is "filled" if probability >= its threshold (0.2, 0.4, 0.6, 0.8, 1.0).
    var p = System.Math.Clamp(Model.Probability, 0, 1);
    var filled = (int)System.Math.Ceiling(p * 5);
    var band = Model.Band ?? "None";
    var segColor = band switch
    {
        "Critical" or "VeryHigh" or "High" => "bg-error",
        "Elevated" or "Medium"             => "bg-warning",
        "Low"                              => "bg-success",
        _                                  => "bg-base-content/30"
    };
    var pctText = (p * 100).ToString("F0") + "%";
    var title = $"{band} -- {pctText} bot probability";
}
<div class="inline-flex items-center gap-0.5" title="@title" aria-label="@title">
@for (int i = 0; i < 5; i++)
{
    var cls = i < filled ? segColor : "bg-base-content/15";
    <span class="@cls" style="width:8px; height:8px; border-radius:1px; display:inline-block;"></span>
}
</div>
```

- [ ] **Step 2: Smoke-build**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_RiskBar.cshtml
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _RiskBar primitive partial"
```

---

### Task 5: Sparkline path helper (extracted for unit testing)

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Services/SparklinePathBuilder.cs`
- Test: `src/Mostlylucid.BotDetection.Test/UI/Primitives/SparklinePathBuilderTests.cs`

The sparkline partial is mostly markup, but the SVG-path math is testable logic. Extract it into a helper class first.

- [ ] **Step 1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/UI/Primitives/SparklinePathBuilderTests.cs`:

```csharp
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class SparklinePathBuilderTests
{
    [Fact]
    public void Build_returns_empty_path_for_empty_data()
    {
        var path = SparklinePathBuilder.Build(System.Array.Empty<int>(), width: 60, height: 18);
        Assert.Equal("", path);
    }

    [Fact]
    public void Build_flat_zero_data_renders_baseline()
    {
        var path = SparklinePathBuilder.Build(new[] { 0, 0, 0, 0 }, width: 60, height: 18);
        // 4 points spread across width 60, all y=18 (bottom). Spacing 60/(4-1)=20.
        Assert.Equal("M0,18 L20,18 L40,18 L60,18", path);
    }

    [Fact]
    public void Build_scales_to_max_value()
    {
        var path = SparklinePathBuilder.Build(new[] { 0, 10, 5, 0 }, width: 60, height: 18);
        // max=10 -> y maps 10->0, 5->9, 0->18. Spacing 20.
        Assert.Equal("M0,18 L20,0 L40,9 L60,18", path);
    }

    [Fact]
    public void Build_with_explicit_max_uses_supplied_value()
    {
        // explicit max=20 so all values are 0-50% of the height range
        var path = SparklinePathBuilder.Build(new[] { 0, 20, 10, 0 }, width: 60, height: 18, max: 20);
        Assert.Equal("M0,18 L20,0 L40,9 L60,18", path);
    }

    [Fact]
    public void Build_single_point_renders_baseline_point()
    {
        // One point has no horizontal extent; emit a tiny line at the baseline.
        var path = SparklinePathBuilder.Build(new[] { 5 }, width: 60, height: 18);
        Assert.Equal("M0,0 L0,0", path);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~SparklinePathBuilderTests" --no-restore 2>&1 | tail -10
```
Expected: compile error — `SparklinePathBuilder` does not exist.

- [ ] **Step 3: Create the helper**

Create `src/Mostlylucid.BotDetection.UI/Services/SparklinePathBuilder.cs`:

```csharp
using System;
using System.Globalization;
using System.Text;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds an SVG path string for a sparkline. Server-side -- the rendered partial
///     emits the path directly so the browser does no work to draw the trend.
/// </summary>
public static class SparklinePathBuilder
{
    /// <summary>
    ///     Build an SVG <c>d</c> attribute for an int[] series.
    ///     Returns "" for an empty array.
    ///     Y-axis is auto-scaled to the max value unless <paramref name="max"/> is supplied.
    /// </summary>
    public static string Build(int[] values, int width, int height, int? max = null)
    {
        if (values.Length == 0) return "";
        if (values.Length == 1) return "M0,0 L0,0";

        int peak = max ?? MaxOrOne(values);
        if (peak <= 0) peak = 1;

        double xStep = (double)width / (values.Length - 1);
        var sb = new StringBuilder(values.Length * 12);

        for (int i = 0; i < values.Length; i++)
        {
            double x = i * xStep;
            double y = height - (double)values[i] / peak * height;
            sb.Append(i == 0 ? "M" : " L");
            sb.Append(((int)Math.Round(x)).ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(((int)Math.Round(y)).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static int MaxOrOne(int[] values)
    {
        int m = 0;
        for (int i = 0; i < values.Length; i++) if (values[i] > m) m = values[i];
        return m == 0 ? 1 : m;
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~SparklinePathBuilderTests" --no-restore 2>&1 | tail -10
```
Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Services/SparklinePathBuilder.cs \
  src/Mostlylucid.BotDetection.Test/UI/Primitives/SparklinePathBuilderTests.cs
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): SparklinePathBuilder helper for SSR svg paths"
```

---

### Task 6: `_Sparkline` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_Sparkline.cshtml`

- [ ] **Step 1: Create the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@using Mostlylucid.BotDetection.UI.Services
@model SparklineModel
@{
    const int W = 60;
    const int H = 18;
    // Use a shared max across both series so the human trend doesn't get squashed
    // out of view when bot traffic dwarfs it.
    int sharedMax = 1;
    foreach (var v in Model.BotTrend)   if (v > sharedMax) sharedMax = v;
    foreach (var v in Model.HumanTrend) if (v > sharedMax) sharedMax = v;

    var botPath   = SparklinePathBuilder.Build(Model.BotTrend,   W, H, sharedMax);
    var humanPath = SparklinePathBuilder.Build(Model.HumanTrend, W, H, sharedMax);

    int botSum = 0, humanSum = 0;
    foreach (var v in Model.BotTrend)   botSum   += v;
    foreach (var v in Model.HumanTrend) humanSum += v;
    var title = $"{Model.WindowMinutes}m: {botSum:N0} bot · {humanSum:N0} human";
    var hasData = botPath.Length > 0 || humanPath.Length > 0;
}
<svg width="@W" height="@H" viewBox="0 0 @W @H" style="overflow:visible;" role="img" aria-label="@title">
    <title>@title</title>
    @if (hasData)
    {
        @if (humanPath.Length > 0)
        {
            <path d="@humanPath" stroke="var(--color-success, #22c55e)" fill="none" stroke-width="1" opacity="0.55" />
        }
        @if (botPath.Length > 0)
        {
            <path d="@botPath" stroke="var(--color-error, #ef4444)" fill="none" stroke-width="1.2" />
        }
    }
    else
    {
        <line x1="0" y1="@(H-1)" x2="@W" y2="@(H-1)" stroke="var(--color-base-content, #888)" stroke-opacity="0.15" stroke-width="1" />
    }
</svg>
```

- [ ] **Step 2: Smoke-build**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_Sparkline.cshtml
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _Sparkline primitive partial (SSR svg)"
```

---

### Task 7: `_CountryFlag` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_CountryFlag.cshtml`

- [ ] **Step 1: Create the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@model CountryFlagModel
@{
    var hasFlag = !string.IsNullOrEmpty(Model.Code)
                  && Model.Code!.Length == 2
                  && !Model.Code.Equals("XX", System.StringComparison.OrdinalIgnoreCase);
    var name = Model.Name ?? Model.Code ?? "Unknown";
}
@if (hasFlag)
{
    var url = $"/_content/Mostlylucid.BotDetection.UI/flags/{Model.Code!.ToLowerInvariant()}.svg";
    <img src="@url" alt="@Model.Code" title="@name" class="w-6 h-4 object-cover rounded-sm" loading="lazy" />
}
else
{
    <span class="text-base-content/30 text-[10px]" title="@name">--</span>
}
```

- [ ] **Step 2: Smoke-build**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_CountryFlag.cshtml
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _CountryFlag primitive partial"
```

---

### Task 8: `_TimeAgo` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_TimeAgo.cshtml`

- [ ] **Step 1: Create the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@model TimeAgoModel
@{
    var iso = Model.Utc == default
        ? ""
        : Model.Utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
    var local = Model.Utc == default ? "" : Model.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    var rel = Model.RelativeText ?? Format(Model.Utc);
}
<span class="text-[10px] text-base-content/40" title="@local UTC: @iso">@rel</span>
@functions {
    private static string Format(System.DateTime utc)
    {
        if (utc == default) return "";
        var span = System.DateTime.UtcNow - utc;
        if (span.TotalSeconds < 5) return "now";
        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d";
    }
}
```

- [ ] **Step 2: Smoke-build**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_TimeAgo.cshtml
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _TimeAgo primitive partial"
```

---

### Task 9: `_TableToolbar` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_TableToolbar.cshtml`

- [ ] **Step 1: Create the partial**

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@model TableToolbarModel
@{
    string ChipClass(string key) =>
        string.Equals(Model.ActiveFilter, key, System.StringComparison.OrdinalIgnoreCase)
            ? "bg-base-100 shadow-sm text-base-content font-semibold"
            : "text-base-content/40 hover:text-base-content/60";

    string WindowClass(string key) =>
        string.Equals(Model.ActiveTimeWindow, key, System.StringComparison.OrdinalIgnoreCase)
            ? "bg-base-100 shadow-sm text-base-content font-semibold"
            : "text-base-content/40 hover:text-base-content/60";
}
<div class="flex items-center justify-between gap-2 mb-2 flex-wrap">
    <div class="flex items-center gap-1 bg-base-300/50 rounded-lg p-0.5">
        @foreach (var chip in Model.Chips)
        {
            <button type="button"
                    hx-get="@chip.Url"
                    hx-target="#@Model.TargetId"
                    hx-swap="outerHTML transition:true"
                    class="px-2.5 py-1 text-xs font-medium rounded-md transition-all @ChipClass(chip.Key)">
                @chip.Label <span class="text-[10px] opacity-60">@chip.Count</span>
            </button>
        }
    </div>

    <div class="flex items-center gap-2">
        @if (Model.ShowSearch && !string.IsNullOrEmpty(Model.SearchUrl))
        {
            <input type="search"
                   name="q"
                   placeholder="Search..."
                   hx-get="@Model.SearchUrl"
                   hx-trigger="keyup changed delay:300ms"
                   hx-target="#@Model.TargetId"
                   hx-swap="outerHTML transition:true"
                   hx-include="this"
                   class="input input-xs input-bordered w-40 text-xs" />
        }
        @if (Model.TimeWindowOptions is { Count: > 0 })
        {
            <div class="flex items-center gap-0.5 bg-base-300/50 rounded-lg p-0.5">
                @foreach (var opt in Model.TimeWindowOptions)
                {
                    <button type="button"
                            hx-get="@opt.Url"
                            hx-target="#@Model.TargetId"
                            hx-swap="outerHTML transition:true"
                            class="px-1.5 py-0.5 text-[10px] font-medium rounded-md transition-all @WindowClass(opt.Key)">
                        @opt.Label
                    </button>
                }
            </div>
        }
    </div>
</div>
```

- [ ] **Step 2: Smoke-build**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_TableToolbar.cshtml
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _TableToolbar primitive partial"
```

---

### Task 10: `_TablePagination` partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_TablePagination.cshtml`
- Create: `src/Mostlylucid.BotDetection.UI/Services/PaginationNumbering.cs`
- Test: `src/Mostlylucid.BotDetection.Test/UI/Primitives/PaginationNumberingTests.cs`

The page-number-with-ellipsis logic is testable; extract to a helper.

- [ ] **Step 1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/UI/Primitives/PaginationNumberingTests.cs`:

```csharp
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class PaginationNumberingTests
{
    [Fact]
    public void Compact_returns_all_pages_when_few()
    {
        var seq = PaginationNumbering.Compact(currentPage: 2, totalPages: 5, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, 2, 3, 4, 5 }, seq);
    }

    [Fact]
    public void Compact_inserts_left_ellipsis_when_far_from_start()
    {
        // Current=10, total=14, slots=7 -> 1 ... 8 9 [10] 11 12 ... 14 is 9 slots, doesn't fit.
        // 7 slots = first + left-ellipsis + window-of-3-around-current + right-ellipsis + last.
        var seq = PaginationNumbering.Compact(currentPage: 10, totalPages: 14, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, null, 9, 10, 11, null, 14 }, seq);
    }

    [Fact]
    public void Compact_inserts_right_ellipsis_only_when_near_start()
    {
        var seq = PaginationNumbering.Compact(currentPage: 2, totalPages: 14, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, 2, 3, 4, 5, null, 14 }, seq);
    }

    [Fact]
    public void Compact_inserts_left_ellipsis_only_when_near_end()
    {
        var seq = PaginationNumbering.Compact(currentPage: 13, totalPages: 14, maxSlots: 7);
        Assert.Equal(new[] { (int?)1, null, 10, 11, 12, 13, 14 }, seq);
    }

    [Fact]
    public void Compact_handles_single_page()
    {
        var seq = PaginationNumbering.Compact(currentPage: 1, totalPages: 1, maxSlots: 7);
        Assert.Equal(new[] { (int?)1 }, seq);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~PaginationNumberingTests" --no-restore 2>&1 | tail -10
```
Expected: compile error.

- [ ] **Step 3: Create the helper**

Create `src/Mostlylucid.BotDetection.UI/Services/PaginationNumbering.cs`:

```csharp
using System.Collections.Generic;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Compact pagination numbering. Produces a sequence of page numbers (nullable int)
///     where <c>null</c> means "ellipsis". The total number of slots is clamped to
///     <paramref name="maxSlots"/> (default 7) so the rendered footer never wraps.
/// </summary>
public static class PaginationNumbering
{
    public static IReadOnlyList<int?> Compact(int currentPage, int totalPages, int maxSlots = 7)
    {
        if (totalPages <= 1) return new int?[] { 1 };
        if (totalPages <= maxSlots)
        {
            var all = new int?[totalPages];
            for (int i = 0; i < totalPages; i++) all[i] = i + 1;
            return all;
        }

        // Reserve slot 0 = first, slot maxSlots-1 = last. Middle slots show a window
        // around current. Use a single ellipsis on each side when truncation is needed.
        var result = new List<int?>(maxSlots);
        // Window size = maxSlots - 4 (first, last, two possible ellipses)
        int window = maxSlots - 4;
        if (window < 1) window = 1;

        // Determine window bounds around current.
        int windowStart = currentPage - window / 2;
        int windowEnd = currentPage + window / 2;
        if (windowStart < 3) { windowStart = 2; windowEnd = windowStart + window - 1; }
        if (windowEnd > totalPages - 2) { windowEnd = totalPages - 1; windowStart = windowEnd - window + 1; }
        if (windowStart < 2) windowStart = 2;

        result.Add(1);
        bool leftEllipsis = windowStart > 2;
        bool rightEllipsis = windowEnd < totalPages - 1;

        if (leftEllipsis) result.Add(null);
        else
        {
            for (int p = 2; p < windowStart; p++) result.Add(p);
        }

        for (int p = windowStart; p <= windowEnd; p++) result.Add(p);

        if (rightEllipsis) result.Add(null);
        else
        {
            for (int p = windowEnd + 1; p < totalPages; p++) result.Add(p);
        }

        result.Add(totalPages);

        // Trim to maxSlots if rounding put us slightly over.
        while (result.Count > maxSlots)
        {
            // Prefer to collapse the side furthest from current.
            int leftDist = currentPage - 1;
            int rightDist = totalPages - currentPage;
            if (leftDist >= rightDist && result.Count > 1) result.RemoveAt(1);
            else if (result.Count > 1) result.RemoveAt(result.Count - 2);
            else break;
        }

        return result;
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~PaginationNumberingTests" --no-restore 2>&1 | tail -10
```
Expected: 5 tests pass.

- [ ] **Step 5: Create the partial**

Create `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_TablePagination.cshtml`:

```cshtml
@using Mostlylucid.BotDetection.UI.Models.Primitives
@using Mostlylucid.BotDetection.UI.Services
@model TablePaginationModel
@{
    if (Model.TotalCount == 0) { return; }
    var slots = PaginationNumbering.Compact(Model.Page, Model.TotalPages);
}
<div class="flex items-center justify-between gap-2 mt-2 pt-2 border-t border-base-300/60 flex-wrap">
    <div class="flex items-center gap-2 text-[10px] text-base-content/50">
        <span>Showing @Model.FirstItem–@Model.LastItem of @Model.TotalCount.ToString("N0")</span>
        <div class="flex items-center gap-1">
            <span>per page</span>
            <select hx-get="@Model.PageSizeUrl(10)"
                    hx-target="#@Model.TargetId"
                    hx-swap="outerHTML transition:true"
                    onchange="this.value && htmx.ajax('GET', this.options[this.selectedIndex].dataset.url, { target: '#@Model.TargetId', swap: 'outerHTML transition:true' })"
                    class="select select-xs select-bordered text-[10px]">
                @foreach (var size in new[] { 10, 25, 50, 100 })
                {
                    var selected = size == Model.PageSize ? "selected" : null;
                    <option value="@size" data-url="@Model.PageSizeUrl(size)" selected="@selected">@size</option>
                }
            </select>
        </div>
    </div>

    <div class="flex items-center gap-0.5">
        @if (Model.Page > 1)
        {
            <button type="button"
                    hx-get="@Model.PageUrl(Model.Page - 1)"
                    hx-target="#@Model.TargetId"
                    hx-swap="outerHTML transition:true"
                    class="btn btn-xs btn-ghost px-2">‹</button>
        }
        @foreach (var slot in slots)
        {
            if (slot is null)
            {
                <span class="px-2 text-[10px] text-base-content/30">…</span>
            }
            else if (slot == Model.Page)
            {
                <span class="btn btn-xs btn-active px-2 pointer-events-none">@slot</span>
            }
            else
            {
                <button type="button"
                        hx-get="@Model.PageUrl(slot.Value)"
                        hx-target="#@Model.TargetId"
                        hx-swap="outerHTML transition:true"
                        class="btn btn-xs btn-ghost px-2">@slot</button>
            }
        }
        @if (Model.Page < Model.TotalPages)
        {
            <button type="button"
                    hx-get="@Model.PageUrl(Model.Page + 1)"
                    hx-target="#@Model.TargetId"
                    hx-swap="outerHTML transition:true"
                    class="btn btn-xs btn-ghost px-2">›</button>
        }
    </div>
</div>
```

- [ ] **Step 6: Smoke-build**

```bash
dotnet build /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Services/PaginationNumbering.cs \
  src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/_TablePagination.cshtml \
  src/Mostlylucid.BotDetection.Test/UI/Primitives/PaginationNumberingTests.cs
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "feat(ui): _TablePagination primitive partial + numbering helper"
```

---

### Task 11: Switch `InjectOobAttribute` to innerHTML on `data-sb-data-region`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/WidgetRenderHelpers.cs:44-60`
- Test: `src/Mostlylucid.BotDetection.Test/UI/WidgetRenderHelpersInjectOobTests.cs`

This is the structural change. After this lands, any partial that emits `data-sb-data-region` gets innerHTML OOB; any that does not falls back to the current outerHTML-on-root behaviour.

- [ ] **Step 1: Write the failing test**

Create `src/Mostlylucid.BotDetection.Test/UI/WidgetRenderHelpersInjectOobTests.cs`:

```csharp
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public class WidgetRenderHelpersInjectOobTests
{
    [Fact]
    public void Legacy_html_without_data_region_gets_outerHTML_oob_on_root()
    {
        const string html = "<div id=\"my-widget\" data-sb-widget=\"my-widget\">stuff</div>";
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        Assert.Contains("hx-swap-oob=\"true\"", result);
        // Attribute lands on the root tag (before the closing >).
        var firstTagEnd = result.IndexOf('>');
        Assert.Contains("hx-swap-oob", result[..firstTagEnd]);
    }

    [Fact]
    public void Html_with_data_region_gets_innerHTML_oob_on_region_not_root()
    {
        const string html = """
            <div id="my-widget" data-sb-widget="my-widget">
              <div class="toolbar">chrome</div>
              <div id="my-widget-data" data-sb-data-region>
                <table><tr><td>row</td></tr></table>
              </div>
            </div>
            """;
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        Assert.Contains("hx-swap-oob=\"innerHTML\"", result);
        // The root <div id="my-widget"> must NOT have hx-swap-oob.
        var firstTagEnd = result.IndexOf('>');
        Assert.DoesNotContain("hx-swap-oob", result[..firstTagEnd]);
        // The data region <div id="my-widget-data"> MUST have it.
        var dataRegionStart = result.IndexOf("id=\"my-widget-data\"");
        var dataRegionTagEnd = result.IndexOf('>', dataRegionStart);
        Assert.Contains("hx-swap-oob=\"innerHTML\"", result[dataRegionStart..dataRegionTagEnd]);
    }

    [Fact]
    public void Already_oob_html_is_left_alone()
    {
        const string html = "<div id=\"my-widget\" hx-swap-oob=\"true\">stuff</div>";
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        // Should not double-inject.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result, "hx-swap-oob").Count;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Data_region_with_existing_oob_is_left_alone()
    {
        const string html = """
            <div id="my-widget">
              <div id="my-widget-data" data-sb-data-region hx-swap-oob="innerHTML">rows</div>
            </div>
            """;
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result, "hx-swap-oob").Count;
        Assert.Equal(1, occurrences);
    }
}
```

- [ ] **Step 2: Run to verify two of the four tests fail**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~WidgetRenderHelpersInjectOobTests" --no-restore 2>&1 | tail -20
```
Expected: `Legacy_html_without_data_region_gets_outerHTML_oob_on_root` passes (current behaviour); `Already_oob_html_is_left_alone` passes; `Html_with_data_region_gets_innerHTML_oob_on_region_not_root` and `Data_region_with_existing_oob_is_left_alone` fail (innerHTML path not implemented yet).

- [ ] **Step 3: Update `InjectOobAttribute`**

Read the existing helper at `src/Mostlylucid.BotDetection.UI/Middleware/WidgetRenderHelpers.cs:1-60` to confirm the existing regex and replace the body of `InjectOobAttribute` with the two-path logic. Replace lines 44–60 with:

```csharp
    // Matches the first opening tag that carries the data-sb-data-region attribute.
    // Group 1 = "<tag ... " (everything before the trailing >). Group 2 = "/>" or ">".
    private static readonly Regex DataRegionTagRegex = new(
        @"(<[a-zA-Z][^>]*?\sdata-sb-data-region(?:=""[^""]*"")?[^>]*?)(/?>)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    internal static string InjectOobAttribute(string html)
    {
        // Preferred path: a [data-sb-data-region] element exists in the chunk.
        // Inject hx-swap-oob="innerHTML" on it so HTMX replaces ONLY the contents
        // of the data region, leaving the widget chrome untouched. This is the
        // structural fix for the "flickery resetting" SignalR refresh.
        var regionMatch = DataRegionTagRegex.Match(html);
        if (regionMatch.Success)
        {
            if (regionMatch.Value.Contains("hx-swap-oob", System.StringComparison.Ordinal))
                return html;

            return html[..(regionMatch.Index + regionMatch.Groups[1].Length)]
                   + " hx-swap-oob=\"innerHTML\""
                   + html[(regionMatch.Index + regionMatch.Groups[1].Length)..];
        }

        // Legacy fallback: no data region marked. Inject the old outerHTML OOB on the
        // root. Kept so partials not yet migrated to the two-region contract keep
        // working. The widget will continue to flicker on update -- a deliberate
        // signal that the partial needs migration.
        var rootMatch = FirstTagRegex.Match(html);
        if (!rootMatch.Success) return html;
        if (rootMatch.Value.Contains("hx-swap-oob", System.StringComparison.Ordinal)) return html;

        return html[..rootMatch.Groups[1].Index]
               + rootMatch.Groups[1].Value
               + " hx-swap-oob=\"true\""
               + rootMatch.Groups[2].Value
               + html[(rootMatch.Index + rootMatch.Length)..];
    }
```

Make sure `using System.Text.RegularExpressions;` is at the top of the file (it already is).

- [ ] **Step 4: Run to verify all four tests pass**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~WidgetRenderHelpersInjectOobTests" --no-restore 2>&1 | tail -10
```
Expected: 4 tests pass.

- [ ] **Step 5: Run the broader UI test suite to confirm no regression**

```bash
dotnet test /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.Test --filter "FullyQualifiedName~UI" --no-restore 2>&1 | tail -10
```
Expected: all UI tests pass (the legacy `hx-swap-oob="true"` behaviour is preserved for any test that asserts on it).

- [ ] **Step 6: Commit**

```bash
git -C /Users/scottgalloway/RiderProjects/stylobot add \
  src/Mostlylucid.BotDetection.UI/Middleware/WidgetRenderHelpers.cs \
  src/Mostlylucid.BotDetection.Test/UI/WidgetRenderHelpersInjectOobTests.cs
git -C /Users/scottgalloway/RiderProjects/stylobot commit -m "fix(ui): inject hx-swap-oob=innerHTML on data-sb-data-region

Two-region widget contract: chrome SSR'd once, data region innerHTML-swapped
on every SignalR beacon. Partials without [data-sb-data-region] fall back
to the legacy outerHTML-on-root path so unmigrated widgets keep working."
```

---

### Task 12: Smoke-test build and run the full FOSS test suite

**Files:**
- None (verification only)

- [ ] **Step 1: Build the whole solution**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build 2>&1 | tail -10
```
Expected: `Build succeeded.` with 0 errors. Warnings are acceptable.

- [ ] **Step 2: Run the full UI test suite**

```bash
dotnet test src/Mostlylucid.BotDetection.Test --no-restore 2>&1 | tail -10
```
Expected: all tests pass.

- [ ] **Step 3: Verify the eight partials are on disk**

```bash
ls /Users/scottgalloway/RiderProjects/stylobot/src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Primitives/
```
Expected:
```
_CountryFlag.cshtml
_IntentIcon.cshtml
_RiskBar.cshtml
_Sparkline.cshtml
_TableToolbar.cshtml
_TablePagination.cshtml
_ThreatIcon.cshtml
_TimeAgo.cshtml
```

No commit for this task — it's verification only.

---

## What's NOT in this plan (follow-on plans)

1. **Sparkline data plumbing.** Adding `int[] Trend` / `int[] HumanTrend` to `DashboardTopBotEntry` (and the parallel row records) and populating them in `SignatureAggregateCache` with a 60-bucket per-minute ring buffer. Separate plan because it touches the aggregate cache hot path and the event ingestion side and deserves its own review.
2. **SbTopBots migration to two-region contract + primitives.** First widget to consume the foundation. Visible "no more flicker" lands when this plan completes.
3. **Per-widget migrations for SbVisitorList, SbThreatsList, SbEndpointsList, SbSessionsList, SbUserAgentsList, SbCountriesList.** One plan per widget so each is a small focused PR.
4. **Overview tab layout changes.** Drop the world map from Overview (it stays in Countries), reorder rows.

These follow once this foundation is in `main`.
