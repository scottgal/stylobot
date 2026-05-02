# Dashboard Tightening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tighten the StyloBot dashboard: fix bunched pagination, consolidate Top Visitors / Live Visitors panels, fix dead fingerprint 404s, complete inline script consolidation, and add bot/human gauge to the summary strip.

**Architecture:** Five isolated tasks that each touch different parts of the dashboard. No shared state between tasks - safe to execute sequentially. All changes are in `Mostlylucid.BotDetection.UI`.

**Tech Stack:** ASP.NET Core Razor ViewComponents, TagHelpers, HTMX, SVG (server-side), C#

---

## File Map

| File | Change |
|------|--------|
| `Views/StyloBot/Dashboard/_Pagination.cshtml` | **Create** - shared pagination partial with ellipsis |
| `Models/DashboardPartialModels.cs` | **Modify** - add `PaginationModel` record |
| `Views/Shared/Components/SbSessionsList/Default.cshtml` | **Modify** - use shared pagination partial |
| `Views/Shared/Components/SbVisitorList/Default.cshtml` | **Modify** - use shared pagination partial |
| `Views/Shared/Components/SbCountriesList/Default.cshtml` | **Modify** - use shared pagination partial |
| `Views/Shared/Components/SbEndpointsList/Default.cshtml` | **Modify** - use shared pagination partial |
| `Views/Shared/Components/SbUserAgentsList/Default.cshtml` | **Modify** - use shared pagination partial |
| `Views/Shared/Components/SbThreats/Default.cshtml` | **Modify** - use shared pagination partial |
| `Middleware/StyloBotDashboardMiddleware.cs` | **Modify** - return 404 when signature not found |
| `ViewComponents/Dashboard/SbTopBotsViewComponent.cs` | **Modify** - add `filter` and `widgetId` params |
| `TagHelpers/Dashboard/SbTopBotsTagHelper.cs` | **Modify** - add `filter` and `widget-id` attributes |
| `Views/Shared/Components/SbTopBots/Default.cshtml` | **Modify** - support filter="all", dynamic heading, dynamic id |
| `Views/StyloBot/Dashboard/Index.cshtml` | **Modify** - two stacked panels on overview; replace inline script |
| `Views/Shared/Components/SbSummaryStats/Default.cshtml` | **Modify** - add SVG arc gauge alongside stat tiles |

---

## Task 1: Shared pagination partial with ellipsis

**Files:**
- Create: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Pagination.cshtml`
- Modify: `Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSessionsList/Default.cshtml` (lines 110-123)
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbVisitorList/Default.cshtml` (lines 100-125)
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbCountriesList/Default.cshtml`
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbEndpointsList/Default.cshtml`
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbUserAgentsList/Default.cshtml`
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbThreats/Default.cshtml`

- [ ] **Step 1: Add `PaginationModel` to `DashboardPartialModels.cs`**

Open `Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs` and add this record anywhere at namespace level:

```csharp
public sealed record PaginationModel
{
    public required Func<int, string> PageUrl { get; init; }
    public required int Page { get; init; }
    public required int TotalPages { get; init; }
    public required string TargetId { get; init; }
    public string CssClass { get; init; } = "";
}
```

- [ ] **Step 2: Create `_Pagination.cshtml`**

Create `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Pagination.cshtml`:

