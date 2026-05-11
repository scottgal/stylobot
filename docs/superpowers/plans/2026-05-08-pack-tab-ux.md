# Pack-Driven Tab UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each registered `IMonitoringPack` automatically contributes a named tab to the StyloBot dashboard via its `TabName` property, replacing the hardcoded "Metrics" tab and single `MonitoringPackEnabled` bool.

**Architecture:** `IMonitoringPack` gains `TabName`. The dashboard middleware caches a `List<PackTabInfo>` (built from `IEnumerable<IMonitoringPack>` at construction). `DashboardShellModel` exposes that list. `Index.cshtml` loops it to render tab nav and content with no pack-name strings in view code. `Enabled` defaults to `true` so all FOSS installs get the System tab out of the box.

**Tech Stack:** C# 13 / .NET 10, ASP.NET Core middleware, Razor views, xUnit.

---

## Files

| File | Change |
|---|---|
| `src/Mostlylucid.BotDetection/MonitoringPacks/IMonitoringPack.cs` | Add `TabName` to interface |
| `src/Mostlylucid.BotDetection/MonitoringPacks/AspNetMonitoringPack.cs` | Implement `TabName = "System"` |
| `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs` | Add `PackTabInfo` record; replace `MonitoringPackEnabled` bool with `MonitoringPacks` list + helpers |
| `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` | Inject `IEnumerable<IMonitoringPack>`, replace `_monitoringPackEnabled` with `_packTabs`, update tab guard and model init |
| `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml` | Replace hardcoded Metrics tab with foreach loop; generic content switch |
| `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs` | Change `Enabled` default to `true` |
| `src/Mostlylucid.BotDetection.Test/MonitoringPacks/PackUxTests.cs` | Rewrite tests for new model shape |

---

## Task 1: Add `TabName` to `IMonitoringPack` and `AspNetMonitoringPack`

**Files:**
- Modify: `src/Mostlylucid.BotDetection/MonitoringPacks/IMonitoringPack.cs`
- Modify: `src/Mostlylucid.BotDetection/MonitoringPacks/AspNetMonitoringPack.cs`

- [ ] **Step 1: Write the failing test**

Add to `src/Mostlylucid.BotDetection.Test/MonitoringPacks/PackUxTests.cs` (replace entire file):

```csharp
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class PackUxTests
{
    // ── IMonitoringPack.TabName ───────────────────────────────────────────────

    [Fact]
    public void AspNetMonitoringPack_TabName_IsSystem()
    {
        var pack = new AspNetMonitoringPack();
        Assert.Equal("System", pack.TabName);
    }

    // ── MonitoringPackOptions.Enabled defaults ────────────────────────────────

    [Fact]
    public void MonitoringPackOptions_Enabled_DefaultsToTrue()
    {
        var opts = new MonitoringPackOptions();
        Assert.True(opts.Enabled);
    }

    // ── DashboardShellModel.MonitoringPacks ───────────────────────────────────

    [Fact]
    public void DashboardShellModel_MonitoringPacks_DefaultsToEmpty()
    {
        var model = BuildShellModel([]);
        Assert.Empty(model.MonitoringPacks);
        Assert.False(model.HasPackTabs);
    }

    [Fact]
    public void DashboardShellModel_HasPackTabs_TrueWhenListNotEmpty()
    {
        var model = BuildShellModel([new PackTabInfo("aspnet-monitoring", "System")]);
        Assert.True(model.HasPackTabs);
    }

    [Fact]
    public void DashboardShellModel_IsPackTab_ReturnsTrueForRegisteredId()
    {
        var model = BuildShellModel([new PackTabInfo("aspnet-monitoring", "System")]);
        Assert.True(model.IsPackTab("aspnet-monitoring"));
        Assert.False(model.IsPackTab("metrics"));
        Assert.False(model.IsPackTab("overview"));
    }

    private static DashboardShellModel BuildShellModel(IReadOnlyList<PackTabInfo> packs) =>
        new()
        {
            CspNonce      = "test",
            BasePath      = "/stylobot",
            HubPath       = "/stylobot/hub",
            ActiveTab     = "overview",
            Summary       = null!,
            Visitors      = null!,
            YourDetection = null!,
            Countries     = null!,
            Endpoints     = null!,
            Clusters      = null!,
            UserAgents    = null!,
            TopBots       = null!,
            Sessions      = null!,
            Threats       = null!,
            License       = null!,
            MonitoringPacks = packs,
        };
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackUxTests" -v minimal
```

