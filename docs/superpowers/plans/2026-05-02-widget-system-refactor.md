# Widget System Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert dashboard widget partials into proper ASP.NET Core ViewComponents with TagHelper wrappers, so the `/_stylobot` dashboard dogfoods the same components customers drop into their own Razor pages, with views overridable at the standard ASP.NET Core path.

**Architecture:** Each widget is a `ViewComponent` (data fetching + model) + `TagHelper` (clean HTML syntax) + `Default.cshtml` at `Views/Shared/Components/Sb{Name}/Default.cshtml` (standard override path). The `/_stylobot` dashboard switches from monolithic middleware rendering to using these tag helpers directly. The batch SignalR update endpoint (`/partials/update`) renders the same view files directly via `RazorViewRenderer`, keeping two code paths (Razor page via ViewComponent, SignalR batch via direct render) sharing one view. `AddStyloBotWidgets()` registers dashboard widgets without requiring full dashboard middleware - customers call this to embed widgets in their own apps.

**Tech Stack:** ASP.NET Core ViewComponents, TagHelpers, IViewComponentHelper, IMemoryCache, Razor class library views, HTMX OOB swaps, SignalR

---

## Supersedes

This plan replaces `2026-05-02-signalr-stateful-widget-refresh.md`. The `data-sb-params` work, `ExtractWidgetParams`, render cache, and JS flush changes are all included here in the correct architectural context.

---

## Service Registration Design

Three tiers (additive):

```
AddStyloBotUI()              - detection context tag helpers only (SbGate, SbBadge, etc.)
  + AddStyloBotWidgets()     - dashboard widget ViewComponents (SbVisitorList, SbCountriesList, etc.)
                               + registers /partials/update endpoint
                               + registers IMemoryCache
  + AddStyloBotDashboard()   - full /_stylobot dashboard + SignalR hub + background services
```

A customer building their own dashboard calls:
```csharp
builder.Services.AddBotDetection();
builder.Services.AddStyloBotWidgets(); // NEW
app.UseBotDetection();
app.UseStyloBotWidgets(); // NEW - registers /partials/update
```

Then in their Razor pages:
```cshtml
<sb-live-updates />
<sb-visitor-list />
<sb-countries-list />
```

---

## File Map

### New files - ViewComponents
| File | Purpose |
|------|---------|
| `ViewComponents/Dashboard/SbVisitorListViewComponent.cs` | Visitor list data + model |
| `ViewComponents/Dashboard/SbCountriesListViewComponent.cs` | Countries data + model |
| `ViewComponents/Dashboard/SbEndpointsListViewComponent.cs` | Endpoints data + model |
| `ViewComponents/Dashboard/SbUserAgentsListViewComponent.cs` | UA data + model |
| `ViewComponents/Dashboard/SbSessionsListViewComponent.cs` | Sessions data + model |
| `ViewComponents/Dashboard/SbSummaryStatsViewComponent.cs` | Summary stats data + model |
| `ViewComponents/Dashboard/SbTopBotsViewComponent.cs` | Top bots data + model |
| `ViewComponents/Dashboard/SbThreatsListViewComponent.cs` | Threats data + model |

### New files - TagHelpers
| File | Purpose |
|------|---------|
| `TagHelpers/Dashboard/SbVisitorListTagHelper.cs` | `<sb-visitor-list />` |
| `TagHelpers/Dashboard/SbCountriesListTagHelper.cs` | `<sb-countries-list />` |
| `TagHelpers/Dashboard/SbEndpointsListTagHelper.cs` | `<sb-endpoints-list />` |
| `TagHelpers/Dashboard/SbUserAgentsListTagHelper.cs` | `<sb-useragents-list />` |
| `TagHelpers/Dashboard/SbSessionsListTagHelper.cs` | `<sb-sessions-list />` |
| `TagHelpers/Dashboard/SbSummaryStatsTagHelper.cs` | `<sb-summary-stats />` |
| `TagHelpers/Dashboard/SbTopBotsTagHelper.cs` | `<sb-top-bots />` |
| `TagHelpers/Dashboard/SbThreatsListTagHelper.cs` | `<sb-threats-list />` |

### Moved files - Views
| Old path | New path |
|----------|----------|
| `Views/StyloBot/Dashboard/_VisitorList.cshtml` | `Views/Shared/Components/SbVisitorList/Default.cshtml` |
| `Views/StyloBot/Dashboard/_CountriesList.cshtml` | `Views/Shared/Components/SbCountriesList/Default.cshtml` |
| `Views/StyloBot/Dashboard/_EndpointsList.cshtml` | `Views/Shared/Components/SbEndpointsList/Default.cshtml` |
| `Views/StyloBot/Dashboard/_UserAgentsList.cshtml` | `Views/Shared/Components/SbUserAgentsList/Default.cshtml` |
| `Views/StyloBot/Dashboard/_SessionsList.cshtml` | `Views/Shared/Components/SbSessionsList/Default.cshtml` |
| `Views/StyloBot/Dashboard/_SummaryStats.cshtml` | `Views/Shared/Components/SbSummaryStats/Default.cshtml` |
| `Views/StyloBot/Dashboard/_TopBotsList.cshtml` | `Views/Shared/Components/SbTopBots/Default.cshtml` |
| `Views/StyloBot/Dashboard/_ThreatsList.cshtml` | `Views/Shared/Components/SbThreats/Default.cshtml` |

### Modified files
| File | Change |
|------|--------|
| `Extensions/StyloBotDashboardServiceExtensions.cs` | Add `AddStyloBotWidgets()`, `UseStyloBotWidgets()` |
| `Middleware/StyloBotDashboardMiddleware.cs` | Update batch endpoint: new view paths, ExtractWidgetParams, render cache, fix InjectOobAttribute |
| `TagHelpers/SbLiveUpdatesTagHelper.cs` | Update JS flush() to pass widget state; add periodic refresh |
| `Views/StyloBot/Dashboard/Index.cshtml` | Use `<sb-visitor-list />` etc. instead of hardcoded HTMX partials |

---

## Task 1: Create `Views/Shared/Components/` directory and move views