```cshtml
@using Mostlylucid.BotDetection.UI.Models
@model PaginationModel
@{
    var page = Model.Page;
    var total = Model.TotalPages;
    var target = "#" + Model.TargetId;
    var url = Model.PageUrl;

    // Build the page window: always include 1, last page, and a window of 3 around current page.
    // Fill in "..." where there are gaps.
    var pages = new System.Collections.Generic.List<int?>();
    void AddPage(int p) {
        if (pages.Count > 0 && pages[^1] is int prev && p - prev > 1) pages.Add(null); // ellipsis
        pages.Add(p);
    }
    if (total <= 7) {
        for (var i = 1; i <= total; i++) AddPage(i);
    } else {
        AddPage(1);
        for (var i = Math.Max(2, page - 1); i <= Math.Min(total - 1, page + 1); i++) AddPage(i);
        AddPage(total);
    }
}
@if (total > 1)
{
    <div class="flex items-center justify-center mt-3 @Model.CssClass">
        <div class="join">
            @if (page > 1)
            {
                <button hx-get="@url(page - 1)" hx-target="@target" hx-swap="outerHTML transition:true"
                        class="join-item btn btn-xs" title="Previous">&lsaquo;</button>
            }
            @foreach (var p in pages)
            {
                if (p is null)
                {
                    <span class="join-item btn btn-xs btn-disabled">...</span>
                }
                else
                {
                    <button hx-get="@url(p.Value)" hx-target="@target" hx-swap="outerHTML transition:true"
                            class="join-item btn btn-xs @(p.Value == page ? "btn-active" : "")">@p.Value</button>
                }
            }
            @if (page < total)
            {
                <button hx-get="@url(page + 1)" hx-target="@target" hx-swap="outerHTML transition:true"
                        class="join-item btn btn-xs" title="Next">&rsaquo;</button>
            }
        </div>
    </div>
}
```

- [ ] **Step 3: Verify `_Pagination.cshtml` compiles**

Run: `dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Replace pagination in `SbSessionsList/Default.cshtml`**

In `Views/Shared/Components/SbSessionsList/Default.cshtml`, find the pagination block (lines 109-123):

```cshtml
            @* Pagination *@
            @if (Model.TotalPages > 1)
            {
                <div class="flex justify-center mt-3">
                    <div class="join">
                        @for (var p = 1; p <= Math.Min(Model.TotalPages, 10); p++)
                        {
                            <button class="join-item btn btn-xs @(p == Model.Page ? "btn-active" : "")"
                                    hx-get="@bp/partials/sessions?page=@p&pageSize=@Model.PageSize&filter=@Model.Filter" hx-target="#sessions-list" hx-swap="outerHTML transition:true">
                                @p
                            </button>
                        }
                    </div>
                </div>
            }
```

Replace with:

```cshtml
            @await Html.PartialAsync("~/Views/StyloBot/Dashboard/_Pagination.cshtml",
                new PaginationModel
                {
                    PageUrl = p => $"{bp}/partials/sessions?page={p}&pageSize={Model.PageSize}&filter={Model.Filter}",
                    Page = Model.Page,
                    TotalPages = Model.TotalPages,
                    TargetId = "sessions-list"
                })
```

- [ ] **Step 5: Replace pagination in `SbVisitorList/Default.cshtml`**

In `Views/Shared/Components/SbVisitorList/Default.cshtml`, replace BOTH pagination blocks (the top one at lines 59-81 and the bottom one at lines 98-125) with a single call after the card grid:

Find the top pagination block (`@* Pagination top *@`... `</div>`) and remove it entirely.

Find the bottom pagination block (`@* Pagination bottom *@`... `</div>`) and replace it:

```cshtml
    @await Html.PartialAsync("~/Views/StyloBot/Dashboard/_Pagination.cshtml",
        new PaginationModel
        {
            PageUrl = p => PageUrl(p),
            Page = Model.Page,
            TotalPages = Model.TotalPages,
            TargetId = "visitor-list"
        })
```

- [ ] **Step 6: Replace pagination in `SbCountriesList/Default.cshtml`**

Find the pagination block (look for `join-item btn btn-xs` in the paginated section) and replace with:

```cshtml
@await Html.PartialAsync("~/Views/StyloBot/Dashboard/_Pagination.cshtml",
    new PaginationModel
    {
        PageUrl = p => $"{bp}/partials/countries?page={p}&pageSize={Model.PageSize}&sort={Model.SortField}&dir={Model.SortDir}",
        Page = Model.Page,
        TotalPages = Model.TotalPages,
        TargetId = "countries-list"
    })
```

- [ ] **Step 7: Replace pagination in `SbEndpointsList/Default.cshtml`**

Same pattern:

```cshtml
@await Html.PartialAsync("~/Views/StyloBot/Dashboard/_Pagination.cshtml",
    new PaginationModel
    {
        PageUrl = p => $"{bp}/partials/endpoints?page={p}&pageSize={Model.PageSize}&sort={Model.SortField}&dir={Model.SortDir}",
        Page = Model.Page,
        TotalPages = Model.TotalPages,
        TargetId = "endpoints-list"
    })
