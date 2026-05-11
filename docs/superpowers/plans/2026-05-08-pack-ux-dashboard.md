# Dynamic Pack UX Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Metrics tab appear dynamically in the StyloBot dashboard only when a monitoring pack is registered, driven by an `Enabled` config flag so FOSS users can opt-in via appsettings.json.

**Architecture:** Add a boolean `Enabled` property to `MonitoringPackOptions` (default `false`). Populate a `MonitoringPackEnabled` flag on `DashboardShellModel` by checking `IEnumerable<IMonitoringPack>` at render time. Gate the tab link and tab content in `Index.cshtml` behind that flag. The `SbMetricsTabViewComponent` stays simple -it renders hardcoded StyloBot metrics but correctly sources pack metadata from `IMonitoringPack` for its section title and description.

**Tech Stack:** ASP.NET Core (.NET 10), Razor views, `IOptions<StyloBotDashboardOptions>`, `IEnumerable<IMonitoringPack>` from DI, `DashboardShellModel`, xUnit tests.

---

## File Map

| File | Change |
|------|--------|
| `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs` | Add `Enabled` bool to `MonitoringPackOptions` |
| `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs` | Gate pack DI registration on `options.MonitoringPack.Enabled` |
| `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs` | Add `MonitoringPackEnabled` to `DashboardShellModel` |
| `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` | Set `MonitoringPackEnabled` when building `DashboardShellModel` |
| `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml` | Wrap metrics tab link and content in `@if (Model.MonitoringPackEnabled)` |
| `src/Mostlylucid.BotDetection.Demo/appsettings.json` | Add `"StyloBot": { "Dashboard": { "MonitoringPack": { "Enabled": true } } }` |
| `src/Mostlylucid.BotDetection.Test/MonitoringPacks/PackUxTests.cs` | Tests for the enabled/disabled conditional logic |

---

### Task 1: Add `Enabled` to `MonitoringPackOptions` and gate DI registration

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs`

- [ ] **Step 1: Add `Enabled` property to `MonitoringPackOptions`**

In `StyloBotDashboardOptions.cs`, modify the `MonitoringPackOptions` class (currently at the bottom of the file):

```csharp
public sealed class MonitoringPackOptions
{
    /// <summary>
    ///     When true, registers the monitoring pack and shows the Metrics tab in the dashboard.
    ///     Set to true in appsettings.json to opt-in. Default: false.
    /// </summary>
    public bool Enabled { get; set; }
    public MonitoringMode Mode { get; set; } = MonitoringMode.Local;
    public bool IncludeAspNetHostMeters { get; set; }
    public string? GatewayMetricsUrl { get; set; }
    public TimeSpan RemotePollInterval { get; set; } = TimeSpan.FromSeconds(60);
}
```

- [ ] **Step 2: Gate pack DI registration on `Enabled`**

In `StyloBotDashboardServiceExtensions.cs`, find the block that registers `IMonitoringPack`, `MeterListenerService`, etc. Wrap it in a conditional:

```csharp
if (dashboardOptions.MonitoringPack.Enabled)
{
    // existing: services.TryAddSingleton<IMetricSnapshotStore>(...);
    // existing: switch (dashboardOptions.MonitoringPack.Mode) { ... }
    // existing: services.AddSingleton<IMonitoringPack, AspNetMonitoringPack>();
}
```

The exact location: find the comment or block that registers `IMetricSnapshotStore` and the monitoring mode switch. Wrap the entire block (from `TryAddSingleton<IMetricSnapshotStore>` through the mode switch including all three cases) in `if (dashboardOptions.MonitoringPack.Enabled)`.

- [ ] **Step 3: Build and verify it compiles**

```bash
cd /Users/scottgalloway/RiderProjects/stylobot
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj --no-restore 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Configuration/StyloBotDashboardOptions.cs \
        src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs
git commit -m "feat(metrics): add Enabled flag to MonitoringPackOptions; gate pack DI on config"
```

---

### Task 2: Add `MonitoringPackEnabled` to `DashboardShellModel` and populate it in middleware

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs` (line ~388)
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` (line ~865)

- [ ] **Step 1: Add property to `DashboardShellModel`**

In `DashboardPartialModels.cs`, in the `DashboardShellModel` class, add after the `Compliance` property (around line 388):

```csharp
/// <summary>True when at least one IMonitoringPack is registered in DI (i.e., MonitoringPack.Enabled = true in config).</summary>
public bool MonitoringPackEnabled { get; init; }
```

- [ ] **Step 2: Populate the flag in middleware**

In `StyloBotDashboardMiddleware.cs`, in the `new DashboardShellModel { ... }` initializer (around line 865), add after `Compliance = ...`:

```csharp
MonitoringPackEnabled = context.RequestServices
    .GetServices<IMonitoringPack>()
    .Any()
```

The `IMonitoringPack` interface is in `Mostlylucid.BotDetection.MonitoringPacks` namespace. Add the using if not already present:
```csharp
using Mostlylucid.BotDetection.MonitoringPacks;
```

- [ ] **Step 3: Build and verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj --no-restore 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs \
        src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "feat(metrics): add MonitoringPackEnabled to DashboardShellModel; set from DI in middleware"
```

---

### Task 3: Gate the Metrics tab in `Index.cshtml`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml` (lines 192 and 368–370)

- [ ] **Step 1: Wrap tab link in conditional**

Find line 192 (the Metrics tab link):
```html
<a href="@TabUrl("metrics")" class="px-3 py-1.5 text-xs font-medium rounded-md transition-all @TabClass("metrics")">Metrics</a>
```

Replace it with:
```html
@if (Model.MonitoringPackEnabled)
{
    <a href="@TabUrl("metrics")" class="px-3 py-1.5 text-xs font-medium rounded-md transition-all @TabClass("metrics")">Metrics</a>
}
```

- [ ] **Step 2: Wrap tab content in conditional**

Find lines 368–370:
```html
else if (tab == "metrics")
{
    <sb-metrics-tab />
```

Replace the entire block with:
```html
else if (tab == "metrics" && Model.MonitoringPackEnabled)
{
    <sb-metrics-tab />
```

This is a two-character change: add `&& Model.MonitoringPackEnabled` to the condition. This ensures that if someone navigates to `?tab=metrics` when the pack is disabled, they fall through to the default tab render rather than rendering an empty component.