**Files:**
- Move: `Views/StyloBot/Dashboard/_VisitorList.cshtml` → `Views/Shared/Components/SbVisitorList/Default.cshtml`
- Move: `Views/StyloBot/Dashboard/_CountriesList.cshtml` → `Views/Shared/Components/SbCountriesList/Default.cshtml`
- Move: `Views/StyloBot/Dashboard/_EndpointsList.cshtml` → `Views/Shared/Components/SbEndpointsList/Default.cshtml`
- Move: `Views/StyloBot/Dashboard/_UserAgentsList.cshtml` → `Views/Shared/Components/SbUserAgentsList/Default.cshtml`
- Move: `Views/StyloBot/Dashboard/_SessionsList.cshtml` → `Views/Shared/Components/SbSessionsList/Default.cshtml`
- Move: `Views/StyloBot/Dashboard/_SummaryStats.cshtml` → `Views/Shared/Components/SbSummaryStats/Default.cshtml`
- Move: `Views/StyloBot/Dashboard/_TopBotsList.cshtml` → `Views/Shared/Components/SbTopBots/Default.cshtml`
- Move: `Views/StyloBot/Dashboard/_ThreatsList.cshtml` → `Views/Shared/Components/SbThreats/Default.cshtml`

- [ ] **Step 1.1: Create target directories**

```bash
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbVisitorList
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbCountriesList
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbEndpointsList
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbUserAgentsList
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSessionsList
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummaryStats
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbTopBots
mkdir -p Mostlylucid.BotDetection.UI/Views/Shared/Components/SbThreats
```

- [ ] **Step 1.2: Move each view file**

```bash
cd Mostlylucid.BotDetection.UI
mv Views/StyloBot/Dashboard/_VisitorList.cshtml   Views/Shared/Components/SbVisitorList/Default.cshtml
mv Views/StyloBot/Dashboard/_CountriesList.cshtml Views/Shared/Components/SbCountriesList/Default.cshtml
mv Views/StyloBot/Dashboard/_EndpointsList.cshtml Views/Shared/Components/SbEndpointsList/Default.cshtml
mv Views/StyloBot/Dashboard/_UserAgentsList.cshtml Views/Shared/Components/SbUserAgentsList/Default.cshtml
mv Views/StyloBot/Dashboard/_SessionsList.cshtml  Views/Shared/Components/SbSessionsList/Default.cshtml
mv Views/StyloBot/Dashboard/_SummaryStats.cshtml  Views/Shared/Components/SbSummaryStats/Default.cshtml
mv Views/StyloBot/Dashboard/_TopBotsList.cshtml   Views/Shared/Components/SbTopBots/Default.cshtml
mv Views/StyloBot/Dashboard/_ThreatsList.cshtml   Views/Shared/Components/SbThreats/Default.cshtml
```

- [ ] **Step 1.3: Add `data-sb-params` to each moved view's root element**

For each view, find the root `<div>` with `data-sb-widget` and add `data-sb-params`. Open each file and make the following changes:

**`Views/Shared/Components/SbVisitorList/Default.cshtml`** - root div:
```html
<div id="visitor-list"
     data-sb-widget="visitors"
     data-sb-depends="signature,summary"
     data-sb-params="page=@Model.Page&filter=@Model.Filter&sort=@Model.SortField&dir=@Model.SortDir">
```

**`Views/Shared/Components/SbCountriesList/Default.cshtml`** - root div:
```html
<div id="countries-list"
     data-sb-widget="countries"
     data-sb-depends="countries"
     data-sb-params="page=@Model.Page&sort=@Model.SortField&dir=@Model.SortDir"
     class="card bg-base-200 transition-all duration-200">
```

**`Views/Shared/Components/SbEndpointsList/Default.cshtml`** - root div:
```html
<div id="endpoints-list"
     data-sb-widget="endpoints"
     data-sb-depends="endpoints"
     data-sb-params="page=@Model.Page&sort=@Model.SortField&dir=@Model.SortDir"
     class="transition-all duration-200">
```

**`Views/Shared/Components/SbUserAgentsList/Default.cshtml`** - root div:
```html
<div id="useragents-list"
     data-sb-widget="useragents"
     data-sb-depends="useragents"
     data-sb-params="page=@Model.Page&filter=@Model.Filter&sort=@Model.SortField&dir=@Model.SortDir"
     class="transition-all duration-200">
```

**`Views/Shared/Components/SbSessionsList/Default.cshtml`** - root div:
```html
<div id="sessions-list"
     data-sb-widget="sessions"
     data-sb-depends="signature,summary"
     data-sb-params="page=@Model.Page&filter=@(Model.Filter ?? string.Empty)"
     class="card bg-base-200 transition-all duration-200">
```

**`Views/Shared/Components/SbSummaryStats/Default.cshtml`** - root div (no paging - add widget identity only):
Confirm root element already has `id="summary-stats"` and `data-sb-widget="summary"`. No `data-sb-params` needed for stateless widgets.

**`Views/Shared/Components/SbTopBots/Default.cshtml`** - confirm `data-sb-widget="topbots"` on root.

**`Views/Shared/Components/SbThreats/Default.cshtml`** - confirm `data-sb-widget="threats"` on root.

- [ ] **Step 1.4: Build to catch any view compilation errors**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded. Fix any `@Model.` property errors if model type names changed.