```

- [ ] **Step 8: Replace pagination in `SbUserAgentsList/Default.cshtml`**

Same pattern (check the actual partial URL and query params used in the existing pagination):

```cshtml
@await Html.PartialAsync("~/Views/StyloBot/Dashboard/_Pagination.cshtml",
    new PaginationModel
    {
        PageUrl = p => $"{bp}/partials/useragents?page={p}&pageSize={Model.PageSize}&sort={Model.SortField}&dir={Model.SortDir}",
        Page = Model.Page,
        TotalPages = Model.TotalPages,
        TargetId = "useragents-list"
    })
```

- [ ] **Step 9: Replace pagination in `SbThreats/Default.cshtml`**

Same pattern (check actual partial URL used in existing pagination):

```cshtml
@await Html.PartialAsync("~/Views/StyloBot/Dashboard/_Pagination.cshtml",
    new PaginationModel
    {
        PageUrl = p => $"{bp}/partials/threats?page={p}&pageSize={Model.PageSize}",
        Page = Model.Page,
        TotalPages = Model.TotalPages,
        TargetId = "threats-list"
    })
```

- [ ] **Step 10: Build and verify**

Run: `dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 11: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_Pagination.cshtml
git add Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs
git add Mostlylucid.BotDetection.UI/Views/Shared/Components/
git commit -m "feat(dashboard): shared pagination partial with ellipsis across all widgets"
```

---

## Task 2: Fix dead fingerprint links (return 404 not 200)

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` (~line 3700)

Context: when a signature is not found in cache OR the event store, `ServeSignatureDetailAsync` currently builds a model with `Found = false` and renders the detail page with a 200 status. This means clicking a stale link shows a silent "not found" page. It should return 404.

- [ ] **Step 1: Locate the not-found model construction**

Open `StyloBotDashboardMiddleware.cs` and find the block that constructs `SignatureDetailModel` with `Found = false` (around line 3700):

```csharp
                else
                {
                    model = new SignatureDetailModel
                    {
                        SignatureId = decodedSignature,
                        BasePath = basePath,
                        NavBasePath = navBasePath,
                        CspNonce = cspNonce,
                        HubPath = _options.HubPath,
                        Found = false
                    };
                }
```

- [ ] **Step 2: Return 404 instead of rendering**

Replace the `else` block above with:

```csharp
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    var notFoundHtml = $"""
                        <!DOCTYPE html><html lang="en" data-theme="dark">
                        <head><meta charset="utf-8"><title>Signature Not Found - StyloBot</title>
                        <link rel="stylesheet" href="/_content/Mostlylucid.BotDetection.UI/vendor/css/tailwind.min.css" /></head>
                        <body class="min-h-screen flex items-center justify-center bg-base-100">
                        <div class="text-center">
                            <p class="text-4xl font-black text-base-content/20 mb-2">404</p>
                            <p class="text-sm text-base-content/60">Signature not found</p>
                            <p class="text-xs text-base-content/40 mt-1 font-mono">{System.Web.HttpUtility.HtmlEncode(decodedSignature[..Math.Min(24, decodedSignature.Length)])}&hellip;</p>
                            <a href="{basePath}" class="btn btn-sm btn-ghost mt-4">Back to dashboard</a>
                        </div></body></html>
                        """;
                    await context.Response.WriteAsync(notFoundHtml);
                    return;
                }
```

Note: the outer `catch (Exception ex)` block after this also falls through to rendering. Find the `catch` that follows the DB lookup and ensure it also calls `return` after setting 404. Check that catch block - it likely falls through to rendering with `Found = false` model. If so, apply the same 404 treatment there.

- [ ] **Step 3: Find the outer catch fallthrough**

After the try/catch wrapping the DB lookup, there may be a fallthrough where `model` is left unset or set to a not-found state. Check for any remaining path that renders the signature detail page with `Found = false` and ensure those also set 404.