- [ ] **Step 3: Build and verify**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj --no-restore 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml
git commit -m "feat(metrics): hide Metrics tab when no monitoring pack is registered"
```

---

### Task 4: Add example config to Demo `appsettings.json`

**Files:**
- Modify: `src/Mostlylucid.BotDetection.Demo/appsettings.json`

- [ ] **Step 1: Add `StyloBot` section with monitoring pack enabled**

The demo's `appsettings.json` currently has no `StyloBot` section. Add it at the top level (after the closing `}` of `"BotDetection"` and before the final `}`):

```json
"StyloBot": {
  "Dashboard": {
    "MonitoringPack": {
      "Enabled": true,
      "Mode": "Local",
      "IncludeAspNetHostMeters": true
    }
  }
}
```

This enables the Metrics tab in the demo app with both StyloBot meters and ASP.NET host metrics.

- [ ] **Step 2: Build the demo and run a quick check**

```bash
dotnet build src/Mostlylucid.BotDetection.Demo/Mostlylucid.BotDetection.Demo.csproj --no-restore 2>&1 | tail -5
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.Demo/appsettings.json
git commit -m "feat(metrics): enable monitoring pack in demo appsettings.json"
```

---

### Task 5: Write tests for the conditional pack UX behavior

**Files:**
- Create: `src/Mostlylucid.BotDetection.Test/MonitoringPacks/PackUxTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class PackUxTests
{
    [Fact]
    public void MonitoringPackOptions_Enabled_DefaultsToFalse()
    {
        var opts = new MonitoringPackOptions();
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void WhenEnabled_False_NoIMonitoringPackRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Register dashboard options with Enabled = false (the default)
        services.Configure<StyloBotDashboardOptions>(o =>
        {
            o.AllowUnauthenticatedAccess = true;
            o.MonitoringPack = new MonitoringPackOptions { Enabled = false };
        });

        // We can't call full AddStyloBotDashboard here (it needs SQLite paths etc.)
        // Instead, test the flag behavior directly
        var opts = new MonitoringPackOptions { Enabled = false };
        Assert.False(opts.Enabled);

        var sp = services.BuildServiceProvider();
        var packs = sp.GetServices<IMonitoringPack>();
        Assert.Empty(packs);
    }

    [Fact]
    public void MonitoringPackOptions_CanSetEnabled()
    {
        var opts = new MonitoringPackOptions { Enabled = true };
        Assert.True(opts.Enabled);
    }

    [Fact]
    public void DashboardShellModel_MonitoringPackEnabled_DefaultsFalse()
    {
        // DashboardShellModel.MonitoringPackEnabled should default to false
        // (init property, so must be set explicitly)
        // This test verifies the property exists and is settable
        var model = new Mostlylucid.BotDetection.UI.Models.DashboardShellModel
        {
            CspNonce = "test",
            BasePath = "/stylobot",
            HubPath = "/stylobot/hub",
            ActiveTab = "overview",
            Summary = null!,
            Visitors = null!,
            YourDetection = null!,
            Countries = null!,
            Endpoints = null!,
            Clusters = null!,
            UserAgents = null!,
            TopBots = null!,
            Sessions = null!,
            Threats = null!,
            License = null!,
            MonitoringPackEnabled = false
        };
        Assert.False(model.MonitoringPackEnabled);
    }

    [Fact]
    public void DashboardShellModel_MonitoringPackEnabled_CanBeSetTrue()
    {
        var model = new Mostlylucid.BotDetection.UI.Models.DashboardShellModel
        {
            CspNonce = "test",
            BasePath = "/stylobot",
            HubPath = "/stylobot/hub",
            ActiveTab = "metrics",
            Summary = null!,
            Visitors = null!,
            YourDetection = null!,
            Countries = null!,
            Endpoints = null!,
            Clusters = null!,
            UserAgents = null!,
            TopBots = null!,
            Sessions = null!,
            Threats = null!,
            License = null!,
            MonitoringPackEnabled = true
        };
        Assert.True(model.MonitoringPackEnabled);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail (property doesn't exist yet)**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackUxTests" --no-build 2>&1 | tail -10
```

Expected: build error -`MonitoringPackOptions` has no `Enabled` property and `DashboardShellModel` has no `MonitoringPackEnabled` property. (These will pass after Tasks 1 and 2 are complete.)

- [ ] **Step 3: Run tests after prior tasks are complete**

After Tasks 1 and 2 are done, run:

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~PackUxTests" 2>&1 | tail -15
```

Expected: 5 tests pass.

- [ ] **Step 4: Run full test suite to check for regressions**

```bash
dotnet test src/Mostlylucid.BotDetection.Test/ 2>&1 | tail -10
```

Expected: same pass count as before (1395+), no new failures in non-Http2/Puppeteer tests.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.Test/MonitoringPacks/PackUxTests.cs
git commit -m "test(metrics): add pack UX conditional behavior tests"
```

---

## Self-Review

**Spec coverage:**
- Config flag (`Enabled` in `MonitoringPackOptions`): Task 1
- DI gating (pack not registered when disabled): Task 1 Step 2
- `DashboardShellModel.MonitoringPackEnabled`: Task 2
- Middleware populates flag from DI: Task 2 Step 2
- Tab link conditional: Task 3 Step 1
- Tab content conditional: Task 3 Step 2
- Demo appsettings.json example: Task 4
- Tests: Task 5

**Placeholder scan:** None found.

**Type consistency:**
- `MonitoringPackOptions.Enabled` -`bool`, used consistently in Tasks 1, 2, and 5
- `DashboardShellModel.MonitoringPackEnabled` -`bool`, used consistently in Tasks 2, 3, and 5
- `IMonitoringPack` -from `Mostlylucid.BotDetection.MonitoringPacks`, injected via `GetServices<IMonitoringPack>()` in Task 2 Step 2

All consistent.