- [ ] **Step 1.5: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Views/
git commit -m "refactor(dashboard): move widget views to standard ViewComponent paths with data-sb-params"
```

---

## Task 2: Create dashboard ViewComponents

**Files:**
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbVisitorListViewComponent.cs`
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbCountriesListViewComponent.cs`
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbEndpointsListViewComponent.cs`
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbUserAgentsListViewComponent.cs`
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbSessionsListViewComponent.cs`
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbSummaryStatsViewComponent.cs`
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbTopBotsViewComponent.cs`
- Create: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbThreatsListViewComponent.cs`

- [ ] **Step 2.1: Create `SbVisitorListViewComponent`**

```csharp
// ViewComponents/Dashboard/SbVisitorListViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbVisitorListViewComponent(VisitorListCache cache, IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public IViewComponentResult Invoke(
        string filter = "all",
        string sort = "lastSeen",
        string dir = "desc",
        int page = 1,
        int pageSize = 24)
    {
        var (items, total, _, _) = cache.GetFiltered(filter, sort, dir, page, pageSize);
        var model = new VisitorListModel
        {
            Visitors   = items,
            Counts     = cache.GetCounts(),
            Filter     = filter,
            SortField  = sort,
            SortDir    = dir,
            Page       = page,
            PageSize   = pageSize,
            TotalCount = total,
            BasePath   = options.Value.BasePath.TrimEnd('/')
        };
        return View(model);
    }
}
```

- [ ] **Step 2.2: Create `SbCountriesListViewComponent`**

```csharp
// ViewComponents/Dashboard/SbCountriesListViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbCountriesListViewComponent(
    DashboardAggregateCache aggregateCache,
    IDashboardEventStore eventStore,
    IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string sort = "total",
        string dir = "desc",
        int page = 1,
        int pageSize = 20)
    {
        var cached = aggregateCache.Current.Countries;
        var data   = cached.Count > 0 ? cached : await eventStore.GetCountryStatsAsync(100);
        var model  = BuildModel(sort, dir, page, pageSize, data, options.Value.BasePath.TrimEnd('/'));
        return View(model);
    }

    private static CountriesListModel BuildModel(
        string sort, string dir, int page, int pageSize,
        List<DashboardCountryStats> data, string basePath)
    {
        var sorted = sort switch
        {
            "bots"  => dir == "asc" ? data.OrderBy(x => x.BotCount)   : data.OrderByDescending(x => x.BotCount),
            "risk"  => dir == "asc" ? data.OrderBy(x => x.RiskScore)  : data.OrderByDescending(x => x.RiskScore),
            _       => dir == "asc" ? data.OrderBy(x => x.TotalCount) : data.OrderByDescending(x => x.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new CountriesListModel
        {
            Countries  = paged,
            SortField  = sort,
            SortDir    = dir,
            Page       = page,
            PageSize   = pageSize,
            TotalCount = data.Count,
            BasePath   = basePath
        };
    }
}
```

- [ ] **Step 2.3: Create `SbEndpointsListViewComponent`**

```csharp
// ViewComponents/Dashboard/SbEndpointsListViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbEndpointsListViewComponent(
    DashboardAggregateCache aggregateCache,
    IDashboardEventStore eventStore,
    IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string sort = "total",
        string dir = "desc",
        int page = 1,
        int pageSize = 25)
    {
        var cached = aggregateCache.Current.Endpoints;
        var data   = cached.Count > 0 ? cached : await eventStore.GetEndpointStatsAsync(100);
        var sorted = sort switch
        {
            "bots" => dir == "asc" ? data.OrderBy(x => x.BotCount)   : data.OrderByDescending(x => x.BotCount),
            _      => dir == "asc" ? data.OrderBy(x => x.TotalCount) : data.OrderByDescending(x => x.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var model = new EndpointsListModel
        {
            Endpoints  = paged,
            SortField  = sort,
            SortDir    = dir,
            Page       = page,
            PageSize   = pageSize,
            TotalCount = data.Count,
            BasePath   = options.Value.BasePath.TrimEnd('/')
        };
        return View(model);
    }
}
```

- [ ] **Step 2.4: Create `SbUserAgentsListViewComponent`**

```csharp
// ViewComponents/Dashboard/SbUserAgentsListViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbUserAgentsListViewComponent(
    DashboardAggregateCache aggregateCache,
    IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public IViewComponentResult Invoke(
        string filter = "all",
        string sort = "requests",
        string dir = "desc",
        int page = 1,
        int pageSize = 25)
    {
        var all = aggregateCache.Current.UserAgents;
        var filtered = filter switch
        {
            "bots"   => all.Where(x => x.IsBotFamily).ToList(),
            "humans" => all.Where(x => !x.IsBotFamily).ToList(),
            _        => all
        };
        var sorted = sort switch
        {
            "name"    => dir == "asc" ? filtered.OrderBy(x => x.Family)        : filtered.OrderByDescending(x => x.Family),
            "botrate" => dir == "asc" ? filtered.OrderBy(x => x.BotPercentage) : filtered.OrderByDescending(x => x.BotPercentage),
            _         => dir == "asc" ? filtered.OrderBy(x => x.TotalRequests) : filtered.OrderByDescending(x => x.TotalRequests)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var model = new UserAgentsListModel
        {
            UserAgents = paged,
            Filter     = filter,
            SortField  = sort,
            SortDir    = dir,
            Page       = page,
            PageSize   = pageSize,
            TotalCount = all.Count,
            BasePath   = options.Value.BasePath.TrimEnd('/')
        };
        return View(model);
    }
}
```

- [ ] **Step 2.5: Create `SbSessionsListViewComponent`**

Look up the `SessionsListModel` properties and how sessions are fetched in the existing `BuildSessionsModel` method in `StyloBotDashboardMiddleware.cs`. Mirror that logic here.

```csharp
// ViewComponents/Dashboard/SbSessionsListViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbSessionsListViewComponent(
    SignatureAggregateCache sigCache,
    IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public IViewComponentResult Invoke(
        string? filter = null,
        int page = 1,
        int pageSize = 25)
    {
        var all = sigCache.RecentSessions;
        var filtered = filter switch
        {
            "bot"   => all.Where(x => x.IsBot).ToList(),
            "human" => all.Where(x => !x.IsBot).ToList(),
            _       => all
        };
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var model = new SessionsListModel
        {
            Sessions   = paged,
            Filter     = filter,
            Page       = page,
            PageSize   = pageSize,
            TotalCount = filtered.Count,
            BasePath   = options.Value.BasePath.TrimEnd('/')
        };
        return View(model);
    }
}
```

Note: `sigCache.RecentSessions` - verify the actual property name on `SignatureAggregateCache` by reading the file. Adjust if different.

- [ ] **Step 2.6: Create `SbSummaryStatsViewComponent`**

Look at the existing `BuildSummaryStatsModelAsync` method in `StyloBotDashboardMiddleware.cs` to get the data sources. The ViewComponent is async because it may need to query the store.

```csharp
// ViewComponents/Dashboard/SbSummaryStatsViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbSummaryStatsViewComponent(
    IDashboardEventStore eventStore,
    DashboardAggregateCache aggregateCache,
    IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var summary = await eventStore.GetSummaryAsync();
        var model   = new SummaryStatsModel
        {
            TotalRequests = summary.TotalRequests,
            BotRequests   = summary.BotRequests,
            HumanRequests = summary.HumanRequests,
            BlockedCount  = summary.BlockedCount,
            BasePath      = options.Value.BasePath.TrimEnd('/')
        };
        return View(model);
    }
}
```

Note: Check actual property names on `SummaryStatsModel` - adjust to match. Look at the existing `_SummaryStats.cshtml` `@model` declaration to confirm the model type.

- [ ] **Step 2.7: Create `SbTopBotsViewComponent`**

Look at the existing `BuildTopBotsModel()` method in `StyloBotDashboardMiddleware.cs` for data source:

```csharp
// ViewComponents/Dashboard/SbTopBotsViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbTopBotsViewComponent(
    VisitorListCache cache,
    IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public IViewComponentResult Invoke(int count = 10)
    {
        var bots  = cache.GetTopBots(count);
        var model = new TopBotsModel
        {
            Bots     = bots,
            BasePath = options.Value.BasePath.TrimEnd('/')
        };
        return View(model);
    }
}
```

Note: Verify `cache.GetTopBots(count)` exists - if not, look at what `BuildTopBotsModel()` calls and replicate here.

- [ ] **Step 2.8: Create `SbThreatsListViewComponent`**

Look at the existing `RenderPartialAsync` call for threats in `StyloBotDashboardMiddleware` to get the model builder:

```csharp
// ViewComponents/Dashboard/SbThreatsListViewComponent.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbThreatsListViewComponent(
    IDashboardEventStore eventStore,
    IOptions<BotDetectionOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(int page = 1, int pageSize = 25)
    {
        var threats = await eventStore.GetRecentThreatsAsync(page, pageSize);
        var model   = new ThreatsListModel
        {
            Threats  = threats,
            BasePath = options.Value.BasePath.TrimEnd('/')
        };
        return View(model);
    }
}
```

Note: Verify `GetRecentThreatsAsync` signature and `ThreatsListModel` properties against existing code.

- [ ] **Step 2.9: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded. Fix any missing type or method errors by checking the actual model/service classes.

- [ ] **Step 2.10: Commit**

```bash
git add Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/
git commit -m "feat(widgets): add dashboard ViewComponents for all 8 stateful widgets"
```

---

## Task 3: Create dashboard TagHelper wrappers

Pattern established by existing `SbBadgeTagHelper`: inject `IViewComponentHelper`, contextualize, invoke, set output content.

**Files:**
- Create: `Mostlylucid.BotDetection.UI/TagHelpers/Dashboard/Sb{Name}TagHelper.cs` x8

- [ ] **Step 3.1: Create `SbVisitorListTagHelper`**

```csharp
// TagHelpers/Dashboard/SbVisitorListTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-visitor-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbVisitorListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("filter")]    public string Filter   { get; set; } = "all";
    [HtmlAttributeName("sort")]      public string Sort     { get; set; } = "lastSeen";
    [HtmlAttributeName("dir")]       public string Dir      { get; set; } = "desc";
    [HtmlAttributeName("page")]      public int    Page     { get; set; } = 1;
    [HtmlAttributeName("page-size")] public int    PageSize { get; set; } = 24;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(
            await vc.InvokeAsync("SbVisitorList",
                new { filter = Filter, sort = Sort, dir = Dir, page = Page, pageSize = PageSize }));
    }
}
```

- [ ] **Step 3.2: Create `SbCountriesListTagHelper`**

```csharp
// TagHelpers/Dashboard/SbCountriesListTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-countries-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbCountriesListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("sort")]      public string Sort     { get; set; } = "total";
    [HtmlAttributeName("dir")]       public string Dir      { get; set; } = "desc";
    [HtmlAttributeName("page")]      public int    Page     { get; set; } = 1;
    [HtmlAttributeName("page-size")] public int    PageSize { get; set; } = 20;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(
            await vc.InvokeAsync("SbCountriesList",
                new { sort = Sort, dir = Dir, page = Page, pageSize = PageSize }));
    }
}
```

- [ ] **Step 3.3: Create `SbEndpointsListTagHelper`**

```csharp
// TagHelpers/Dashboard/SbEndpointsListTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-endpoints-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbEndpointsListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("sort")]      public string Sort     { get; set; } = "total";
    [HtmlAttributeName("dir")]       public string Dir      { get; set; } = "desc";
    [HtmlAttributeName("page")]      public int    Page     { get; set; } = 1;
    [HtmlAttributeName("page-size")] public int    PageSize { get; set; } = 25;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(
            await vc.InvokeAsync("SbEndpointsList",
                new { sort = Sort, dir = Dir, page = Page, pageSize = PageSize }));
    }
}
```

- [ ] **Step 3.4: Create `SbUserAgentsListTagHelper`**

```csharp
// TagHelpers/Dashboard/SbUserAgentsListTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-useragents-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbUserAgentsListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("filter")]    public string Filter   { get; set; } = "all";
    [HtmlAttributeName("sort")]      public string Sort     { get; set; } = "requests";
    [HtmlAttributeName("dir")]       public string Dir      { get; set; } = "desc";
    [HtmlAttributeName("page")]      public int    Page     { get; set; } = 1;
    [HtmlAttributeName("page-size")] public int    PageSize { get; set; } = 25;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(
            await vc.InvokeAsync("SbUserAgentsList",
                new { filter = Filter, sort = Sort, dir = Dir, page = Page, pageSize = PageSize }));
    }
}
```

- [ ] **Step 3.5: Create `SbSessionsListTagHelper`**

```csharp
// TagHelpers/Dashboard/SbSessionsListTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-sessions-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbSessionsListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("filter")]    public string? Filter   { get; set; }
    [HtmlAttributeName("page")]      public int     Page     { get; set; } = 1;
    [HtmlAttributeName("page-size")] public int     PageSize { get; set; } = 25;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(
            await vc.InvokeAsync("SbSessionsList",
                new { filter = Filter, page = Page, pageSize = PageSize }));
    }
}
```

- [ ] **Step 3.6: Create `SbSummaryStatsTagHelper`**

```csharp
// TagHelpers/Dashboard/SbSummaryStatsTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-summary-stats", TagStructure = TagStructure.WithoutEndTag)]
public class SbSummaryStatsTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(await vc.InvokeAsync("SbSummaryStats", new { }));
    }
}
```

- [ ] **Step 3.7: Create `SbTopBotsTagHelper`**

```csharp
// TagHelpers/Dashboard/SbTopBotsTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-top-bots", TagStructure = TagStructure.WithoutEndTag)]
public class SbTopBotsTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("count")] public int Count { get; set; } = 10;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(await vc.InvokeAsync("SbTopBots", new { count = Count }));
    }
}
```

- [ ] **Step 3.8: Create `SbThreatsListTagHelper`**

```csharp
// TagHelpers/Dashboard/SbThreatsListTagHelper.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;