Search in `ServeSignatureDetailAsync` for `Found = false` occurrences - there should be at most two (the DB miss case and a catch fallthrough). Both should set status 404 and return early.

- [ ] **Step 4: Build and verify**

Run: `dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "fix(dashboard): return 404 for unknown fingerprints instead of silent 200"
```

---

## Task 3: Top Visitors / Live Visitors on overview (extend SbTopBots)

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbTopBotsViewComponent.cs`
- Modify: `Mostlylucid.BotDetection.UI/TagHelpers/Dashboard/SbTopBotsTagHelper.cs`
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbTopBots/Default.cshtml`
- Modify: `Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`

Context: The current overview tab shows `<sb-top-bots />` (bots-only table) in the right 1/3 column. The user wants "Top Visitors" (all, sort by hits) and "Live Visitors" (all, sort by last seen) stacked in that column. The `SbTopBots` widget becomes a general-purpose compact visitor table with a `filter` attribute (`bots`/`all`/`humans`) and a `widget-id` attribute to allow two instances on one page.

- [ ] **Step 1: Add `Filter` and `WidgetId` to `TopBotsListModel`**

In `Models/DashboardPartialModels.cs`, find `TopBotsListModel` and add two properties:

```csharp
    public string Filter { get; set; } = "bots";      // bots | all | humans
    public string WidgetId { get; set; } = "topbots";  // HTML id prefix; allows multiple instances
```

- [ ] **Step 2: Update `SbTopBotsViewComponent.cs`**

Add `filter` and `widgetId` parameters to `InvokeAsync`:

```csharp
public async Task<IViewComponentResult> InvokeAsync(
    string sort = "default",
    string dir = "desc",
    int page = 1,
    int pageSize = 10,
    string filter = "bots",
    string widgetId = "topbots")
```

After fetching `allBots` from the signature cache, apply the filter:

```csharp
// filter: "bots" = IsBot only, "humans" = !IsBot only, "all" = everyone
var filtered = filter switch
{
    "humans" => allBots.Where(b => !b.IsBot).ToList(),
    "all"    => allBots,
    _        => allBots.Where(b => b.IsBot).ToList()   // "bots" default
};
```

Then paginate `filtered` instead of `allBots`, and set:

```csharp
model.Filter = filter;
model.WidgetId = widgetId;
```

- [ ] **Step 3: Update `SbTopBotsTagHelper.cs`**

Add two new attributes:

```csharp
[HtmlAttributeName("filter")]
public string Filter { get; set; } = "bots";

[HtmlAttributeName("widget-id")]
public string WidgetId { get; set; } = "topbots";
```

Pass them to the ViewComponent invocation:

```csharp
output.Content.SetHtmlContent(await vc.InvokeAsync("SbTopBots",
    new { sort = Sort, dir = Dir, page = Page, pageSize = PageSize, filter = Filter, widgetId = WidgetId }));
```

- [ ] **Step 4: Update `SbTopBots/Default.cshtml`**

Change the widget root `div` to use the dynamic `WidgetId`:

```cshtml
<div id="@Model.WidgetId-list"
     data-sb-widget="@Model.WidgetId"
     data-sb-depends="signature,summary"
     data-sb-params="page=@Model.Page&sort=@Model.SortField&dir=@Model.SortDir"
     class="card bg-base-200 transition-all duration-200">
```

Change the card heading to reflect the filter:

```cshtml
<h3 class="text-sm font-bold text-base-content">
    @(Model.Filter == "humans" ? "Top Humans" : Model.Filter == "all" ? "Top Visitors" : "Top Bots")
</h3>
```

Update all `hx-target` attributes to use the dynamic id (`#@(Model.WidgetId)-list`):

```cshtml
hx-target="#@(Model.WidgetId)-list"
```

Update all `SortUrl` and `PageUrl` functions to include the filter and widgetId:

```cshtml
string SortUrl(string field) {
    var dir = Model.SortField == field ? (Model.SortDir == "desc" ? "asc" : "desc") : "desc";
    return $"{bp}/partials/topbots?sort={field}&dir={dir}&page=1&pageSize={Model.PageSize}&filter={Model.Filter}&widgetId={Model.WidgetId}";
}
string PageUrl(int page) => $"{bp}/partials/topbots?sort={Model.SortField}&dir={Model.SortDir}&page={page}&pageSize={Model.PageSize}&filter={Model.Filter}&widgetId={Model.WidgetId}";
```