Expected: Several failures -`TabName` doesn't exist on interface, `Enabled` defaults to `false`, `MonitoringPacks` / `HasPackTabs` / `IsPackTab` don't exist.

- [ ] **Step 3: Add `TabName` to `IMonitoringPack`**

Replace `src/Mostlylucid.BotDetection/MonitoringPacks/IMonitoringPack.cs`:

```csharp
namespace Mostlylucid.BotDetection.MonitoringPacks;

public interface IMonitoringPack
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string TabName { get; }
    TimeSpan CollectionInterval { get; }
    IReadOnlyList<MeterCollectionGroup> MeterGroups { get; }
}

public sealed record MeterCollectionGroup(
    string MeterName,
    IReadOnlyList<InstrumentCollectionSpec> Instruments);

public sealed record InstrumentCollectionSpec(
    string InstrumentName,
    CollectedValueType ValueType,
    IReadOnlyList<KeyValuePair<string, string>>? TagFilter = null);

public enum CollectedValueType
{
    Counter,
    Gauge,
    Histogram_P50,
    Histogram_P95,
    Histogram_P99
}
```

- [ ] **Step 4: Implement `TabName` on `AspNetMonitoringPack`**

In `src/Mostlylucid.BotDetection/MonitoringPacks/AspNetMonitoringPack.cs`, add the property after `Description`:

```csharp
public string TabName => "System";
```

The file becomes:

```csharp
using Mostlylucid.BotDetection.Metrics;

namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class AspNetMonitoringPack : IMonitoringPack
{
    private readonly bool _includeHostMeters;

    public AspNetMonitoringPack(bool includeHostMeters = false)
    {
        _includeHostMeters = includeHostMeters;
    }

    public string Id          => "aspnet-monitoring";
    public string Name        => "ASP.NET + StyloBot Metrics";
    public string Description => "StyloBot operational meters and optional ASP.NET host metrics";
    public string TabName     => "System";
    public TimeSpan CollectionInterval => TimeSpan.FromSeconds(60);

    public IReadOnlyList<MeterCollectionGroup> MeterGroups => BuildGroups();

    private IReadOnlyList<MeterCollectionGroup> BuildGroups()
    {
        var groups = new List<MeterCollectionGroup>
        {
            new(BotDetectionMetrics.MeterName, new[]
            {
                new InstrumentCollectionSpec("botdetection.requests.total",     CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.bots.detected",      CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.humans.detected",    CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.errors.total",       CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.detection.duration", CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("botdetection.detection.duration", CollectedValueType.Histogram_P95),
                new InstrumentCollectionSpec("botdetection.confidence.average", CollectedValueType.Gauge),
                new InstrumentCollectionSpec("botdetection.weightstore.cache.hits",   CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.weightstore.cache.misses", CollectedValueType.Counter),
            })
        };

        if (_includeHostMeters)
        {
            groups.Add(new("Microsoft.AspNetCore.Hosting", new[]
            {
                new InstrumentCollectionSpec("http.server.request.duration", CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("http.server.request.duration", CollectedValueType.Histogram_P95),
                new InstrumentCollectionSpec("http.server.active_requests",  CollectedValueType.Gauge),
            }));

            groups.Add(new("System.Runtime", new[]
            {
                new InstrumentCollectionSpec("dotnet.gc.heap.total_allocated",  CollectedValueType.Counter),
                new InstrumentCollectionSpec("dotnet.process.cpu.time",         CollectedValueType.Counter),
                new InstrumentCollectionSpec("dotnet.thread_pool.thread.count", CollectedValueType.Gauge),
            }));
        }

        return groups;
    }
}
```

- [ ] **Step 5: Build to catch any other `IMonitoringPack` implementors that need `TabName`**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep -E "error|Error" | grep -v "0 Error"
```

Expected: Build errors for any other class implementing `IMonitoringPack` that is missing `TabName`. Fix each by adding `public string TabName => "Your Tab Name";`. If none, no errors.

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/MonitoringPacks/IMonitoringPack.cs \
        src/Mostlylucid.BotDetection/MonitoringPacks/AspNetMonitoringPack.cs \
        src/Mostlylucid.BotDetection.Test/MonitoringPacks/PackUxTests.cs
git commit -m "feat(monitoring): add TabName to IMonitoringPack; System on AspNetMonitoringPack"
```

---

## Task 2: Update `DashboardShellModel` and `MonitoringPackOptions`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs`

- [ ] **Step 1: Add `PackTabInfo` record and update `DashboardShellModel`**

In `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`, find the `DashboardShellModel` class (around line 390). Make these two changes:

1. Add the record just before `DashboardShellModel` (in the same file, before the class):

```csharp
/// <summary>Identifies a monitoring pack tab in the dashboard nav.</summary>
public sealed record PackTabInfo(string Id, string TabName);
```

2. In `DashboardShellModel`, replace the `MonitoringPackEnabled` property:

```csharp
// Remove:
/// <summary>True when at least one IMonitoringPack is registered in DI.</summary>
public bool MonitoringPackEnabled { get; init; }

// Replace with:
/// <summary>Monitoring packs registered in DI. Empty when monitoring is disabled.</summary>
public IReadOnlyList<PackTabInfo> MonitoringPacks { get; init; } = Array.Empty<PackTabInfo>();

public bool HasPackTabs  => MonitoringPacks.Count > 0;
public bool IsPackTab(string tab) => MonitoringPacks.Any(p => p.Id == tab);
```

- [ ] **Step 2: Change `Enabled` default to `true`**

In `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs`, find `MonitoringPackOptions.Enabled` and change the default:

```csharp
// Before:
public bool Enabled { get; set; }

// After:
public bool Enabled { get; set; } = true;
```

- [ ] **Step 3: Run the tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackUxTests" -v minimal
```

Expected: `AspNetMonitoringPack_TabName_IsSystem` passes, `MonitoringPackOptions_Enabled_DefaultsToTrue` passes, shell model tests pass. The build may have other compile errors from callers of the old `MonitoringPackEnabled` -those are fixed in Tasks 3 and 4.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs \
        src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs
git commit -m "feat(dashboard): PackTabInfo + MonitoringPacks list on shell model; Enabled defaults true"
```

---

## Task 3: Update `StyloBotDashboardMiddleware`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`

The middleware currently:
- Has `private readonly bool _monitoringPackEnabled;` (line ~128)
- Sets it from `options.MonitoringPack.Enabled` in the constructor (line ~149)
- Guards `if (tab == "metrics" && !_monitoringPackEnabled) tab = "overview";` (line ~803)
- Sets `MonitoringPackEnabled = _monitoringPackEnabled` in the shell model (line ~904)

Replace all four of these.

- [ ] **Step 1: Replace the field declaration**

Find (around line 128):
```csharp
private readonly bool _monitoringPackEnabled;
```

Replace with:
```csharp
private readonly IReadOnlyList<PackTabInfo> _packTabs;
```

- [ ] **Step 2: Update the constructor**

The constructor signature currently ends at the `ILogger` parameter. Add `IEnumerable<IMonitoringPack> monitoringPacks` before `ILogger`:

```csharp
public StyloBotDashboardMiddleware(
    RequestDelegate next,
    StyloBotDashboardOptions options,
    IDashboardEventStore eventStore,
    DashboardAggregateCache aggregateCache,
    SignatureAggregateCache signatureCache,
    RazorViewRenderer razorViewRenderer,
    IMemoryCache widgetCache,
    IWebHostEnvironment env,
    IEnumerable<IMonitoringPack> monitoringPacks,
    ILogger<StyloBotDashboardMiddleware> logger)
```

Replace the body line that set `_monitoringPackEnabled`:
```csharp
// Remove:
_monitoringPackEnabled = options.MonitoringPack.Enabled;

// Add:
_packTabs = monitoringPacks
    .Select(p => new PackTabInfo(p.Id, p.TabName))
    .ToList();
```

Also add the required using if not already present:
```csharp
using Mostlylucid.BotDetection.MonitoringPacks;
```

- [ ] **Step 3: Update the tab guard**

Find (around line 803):
```csharp
if (tab == "metrics" && !_monitoringPackEnabled) tab = "overview";
```

Replace with:
```csharp
if (_packTabs.Count > 0 && !_packTabs.Any(p => p.Id == tab) && tab != "overview"
    && tab is not ("activity" or "visitors" or "countries" or "endpoints"
                   or "sessions" or "clusters" or "threats" or "useragents"
                   or "configuration" or "investigate" or "compliance"))
    tab = "overview";
```

Actually, the simpler and more correct guard is: if the tab is a pack tab ID that is no longer registered, fall back. Replace the old line with:

```csharp
if (tab == "metrics") tab = "overview"; // "metrics" is retired; IDs come from packs now
```

- [ ] **Step 4: Update shell model construction**

Find (around line 904):
```csharp
MonitoringPackEnabled = _monitoringPackEnabled
```

Replace with:
```csharp
MonitoringPacks = _packTabs
```