[HtmlTargetElement("sb-threats-list", TagStructure = TagStructure.WithoutEndTag)]
public class SbThreatsListTagHelper(IViewComponentHelper vc) : TagHelper
{
    [ViewContext, HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    [HtmlAttributeName("page")]      public int Page     { get; set; } = 1;
    [HtmlAttributeName("page-size")] public int PageSize { get; set; } = 25;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext != null) (vc as IViewContextAware)?.Contextualize(ViewContext);
        output.TagName = null;
        output.Content.SetHtmlContent(
            await vc.InvokeAsync("SbThreatsList",
                new { page = Page, pageSize = PageSize }));
    }
}
```

- [ ] **Step 3.9: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3.10: Commit**

```bash
git add Mostlylucid.BotDetection.UI/TagHelpers/Dashboard/
git commit -m "feat(widgets): add TagHelper wrappers for all 8 dashboard ViewComponents"
```

---

## Task 4: Add `AddStyloBotWidgets()` service registration tier

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs`

- [ ] **Step 4.1: Add `AddStyloBotWidgets()` extension method**

Open `StyloBotDashboardServiceExtensions.cs`. Add a new method between `AddStyloBotUI()` and `AddStyloBotDashboard()`:

```csharp
/// <summary>
/// Registers dashboard widget ViewComponents for use in customer Razor pages.
/// Customers call this to embed <sb-visitor-list />, <sb-countries-list /> etc.
/// without the full /_stylobot dashboard.
/// </summary>
public static IServiceCollection AddStyloBotWidgets(this IServiceCollection services,
    BotDetectionDashboardOptions? options = null)
{
    services.AddStyloBotUI();
    services.AddMemoryCache();

    if (options != null)
        services.AddSingleton(options);

    // Dashboard data services needed by widget ViewComponents
    services.TryAddSingleton<IDashboardEventStore, SqliteDashboardEventStore>();
    services.TryAddSingleton<DashboardAggregateCache>();
    services.TryAddSingleton<VisitorListCache>();
    services.TryAddSingleton<SignatureAggregateCache>();

    // SignalR for live updates
    services.AddSignalR();
    services.AddControllersWithViews();

    return services;
}
```