- [ ] **Step 5: Update batch/partial render to pass filter + widgetId**

In `StyloBotDashboardMiddleware.cs`, find the section that handles `partials/topbots` and ensure it reads `filter` and `widgetId` from the query string and passes them to the render method.

Search for `RenderTopBotsPartialAsync` (or equivalent). Add:

```csharp
var filter = context.Request.Query["filter"].FirstOrDefault() ?? "bots";
var widgetId = context.Request.Query["widgetId"].FirstOrDefault() ?? "topbots";
```

Pass these to the ViewComponent render call.

Also update `SbWidgetBatchMiddleware.cs` where it renders the `topbots` widget - it must pass `ExtractWidgetParams` values including `filter` and `widgetId` through to the ViewComponent.

- [ ] **Step 6: Update `Index.cshtml` overview tab**

Find the overview tab section (around line 215-240):

```cshtml
            <!-- ACTIVE BOTS: 1/3 width -->
            <div>
                <sb-top-bots />
            </div>
```

Replace with two stacked panels:

```cshtml
            <!-- TOP VISITORS + LIVE VISITORS: 1/3 width, stacked -->
            <div class="flex flex-col gap-4">
                <sb-top-bots filter="all" sort="hits" page-size="5" widget-id="top-visitors" />
                <sb-top-bots filter="all" sort="lastseen" page-size="5" widget-id="live-visitors" />
            </div>
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 8: Commit**

```bash
git add Mostlylucid.BotDetection.UI/ViewComponents/Dashboard/SbTopBotsViewComponent.cs
git add Mostlylucid.BotDetection.UI/TagHelpers/Dashboard/SbTopBotsTagHelper.cs
git add Mostlylucid.BotDetection.UI/Views/Shared/Components/SbTopBots/Default.cshtml
git add Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs
git add Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml
git commit -m "feat(dashboard): Top Visitors + Live Visitors panels on overview; SbTopBots filter+widgetId params"
```

---

## Task 4: Complete inline script consolidation (replace inline SignalR script)

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml`

Context: `Index.cshtml` has a 107-line inline `<script>` block (lines 430-537) that duplicates `SbLiveUpdatesTagHelper` almost exactly. The `data-sb-params` on each widget already contains the current filter/sort/dir state - the inline script's DOM inspection of the active filter button CSS class is therefore redundant. The `<span id="sb-connection-status">` is in the brand header at line 115 and can stay there; `SbLiveUpdatesTagHelper` with `show-status="false"` emits only the script, and the script already looks for `document.getElementById('sb-connection-status')`.

- [ ] **Step 1: Remove the TODO comment and inline script block**

In `Index.cshtml`, find the TODO comment starting at line 419:

```html
    <!-- TODO(task8-part-c): Replace this script block with <sb-live-updates show-status="true" />
```

And the closing `</script>` at line 537. Delete everything from that TODO comment through the closing `</script>` tag (inclusive).

- [ ] **Step 2: Add `<sb-live-updates>` tag**

In the same location (just before the `<!-- World Threat Map + Time Series Chart initialization -->` comment), add:

```cshtml
    <sb-live-updates show-status="false" />
```

This emits the SignalR + HTMX coordinator script. The `show-status="false"` means it does NOT emit a new status span - the existing `<span id="sb-connection-status">` in the brand header at line 115 is already there and the script will find it via `getElementById`.

- [ ] **Step 3: Build and verify**

Run: `dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: Remove the TODO comment from brand header if present**

The brand header has `<span id="sb-connection-status">` at around line 115. Check the context and ensure it's still there after the edit. No change needed - it stays.

- [ ] **Step 5: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Index.cshtml
git commit -m "refactor(dashboard): replace 107-line inline SignalR script with sb-live-updates tag helper"
```

---