- [ ] **Step 5: Build**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/ 2>&1 | grep -E "error CS" | head -20
```

Expected: 0 errors. (The `Index.cshtml` still references `MonitoringPackEnabled` which causes a runtime Razor error, not a CS build error -fixed in Task 4.)

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "feat(dashboard): middleware uses IEnumerable<IMonitoringPack> for pack tab list"
```

---

## Task 4: Update `Index.cshtml`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`

Two sections need changing: the tab nav (where the `Metrics` link is), and the content switch (where `tab == "metrics"` is).

- [ ] **Step 1: Replace the tab nav entry**

Find (around line 234-237):
```razor
@if (Model.MonitoringPackEnabled)
{
    <a href="@TabUrl("metrics")" class="px-3 py-1.5 text-xs font-medium rounded-md transition-all @TabClass("metrics")">Metrics</a>
}
```

Replace with:
```razor
@foreach (var pack in Model.MonitoringPacks)
{
    <a href="@TabUrl(pack.Id)" class="px-3 py-1.5 text-xs font-medium rounded-md transition-all @TabClass(pack.Id)">@pack.TabName</a>
}
```

- [ ] **Step 2: Replace the tab content switch entry**

Find (around line 413-416):
```razor
else if (tab == "metrics" && Model.MonitoringPackEnabled)
{
    <sb-metrics-tab />
}
```

Replace with:
```razor
else if (Model.MonitoringPacks.FirstOrDefault(p => p.Id == tab) is { } activePack)
{
    <sb-metrics-tab pack-id="@activePack.Id" />
}
```

- [ ] **Step 3: Build the solution**

```bash
dotnet build mostlylucid.stylobot.sln 2>&1 | grep -E "error|Error" | grep -v "0 Error"
```

Expected: 0 errors.

- [ ] **Step 4: Run all tests**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ -v minimal 2>&1 | tail -10
```

Expected: All tests pass. (One pre-existing timing-sensitive test `MachineSpeedRequest_SubTwentyMs_WritesDivergedTrue` may flake under load - ignore if it passes in isolation.)

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml
git commit -m "feat(dashboard): pack-driven tab nav and content -no hardcoded pack names in view"
```

---

## Task 5: Smoke-test in the running demo

**Files:** None -runtime verification only.

- [ ] **Step 1: Start the demo**

```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo
```

- [ ] **Step 2: Verify the System tab appears by default**

Navigate to `http://localhost:5080/stylobot`. Confirm:
- A "System" tab is visible in the tab bar (where "Metrics" used to be)
- Clicking it loads the metrics view with StyloBot performance cards

- [ ] **Step 3: Verify deep-link works**

Navigate to `http://localhost:5080/stylobot?tab=aspnet-monitoring`. Confirm the System tab content loads (not a redirect to overview).

- [ ] **Step 4: Verify old `?tab=metrics` redirects gracefully**

Navigate to `http://localhost:5080/stylobot?tab=metrics`. Confirm it shows the Overview tab (not a blank page or error).

- [ ] **Step 5: Verify opt-out works**

In `src/Mostlylucid.BotDetection.Demo/appsettings.json`, temporarily set:
```json
"StyloBot": {
  "Dashboard": {
    "MonitoringPack": { "Enabled": false }
  }
}
```

Restart and confirm the System tab is gone. Revert the change.

- [ ] **Step 6: Final commit**

```bash
git add -u
git commit -m "test(demo): verify pack-driven tab UX end-to-end"
```

---

## Self-Review

**Spec coverage:**
- `IMonitoringPack.TabName` -Task 1 ✓
- `AspNetMonitoringPack.TabName = "System"` -Task 1 ✓
- `PackTabInfo` record -Task 2 ✓
- `MonitoringPacks` list + `HasPackTabs` + `IsPackTab` on shell model -Task 2 ✓
- `Enabled` defaults to `true` -Task 2 ✓
- Middleware injects `IEnumerable<IMonitoringPack>`, caches `_packTabs` -Task 3 ✓
- Tab guard updated -Task 3 ✓
- Shell model uses `MonitoringPacks = _packTabs` -Task 3 ✓
- `Index.cshtml` loop for nav -Task 4 ✓
- `Index.cshtml` generic content switch -Task 4 ✓
- No pack name strings in view -Task 4 ✓
- RemoteClient limitation (empty tab list) -documented in spec, no code needed ✓

**Placeholder scan:** None found.

**Type consistency:** `PackTabInfo(string Id, string TabName)` used consistently in Tasks 2, 3, 4. `_packTabs` is `IReadOnlyList<PackTabInfo>` everywhere.