- [ ] **Step 4.2: Add `UseStyloBotWidgets()` extension method on `IApplicationBuilder`**

In the same file, add:

```csharp
/// <summary>
/// Maps the /partials/update batch endpoint and SignalR hub for widget live updates.
/// Call after UseRouting().
/// </summary>
public static IApplicationBuilder UseStyloBotWidgets(this IApplicationBuilder app,
    string basePath = "/_stylobot")
{
    // Map the batch update endpoint via the existing dashboard middleware
    // or a lightweight dedicated middleware
    app.UseMiddleware<SbWidgetBatchMiddleware>(basePath);
    return app;
}
```

Note: `SbWidgetBatchMiddleware` is a new lightweight middleware created in Task 5 that handles only the `/partials/update` endpoint, extracted from `StyloBotDashboardMiddleware`.

- [ ] **Step 4.3: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded. The `SbWidgetBatchMiddleware` reference will fail until Task 5 - that's fine, comment it out temporarily and add a TODO.

- [ ] **Step 4.4: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs
git commit -m "feat(widgets): add AddStyloBotWidgets() and UseStyloBotWidgets() service registration tier"
```

---

## Task 5: Extract `SbWidgetBatchMiddleware` and update batch rendering

This extracts the `/partials/update` endpoint from `StyloBotDashboardMiddleware` into a standalone middleware, updates view paths to the new `Views/Shared/Components/` location, adds `ExtractWidgetParams`, `ComputeWidgetCacheKey`, and the HTML render cache.

**Files:**
- Create: `Mostlylucid.BotDetection.UI/Middleware/SbWidgetBatchMiddleware.cs`
- Modify: `Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`

- [ ] **Step 5.1: Create `SbWidgetBatchMiddleware`**

```csharp
// Middleware/SbWidgetBatchMiddleware.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
/// Handles GET /{basePath}/partials/update?widgets=w1,w2&amp;w1.page=2&amp;w1.filter=bots
/// Renders requested widgets with their current state, caches default-state renders.
/// </summary>
public class SbWidgetBatchMiddleware(
    RequestDelegate next,
    RazorViewRenderer renderer,
    IMemoryCache cache,
    IOptions<BotDetectionOptions> options,
    IServiceProvider services,
    ILogger<SbWidgetBatchMiddleware> logger)
{
    private readonly string _basePath = options.Value.BasePath.TrimEnd('/');

    private static readonly Regex FirstTagRegex = new(
        @"^(<[a-zA-Z][^>]*?)(/?>)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith(_basePath + "/partials/update", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var widgetList = context.Request.Query["widgets"].FirstOrDefault() ?? "summary";
        var widgets    = widgetList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        context.Response.ContentType = "text/html";

        var tasks = widgets.Select(w => RenderWidgetAsync(context, w)).ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var html in results)
            if (!string.IsNullOrEmpty(html))
                await context.Response.WriteAsync(html);
    }

    private async Task<string> RenderWidgetAsync(HttpContext context, string widgetId)
    {
        try
        {
            var q        = ExtractWidgetParams(context, widgetId);
            var cacheKey = ComputeWidgetCacheKey(widgetId, q);

            if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
                return cached;

            var html = await RenderByIdAsync(context, widgetId, q);
            if (string.IsNullOrEmpty(html)) return "";

            html = InjectOobAttribute(html);
            cache.Set(cacheKey, html, TimeSpan.FromSeconds(2));
            return html;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to render widget: {Widget}", widgetId);
            return "";
        }
    }

    private async Task<string> RenderByIdAsync(HttpContext context, string widgetId, IQueryCollection q)
    {
        // Resolve required services
        var visitorCache   = services.GetService<VisitorListCache>();
        var aggCache       = services.GetService<DashboardAggregateCache>();
        var sigCache       = services.GetService<SignatureAggregateCache>();
        var eventStore     = services.GetService<IDashboardEventStore>();
        var basePath       = _basePath;

        string viewPath(string name) => $"/Views/Shared/Components/{name}/Default.cshtml";

        int    PageOf(string key, int def = 1)   => int.TryParse(q[key].FirstOrDefault(), out var v) && v > 0 ? v : def;
        string StrOf(string key, string def = "") => q[key].FirstOrDefault() ?? def;

        return widgetId switch
        {
            "visitors" when visitorCache != null => await RenderVisitors(context, visitorCache, q, basePath, viewPath),
            "countries" when aggCache != null && eventStore != null
                => await RenderCountries(context, aggCache, eventStore, q, basePath, viewPath),
            "endpoints" when aggCache != null && eventStore != null
                => await RenderEndpoints(context, aggCache, eventStore, q, basePath, viewPath),
            "useragents" when aggCache != null
                => await RenderUserAgents(context, aggCache, q, basePath, viewPath),
            "sessions" when sigCache != null
                => await RenderSessions(context, sigCache, q, basePath, viewPath),
            "summary" when eventStore != null
                => await RenderSummary(context, eventStore, basePath, viewPath),
            "topbots" when visitorCache != null
                => await RenderTopBots(context, visitorCache, basePath, viewPath),
            _ => ""
        };
    }

    private async Task<string> RenderVisitors(
        HttpContext ctx, VisitorListCache cache, IQueryCollection q, string basePath,
        Func<string, string> viewPath)
    {
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sort   = q["sort"].FirstOrDefault()   ?? "lastSeen";
        var dir    = q["dir"].FirstOrDefault()    ?? "desc";
        var page   = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var (items, total, _, _) = cache.GetFiltered(filter, sort, dir, page, 24);
        var model = new VisitorListModel
        {
            Visitors = items, Counts = cache.GetCounts(),
            Filter = filter, SortField = sort, SortDir = dir,
            Page = page, PageSize = 24, TotalCount = total, BasePath = basePath
        };
        return await renderer.RenderViewToStringAsync(viewPath("SbVisitorList"), model, ctx);
    }

    private async Task<string> RenderCountries(
        HttpContext ctx, DashboardAggregateCache agg, IDashboardEventStore store,
        IQueryCollection q, string basePath, Func<string, string> viewPath)
    {
        var sort = q["sort"].FirstOrDefault() ?? "total";
        var dir  = q["dir"].FirstOrDefault()  ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var data = agg.Current.Countries.Count > 0
            ? agg.Current.Countries
            : await store.GetCountryStatsAsync(100);
        var sorted = sort switch
        {
            "bots" => dir == "asc" ? data.OrderBy(x => x.BotCount)   : data.OrderByDescending(x => x.BotCount),
            _      => dir == "asc" ? data.OrderBy(x => x.TotalCount) : data.OrderByDescending(x => x.TotalCount)
        };
        var model = new CountriesListModel
        {
            Countries = sorted.Skip((page - 1) * 20).Take(20).ToList(),
            SortField = sort, SortDir = dir, Page = page, PageSize = 20,
            TotalCount = data.Count, BasePath = basePath
        };
        return await renderer.RenderViewToStringAsync(viewPath("SbCountriesList"), model, ctx);
    }

    private async Task<string> RenderEndpoints(
        HttpContext ctx, DashboardAggregateCache agg, IDashboardEventStore store,
        IQueryCollection q, string basePath, Func<string, string> viewPath)
    {
        var sort = q["sort"].FirstOrDefault() ?? "total";
        var dir  = q["dir"].FirstOrDefault()  ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var data = agg.Current.Endpoints.Count > 0
            ? agg.Current.Endpoints
            : await store.GetEndpointStatsAsync(100);
        var sorted = sort switch
        {
            "bots" => dir == "asc" ? data.OrderBy(x => x.BotCount)   : data.OrderByDescending(x => x.BotCount),
            _      => dir == "asc" ? data.OrderBy(x => x.TotalCount) : data.OrderByDescending(x => x.TotalCount)
        };
        var model = new EndpointsListModel
        {
            Endpoints = sorted.Skip((page - 1) * 25).Take(25).ToList(),
            SortField = sort, SortDir = dir, Page = page, PageSize = 25,
            TotalCount = data.Count, BasePath = basePath
        };
        return await renderer.RenderViewToStringAsync(viewPath("SbEndpointsList"), model, ctx);
    }

    private async Task<string> RenderUserAgents(
        HttpContext ctx, DashboardAggregateCache agg,
        IQueryCollection q, string basePath, Func<string, string> viewPath)
    {
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sort   = q["sort"].FirstOrDefault()   ?? "requests";
        var dir    = q["dir"].FirstOrDefault()    ?? "desc";
        var page   = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var all    = agg.Current.UserAgents;
        var filtered = filter switch
        {
            "bots"   => all.Where(x => x.IsBotFamily).ToList(),
            "humans" => all.Where(x => !x.IsBotFamily).ToList(),
            _        => all
        };
        var sorted = sort switch
        {
            "name"    => dir == "asc" ? filtered.OrderBy(x => x.Family)        : filtered.OrderByDescending(x => x.Family),
            "botrate" => dir == "asc" ? filtered.OrderBy(x => x.BotPercentage) : filtered.OrderByDescending(x => x.BotPercentage),
            _         => dir == "asc" ? filtered.OrderBy(x => x.TotalRequests) : filtered.OrderByDescending(x => x.TotalRequests)
        };
        var model = new UserAgentsListModel
        {
            UserAgents = sorted.Skip((page - 1) * 25).Take(25).ToList(),
            Filter = filter, SortField = sort, SortDir = dir,
            Page = page, PageSize = 25, TotalCount = all.Count, BasePath = basePath
        };
        return await renderer.RenderViewToStringAsync(viewPath("SbUserAgentsList"), model, ctx);
    }

    private async Task<string> RenderSessions(
        HttpContext ctx, SignatureAggregateCache sigCache,
        IQueryCollection q, string basePath, Func<string, string> viewPath)
    {
        var filter = q["filter"].FirstOrDefault();
        var page   = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var all    = sigCache.RecentSessions;
        var filtered = filter switch
        {
            "bot"   => all.Where(x => x.IsBot).ToList(),
            "human" => all.Where(x => !x.IsBot).ToList(),
            _       => all
        };
        var model = new SessionsListModel
        {
            Sessions   = filtered.Skip((page - 1) * 25).Take(25).ToList(),
            Filter     = filter,
            Page       = page,
            PageSize   = 25,
            TotalCount = filtered.Count,
            BasePath   = basePath
        };
        return await renderer.RenderViewToStringAsync(viewPath("SbSessionsList"), model, ctx);
    }

    private async Task<string> RenderSummary(
        HttpContext ctx, IDashboardEventStore store, string basePath,
        Func<string, string> viewPath)
    {
        var summary = await store.GetSummaryAsync();
        var model   = new SummaryStatsModel
        {
            TotalRequests = summary.TotalRequests,
            BotRequests   = summary.BotRequests,
            HumanRequests = summary.HumanRequests,
            BlockedCount  = summary.BlockedCount,
            BasePath      = basePath
        };
        return await renderer.RenderViewToStringAsync(viewPath("SbSummaryStats"), model, ctx);
    }

    private async Task<string> RenderTopBots(
        HttpContext ctx, VisitorListCache cache, string basePath,
        Func<string, string> viewPath)
    {
        var bots  = cache.GetTopBots(10);
        var model = new TopBotsModel { Bots = bots, BasePath = basePath };
        return await renderer.RenderViewToStringAsync(viewPath("SbTopBots"), model, ctx);
    }

    // --- Helpers ---

    private static IQueryCollection ExtractWidgetParams(HttpContext context, string widgetId)
    {
        var prefix = widgetId + ".";
        Dictionary<string, StringValues>? dict = null;
        foreach (var kvp in context.Request.Query)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                dict ??= new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
                dict[kvp.Key[prefix.Length..]] = kvp.Value;
            }
        }
        return dict is { Count: > 0 } ? new QueryCollection(dict) : context.Request.Query;
    }

    private static string ComputeWidgetCacheKey(string widgetId, IQueryCollection q)
    {
        var sorted = q
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}={kvp.Value}");
        return $"sb:widget:{widgetId}:{string.Join("&", sorted)}";
    }

    private static string InjectOobAttribute(string html)
    {
        var match = FirstTagRegex.Match(html);
        if (!match.Success) return html;
        if (match.Value.Contains("hx-swap-oob", StringComparison.Ordinal)) return html;
        return html[..match.Groups[1].Index]
               + match.Groups[1].Value
               + " hx-swap-oob=\"true\""
               + match.Groups[2].Value
               + html[(match.Index + match.Length)..];
    }
}
```

Note: `SignatureAggregateCache.RecentSessions` - verify the property name against the actual class. Adjust if needed.

- [ ] **Step 5.2: Update `StyloBotDashboardMiddleware` to delegate `/partials/update` to the new middleware**

In `StyloBotDashboardMiddleware.cs`, find the `case "partials/update":` dispatch (around line 372). Change it to forward to the extracted middleware rather than calling `ServeOobUpdateAsync` directly. Since `StyloBotDashboardMiddleware` already has `IServiceProvider` access, this is a simple delegation:

Find `ServeOobUpdateAsync` and mark it `[Obsolete]` - it will be removed in a follow-up once the new middleware is confirmed working. Keep it compiling for now.

Update the case to simply call the new `RenderWidgetAsync` logic via `SbWidgetBatchMiddleware`. The simplest approach: `StyloBotDashboardMiddleware` also holds a reference to `SbWidgetBatchMiddleware` and delegates:

Actually - simplest approach: just update the view paths inside the existing `RenderOobWidgetAsync` to use the new paths AND add `ExtractWidgetParams` + cache. Full replacement of the method body:

```csharp
private async Task<string> RenderOobWidgetAsync(HttpContext context, string widgetId)
{
    try
    {
        var q        = ExtractWidgetParams(context, widgetId);
        var cacheKey = ComputeWidgetCacheKey(widgetId, q);

        if (_widgetCache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        string viewPath(string name) => $"/Views/Shared/Components/{name}/Default.cshtml";

        var html = widgetId switch
        {
            "summary"    => await RenderPartialAsync(context, viewPath("SbSummaryStats"),
                                await BuildSummaryStatsModelAsync(context)),
            "visitors"   => await RenderVisitorPartialAsync(context, q),
            "countries"  => await RenderCountryPartialAsync(context, q),
            "endpoints"  => await RenderEndpointPartialAsync(context, q),
            "clusters"   => await RenderPartialAsync(context, viewPath("SbClusters"),
                                BuildClustersModel(context)),
            "useragents" => await RenderUaPartialAsync(context, q),
            "topbots"    => await RenderPartialAsync(context, viewPath("SbTopBots"),
                                BuildTopBotsModel()),
            "sessions"   => await RenderSessionPartialAsync(context, q),
            "recent"     => await RenderRecentActivityPartialAsync(context),
            "your-detection" => await RenderPartialAsync(context, viewPath("SbYourDetection"),
                                    BuildYourDetectionPartialModel(context)),
            _ => ""
        };

        if (!string.IsNullOrEmpty(html))
        {
            html = InjectOobAttribute(html);
            _widgetCache.Set(cacheKey, html, TimeSpan.FromSeconds(2));
        }
        return html ?? "";
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "Failed to render OOB widget: {Widget}", widgetId);
        return "";
    }
}
```

Also add to `StyloBotDashboardMiddleware`:

```csharp
private readonly IMemoryCache _widgetCache;