## Task 5: Bot/Human SVG gauge in summary stats strip

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummaryStats/Default.cshtml`

Context: The summary strip currently shows a 3-column stats grid (Fingerprints, Requests, Active now, Bounce rate, Avg session, Detection). Replace the "Requests" tile with a visual SVG arc gauge showing bot traffic percentage. This is CSS/SVG only - no JS - so it works correctly with HTMX OOB swaps. ApexCharts is NOT used here; a pure SVG solution avoids chart re-initialization on swap.

Design: A semicircle arc gauge. The arc sweeps from left to right showing bot%. Color shifts from green (0%) to yellow (50%) to red (80%+). Inside the arc: the bot% number. Below: "of traffic is bots".

- [ ] **Step 1: Update `SbSummaryStats/Default.cshtml`**

Find the current file at `Views/Shared/Components/SbSummaryStats/Default.cshtml`.

Add these helper variables at the top of the `@{ ... }` block:

```cshtml
@{
    var s = Model.Summary;
    var totalFingerprints = Model.HumanSessions + Model.BotSessions;
    var botPct = totalFingerprints > 0 ? ((double)Model.BotSessions / totalFingerprints * 100).ToString("F1") : "0.0";

    // SVG gauge: semicircle from -180deg to 0deg, radius=32, stroke-width=6
    // Arc length for the full semicircle: PI * r = PI * 32 ≈ 100.53
    // dasharray: (fraction * 100.53) then gap to 201.06 (full circle circumference)
    var botFraction = totalFingerprints > 0 ? (double)Model.BotSessions / totalFingerprints : 0.0;
    var arcFull = Math.PI * 32;          // half-circumference (the visible arc)
    var arcFilled = botFraction * arcFull;
    var arcCircum = 2 * Math.PI * 32;   // full circle
    // Gauge color: green < 20%, yellow < 50%, red >= 50%
    var gaugeColor = botFraction < 0.20 ? "text-success" : botFraction < 0.50 ? "text-warning" : "text-error";
}
```

Replace the `<div id="summary-analytics" class="grid grid-cols-3 gap-2 text-sm">` section (the 6 stat tiles) with the following - keeping all 6 tiles but replacing the "Requests" tile with the gauge tile:

```cshtml
<div id="summary-stats"
     data-sb-widget="summary"
     data-sb-depends="summary"
     class="px-3 py-2 rounded-lg"
     style="background: var(--sb-card-bg); border: 1px solid var(--sb-card-border);">

    <div class="flex items-center gap-1 mb-2">
        <span class="text-[10px] font-semibold text-base-content/50 uppercase tracking-wider">Traffic Summary</span>
        @await Html.PartialAsync("~/Views/StyloBot/Dashboard/_HelpIcon.cshtml", "summary-stats")
    </div>

    <div id="summary-analytics" class="grid grid-cols-3 gap-2 text-sm">
        <div class="p-2 bg-base-200/50 rounded">
            <div class="text-base-content/50 text-[10px]">Fingerprints</div>
            <div class="font-bold text-base-content leading-tight">@totalFingerprints.ToString("N0")</div>
            <div class="text-[10px]">
                <span class="text-error">@Model.BotSessions.ToString("N0") bot</span>
                <span class="text-base-content/40 mx-0.5">/</span>
                <span class="text-success">@Model.HumanSessions.ToString("N0") human</span>
            </div>
        </div>

        @* Gauge tile: replaces the plain "Requests" tile *@
        <div class="p-2 bg-base-200/50 rounded flex flex-col items-center justify-center">
            <svg viewBox="0 0 72 44" class="w-16 h-10" aria-label="Bot traffic @botPct%">
                @* Background track (full semicircle) *@
                <path d="M 4 40 A 32 32 0 0 1 68 40"
                      fill="none" stroke="currentColor"
                      class="text-base-300" stroke-width="7" stroke-linecap="round" />
                @* Filled arc (bot fraction) *@
                <path d="M 4 40 A 32 32 0 0 1 68 40"
                      fill="none" stroke="currentColor"
                      class="@gaugeColor"
                      stroke-width="7" stroke-linecap="round"
                      stroke-dasharray="@(arcFilled.ToString("F2")) @(arcCircum.ToString("F2"))"
                      pathLength="@(arcFull.ToString("F2"))" />
                <text x="36" y="38" dominant-baseline="auto" text-anchor="middle"
                      class="text-[10px] font-bold" style="font-size:10px; fill:currentColor;">
                    @botPct%
                </text>
            </svg>
            <div class="text-[9px] text-base-content/40 mt-0.5 leading-none">bot traffic</div>
            <div class="text-[9px] text-base-content/50">@s.TotalRequests.ToString("N0") req</div>
        </div>

        <div class="p-2 bg-base-200/50 rounded">
            <div class="text-base-content/50 text-[10px]">Active now</div>
            <div class="font-bold text-base-content leading-tight">@Model.ActiveSessions</div>
            <div class="text-[10px] text-base-content/40">@Model.UniqueVisitors unique</div>
        </div>
        <div class="p-2 bg-base-200/50 rounded">
            <div class="text-base-content/50 text-[10px]">Bounce rate</div>
            <div class="font-bold text-base-content leading-tight">@Model.HumanBounceRate%</div>
            @if (Model.BounceRate != Model.HumanBounceRate && Model.BounceRate > 0)
            {
                <div class="text-[10px] text-base-content/40">@Model.BounceRate% w/ bots</div>
            }
        </div>
        <div class="p-2 bg-base-200/50 rounded">
            <div class="text-base-content/50 text-[10px]">Avg session</div>
            <div class="font-bold text-base-content leading-tight">@FormatDuration(Model.HumanAvgSessionDurationSecs)</div>
            @if (Model.BotAvgSessionDurationSecs > 0)
            {
                <div class="text-[10px] text-base-content/40">Bots: @FormatDuration(Model.BotAvgSessionDurationSecs)</div>
            }
        </div>
        <div class="p-2 bg-base-200/50 rounded">
            <div class="text-base-content/50 text-[10px]">Detection</div>
            <div class="font-bold text-base-content leading-tight">@s.UncertainRequests.ToString("N0")</div>
            <div class="text-[10px] text-base-content/40">uncertain</div>
        </div>
    </div>