// In constructor (add parameter):
IMemoryCache widgetCache,
// Assignment:
_widgetCache = widgetCache;
```

And add the helpers (same as in `SbWidgetBatchMiddleware`):

```csharp
private static readonly Regex _firstTagRegex = new(
    @"^(<[a-zA-Z][^>]*?)(/?>)",
    RegexOptions.Compiled | RegexOptions.Singleline);

private static IQueryCollection ExtractWidgetParams(HttpContext context, string widgetId)
{
    var prefix = widgetId + ".";
    Dictionary<string, StringValues>? dict = null;
    foreach (var kvp in context.Request.Query)
    {
        if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            dict ??= new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
            dict[kvp.Key[prefix.Length..]] = kvp.Value;
        }
    }
    return dict is { Count: > 0 } ? new QueryCollection(dict) : context.Request.Query;
}

private static string ComputeWidgetCacheKey(string widgetId, IQueryCollection q)
{
    var sorted = q
        .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kvp => $"{kvp.Key}={kvp.Value}");
    return $"sb:widget:{widgetId}:{string.Join("&", sorted)}";
}

private static string InjectOobAttribute(string html)
{
    var match = _firstTagRegex.Match(html);
    if (!match.Success) return html;
    if (match.Value.Contains("hx-swap-oob", StringComparison.Ordinal)) return html;
    return html[..match.Groups[1].Index]
           + match.Groups[1].Value
           + " hx-swap-oob=\"true\""
           + match.Groups[2].Value
           + html[(match.Index + match.Length)..];
}
```

Also update the individual render methods to accept `IQueryCollection? q = null` - same changes as in the previous superseded plan (Task 3 of `2026-05-02-signalr-stateful-widget-refresh.md`):
- `RenderVisitorPartialAsync(HttpContext context, IQueryCollection? q = null)` - uses `q ?? context.Request.Query`
- `RenderCountryPartialAsync(HttpContext context, IQueryCollection? q = null)` - reads sort/dir/page from q
- `RenderEndpointPartialAsync(HttpContext context, IQueryCollection? q = null)` - reads sort/dir/page from q
- `RenderUaPartialAsync(HttpContext context, IQueryCollection? q = null)` - reads filter/sort/dir/page from q
- Add `RenderSessionPartialAsync(HttpContext context, IQueryCollection? q = null)` - reads filter/page from q

Also add `IMemoryCache` to `AddStyloBotDashboard()` in `StyloBotDashboardServiceExtensions.cs`:
```csharp
services.AddMemoryCache();
```

- [ ] **Step 5.3: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded. Fix any `IQueryCollection` import (`Microsoft.AspNetCore.Http`) or missing method errors.

- [ ] **Step 5.4: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Middleware/
git commit -m "feat(widgets): extract SbWidgetBatchMiddleware; add render cache and per-widget param routing"
```

---

## Task 6: Update `SbLiveUpdatesTagHelper` JS

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/TagHelpers/SbLiveUpdatesTagHelper.cs`

- [ ] **Step 6.1: Add `refresh-interval` C# attribute**

Find the existing attribute properties in `SbLiveUpdatesTagHelper.cs` (around where `hub-url`, `base-path`, `debounce`, `show-status` are defined). Add:

```csharp
/// <summary>Auto-refresh interval in seconds. 0 = disabled. Default: 30.</summary>
[HtmlAttributeName("refresh-interval")]
public int RefreshInterval { get; set; } = 30;
```

- [ ] **Step 6.2: Replace `flush()` and add periodic refresh in the emitted JS**

Find the `flush()` function in the embedded JS string (around line 103). Replace the entire function, and add the periodic timer. The C# string interpolation should include `RefreshInterval * 1000`:

```javascript
function flush() {
    var ids = Object.keys(pending);
    if (ids.length === 0) return;
    pending = {{}};

    var qs = new URLSearchParams();
    qs.set('widgets', ids.join(','));

    ids.forEach(function(wid) {{
        var el = document.querySelector('[data-sb-widget="' + wid + '"]');
        if (!el) return;
        var raw = el.getAttribute('data-sb-params');
        if (!raw) return;
        try {{
            new URLSearchParams(raw).forEach(function(val, key) {{
                if (val !== '' && val !== 'undefined' && val !== 'null')
                    qs.set(wid + '.' + key, val);
            }});
        }} catch (e) {{ /* malformed params - skip */ }}
    }});

    var url = BASE + '/partials/update?' + qs.toString();
    if (typeof htmx !== 'undefined') {{
        htmx.ajax('GET', url, {{ target: 'body', swap: 'none' }});
    }}
}}