</div>

@functions {
    string FormatDuration(double secs)
    {
        if (secs <= 0) return "0s";
        if (secs < 60) return $"{secs:F0}s";
        var mins = (int)(secs / 60);
        var remainSecs = (int)(secs % 60);
        return $"{mins}m {remainSecs}s";
    }
}
```

Note on the SVG gauge technique: `pathLength` normalizes the coordinate system so `stroke-dasharray` values are in the same units as `arcFull` (the semicircle arc length). This avoids computing exact arc lengths. The `d="M 4 40 A 32 32 0 0 1 68 40"` traces a semicircle arc of radius 32 from left (4,40) to right (68,40) center (36,40).

- [ ] **Step 2: Build and verify**

Run: `dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Views/Shared/Components/SbSummaryStats/Default.cshtml
git commit -m "feat(dashboard): SVG arc gauge for bot traffic % in summary stats strip"
```

---

## Self-Review

After all 5 tasks, check the following:

1. **Pagination ellipsis**: With 20 total pages and current page 10, the pagination should show: `1 ... 9 [10] 11 ... 20`. With 7 or fewer pages, it shows all pages with no ellipsis.

2. **Two instances of SbTopBots on overview**: Both `top-visitors-list` and `live-visitors-list` exist as separate DOM elements. SignalR invalidation of the `signature` signal triggers updates to both (via their `data-sb-widget` attribute values). The `SbWidgetBatchMiddleware` needs to render both when they appear in the `widgets=` param.

3. **Dead links return 404**: Navigating to `/_stylobot/signature/UNKNOWNSIGNATURE` returns a 404 page, not a 200 with "not found" message.

4. **Inline script removed**: `Index.cshtml` no longer has the 107-line SignalR script block. The `<sb-live-updates show-status="false" />` tag produces equivalent behavior. The connection status dot still appears in the brand header.

5. **SVG gauge**: The gauge renders server-side with no JS. When the summary stats OOB-swaps via HTMX, the new SVG is simply swapped in - no chart re-init needed.