// Periodic refresh fallback (fires even when SignalR is quiet)
var REFRESH_MS = {RefreshInterval * 1000};
if (REFRESH_MS > 0) {{
    setInterval(function() {{
        document.querySelectorAll('[data-sb-widget]').forEach(function(el) {{
            var wid = el.getAttribute('data-sb-widget');
            if (wid) pending[wid] = true;
        }});
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(flush, DEBOUNCE_MS);
    }}, REFRESH_MS);
}}
```

Note: `{{` and `}}` are C# string interpolation escapes for literal `{` and `}` in the JS. The `{RefreshInterval * 1000}` is a C# expression that gets evaluated at tag helper render time.

- [ ] **Step 6.3: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded.

- [ ] **Step 6.4: Commit**

```bash
git add Mostlylucid.BotDetection.UI/TagHelpers/SbLiveUpdatesTagHelper.cs
git commit -m "feat(widgets): flush() passes widget state params; add configurable periodic refresh"
```

---

## Task 7: Update dashboard `Index.cshtml` to use tag helpers

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`

- [ ] **Step 7.1: Locate HTMX-loaded widget containers in `Index.cshtml`**

Open `Index.cshtml`. Find the tab panels where widgets are rendered via HTMX `hx-get` attributes (e.g., `hx-get="/_stylobot/partials/visitors"`). These will look like:

```html
<div hx-get="/_stylobot/partials/visitors" hx-trigger="load" ...>
```

OR they may be inline partial includes. Find and replace each with the corresponding tag helper.

- [ ] **Step 7.2: Replace HTMX-loaded widget divs with tag helpers**

For each tab content area, replace the HTMX-loaded container with the tag helper. Examples:

Replace visitor list HTMX load:
```html
<!-- BEFORE -->
<div hx-get="/_stylobot/partials/visitors" hx-trigger="load" hx-swap="outerHTML">
    <!-- loading placeholder -->
</div>

<!-- AFTER -->
<sb-visitor-list />
```

Replace countries:
```html
<!-- BEFORE -->
<div hx-get="/_stylobot/partials/countries" hx-trigger="load" hx-swap="outerHTML">
</div>

<!-- AFTER -->
<sb-countries-list />
```

Replace endpoints:
```html
<sb-endpoints-list />
```

Replace user agents:
```html
<sb-useragents-list />
```

Replace sessions:
```html
<sb-sessions-list />
```

Replace summary stats (likely already rendered server-side, but normalize):
```html
<sb-summary-stats />
```

Replace top bots:
```html
<sb-top-bots />
```

Replace threats:
```html
<sb-threats-list />
```

Note: Some widgets may already be rendered server-side (not HTMX-loaded). Keep those as-is but replace with tag helpers for consistency. The exact HTMX structure varies - inspect the actual file before making changes.

- [ ] **Step 7.3: Ensure `@addTagHelper` is present for the Dashboard view**

Check `Views/StyloBot/Dashboard/_ViewImports.cshtml` (or `Views/StyloBot/_ViewImports.cshtml`). Confirm it has:

```cshtml
@addTagHelper *, Mostlylucid.BotDetection.UI
```

If not, add it.

- [ ] **Step 7.4: Build and run smoke test**

```bash
dotnet build mostlylucid.stylobot.sln
dotnet run --project Mostlylucid.BotDetection.Demo
```

Navigate to `http://localhost:5080/_stylobot`. Verify:
- All tabs load their widget content
- No HTMX errors in browser console
- Widgets render with correct data

- [ ] **Step 7.5: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml
git commit -m "refactor(dashboard): Index.cshtml uses sb-* tag helpers instead of HTMX-loaded partials"
```

---

## Self-Review

**Spec coverage:**
- ViewComponents for all 8 stateful widgets: Task 2. Covered.
- TagHelper wrappers for all 8 widgets: Task 3. Covered.
- Views at standard override path `Views/Shared/Components/{Name}/Default.cshtml`: Task 1. Covered.
- `AddStyloBotWidgets()` for customer use without full dashboard: Task 4. Covered.
- `data-sb-params` on widget root elements: Task 1 Step 1.3. Covered.
- JS flush passes widget state to batch endpoint: Task 6. Covered.
- Server-side render cache (2s TTL, keyed by widgetId+params): Task 5. Covered.
- Per-widget params extracted from `{widgetId}.{param}` query string: Task 5. Covered.
- Periodic refresh fallback (30s): Task 6. Covered.
- Dashboard dogfoods tag helpers: Task 7. Covered.
- `InjectOobAttribute` regex fix: Task 5. Covered.

**Placeholder scan:**
- Step 2.5 notes to verify `sigCache.RecentSessions` property name. This is a genuine "check before using" not a placeholder - the instruction is actionable.
- Step 2.6 notes to verify `SummaryStatsModel` property names. Same - actionable.
- Step 7.1 notes to inspect the actual `Index.cshtml` before replacing - actionable.

**Type consistency:**
- `VisitorListModel`, `CountriesListModel`, `EndpointsListModel`, `UserAgentsListModel`, `SessionsListModel`, `SummaryStatsModel`, `TopBotsModel`, `ThreatsListModel` - same model types used in ViewComponents (Task 2), batch middleware (Task 5), and views (Task 1). Consistent.
- `IQueryCollection` parameter on render methods: `ExtractWidgetParams` returns `IQueryCollection`, passed to `RenderVisitorPartialAsync(context, IQueryCollection? q)`. Consistent.
- `SbWidgetBatchMiddleware` registered in `UseStyloBotWidgets()` (Task 4) and instantiated by ASP.NET Core DI. Consistent.
