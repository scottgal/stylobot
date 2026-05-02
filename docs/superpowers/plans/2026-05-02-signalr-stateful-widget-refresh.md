# SignalR Stateful Widget Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make SignalR-triggered widget refreshes respect each widget's current paging/filtering state, and cache default-state renders so burst traffic (many dashboard tabs) causes only one server-side render.

**Architecture:** Each widget embeds its current state as a `data-sb-params` attribute. The JS flush() reads those attributes and encodes them as `{widgetId}.{param}=value` query params on the batch call. Server-side, `ServeOobUpdateAsync` extracts per-widget params and passes them to render methods. A short-lived (2s) `IMemoryCache` entry keyed by `(widgetId, paramsHash)` absorbs burst traffic - default-state renders (page 1, no filter) become a shared hot key; user-specific state (page 2, filter=bots) renders on demand.

**Tech Stack:** ASP.NET Core middleware (C#), Razor partial views, HTMX OOB swaps, SignalR, `IMemoryCache`, vanilla JS in `SbLiveUpdatesTagHelper`

---

## Issues Identified (Pre-implementation)

Before writing tasks, here are the logic problems found in the current code:

1. **`InjectOobAttribute` is fragile**: finds first `>` via `IndexOf` - breaks on multiline tags or comments in the first attribute. Addressed in Task 1.
2. **`RenderCountryPartialAsync`, `RenderEndpointPartialAsync`, `RenderUaPartialAsync` hardcode defaults** - they ignore query params entirely. Only `RenderVisitorPartialAsync` reads them. Fixed in Task 3.
3. **Sessions widget uses `BuildSessionsModel(context)` not a dedicated render method** - no query param reading. Fixed in Task 3.
4. **`IMemoryCache` may not be registered** in the UI DI setup. Task 2 checks and adds it.
5. **Periodic refresh timer missing** - no fallback if SignalR goes quiet. Added in Task 4 (JS side).
6. **Cache key must be deterministic across tabs** - params must be sorted before hashing so `page=1&filter=all` and `filter=all&page=1` produce the same key. Handled in Task 2.

---

## File Map

| File | Change |
|------|--------|
| `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_VisitorList.cshtml` | Add `data-sb-params` to root element |
| `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_CountriesList.cshtml` | Add `data-sb-params` to root element |
| `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointsList.cshtml` | Add `data-sb-params` to root element |
| `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_UserAgentsList.cshtml` | Add `data-sb-params` to root element |
| `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionsList.cshtml` | Add `data-sb-params` to root element |
| `Mostlylucid.BotDetection.UI/TagHelpers/SbLiveUpdatesTagHelper.cs` | Update `flush()` to read `data-sb-params`; add periodic refresh timer |
| `Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` | `ServeOobUpdateAsync`: extract per-widget params; fix `InjectOobAttribute`; add HTML render cache; fix `RenderCountryPartialAsync`, `RenderEndpointPartialAsync`, `RenderUaPartialAsync`, sessions to read params; add `ExtractWidgetParams` helper |
| `Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs` | Ensure `AddMemoryCache()` is registered |

---

## Task 1: Fix `InjectOobAttribute` and add `data-sb-params` to widget views

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` (~line 3319)
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_VisitorList.cshtml` (~line 16)
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_CountriesList.cshtml` (~line 18)
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointsList.cshtml` (~line 16)
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_UserAgentsList.cshtml` (~line 17)
- Modify: `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionsList.cshtml` (~line 6)

- [ ] **Step 1.1: Fix `InjectOobAttribute` to use regex instead of IndexOf**

Replace the current `InjectOobAttribute` method (around line 3319) with:

```csharp
private static readonly Regex _firstTagRegex = new(
    @"^(<[a-zA-Z][^>]*?)(/?>)",
    RegexOptions.Compiled | RegexOptions.Singleline);

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

Also add `using System.Text.RegularExpressions;` at the top of the file if not present.

- [ ] **Step 1.2: Add `data-sb-params` to `_VisitorList.cshtml` root element**

The root `<div>` is currently around line 16. Change it from:

```html
<div id="visitor-list"
     data-sb-widget="visitors"
     data-sb-depends="signature,summary">
```

to:

```html
<div id="visitor-list"
     data-sb-widget="visitors"
     data-sb-depends="signature,summary"
     data-sb-params="page=@Model.Page&filter=@Model.Filter&sort=@Model.SortField&dir=@Model.SortDir">
```

- [ ] **Step 1.3: Add `data-sb-params` to `_CountriesList.cshtml` root element**

```html
<div id="countries-list"
     data-sb-widget="countries"
     data-sb-depends="countries"
     data-sb-params="page=@Model.Page&sort=@Model.SortField&dir=@Model.SortDir"
     class="card bg-base-200 transition-all duration-200">
```

- [ ] **Step 1.4: Add `data-sb-params` to `_EndpointsList.cshtml` root element**

```html
<div id="endpoints-list"
     data-sb-widget="endpoints"
     data-sb-depends="endpoints"
     data-sb-params="page=@Model.Page&sort=@Model.SortField&dir=@Model.SortDir"
     class="transition-all duration-200">
```

- [ ] **Step 1.5: Add `data-sb-params` to `_UserAgentsList.cshtml` root element**

```html
<div id="useragents-list"
     data-sb-widget="useragents"
     data-sb-depends="useragents"
     data-sb-params="page=@Model.Page&filter=@Model.Filter&sort=@Model.SortField&dir=@Model.SortDir"
     class="transition-all duration-200">
```

- [ ] **Step 1.6: Add `data-sb-params` to `_SessionsList.cshtml` root element**

```html
<div id="sessions-list"
     data-sb-widget="sessions"
     data-sb-depends="signature,summary"
     data-sb-params="page=@Model.Page&filter=@(Model.Filter ?? string.Empty)"
     class="card bg-base-200 transition-all duration-200">
```

- [ ] **Step 1.7: Build and verify no Razor compile errors**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 1.8: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_VisitorList.cshtml \
        Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_CountriesList.cshtml \
        Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_EndpointsList.cshtml \
        Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_UserAgentsList.cshtml \
        Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionsList.cshtml \
        Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "fix(dashboard): add data-sb-params to stateful widgets; fix InjectOobAttribute regex"
```

---

## Task 2: Add HTML render cache and `ExtractWidgetParams` helper to middleware

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`
- Modify: `Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 2.1: Ensure `IMemoryCache` is registered in DI**

Open `Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs`. Find where UI services are registered (look for `AddSingleton`, `AddScoped` calls for dashboard services). Add `AddMemoryCache()` if not already present:

```csharp
services.AddMemoryCache();
```

This is idempotent - safe to call multiple times.

- [ ] **Step 2.2: Inject `IMemoryCache` into `StyloBotDashboardMiddleware`**

Find the constructor of `StyloBotDashboardMiddleware`. Add `IMemoryCache memoryCache` as a parameter and store it:

```csharp
private readonly IMemoryCache _widgetCache;

// In constructor, add parameter and assignment:
public StyloBotDashboardMiddleware(
    // ... existing params ...
    IMemoryCache memoryCache)
{
    // ... existing assignments ...
    _widgetCache = memoryCache;
}
```

- [ ] **Step 2.3: Add `ExtractWidgetParams` helper method**

Add this private method to `StyloBotDashboardMiddleware`:

```csharp
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
```

This reads `?visitors.page=2&visitors.filter=bots` and returns `{page: "2", filter: "bots"}` when called with `widgetId = "visitors"`. Falls back to the real query string when called from individual partial endpoints (non-batch), so existing individual routes keep working.

- [ ] **Step 2.4: Add `ComputeWidgetCacheKey` helper method**

```csharp
private static string ComputeWidgetCacheKey(string widgetId, IQueryCollection q)
{
    // Sort params alphabetically so page=1&filter=all and filter=all&page=1 produce the same key
    var sorted = q
        .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kvp => $"{kvp.Key}={kvp.Value}");
    return $"sb:widget:{widgetId}:{string.Join("&", sorted)}";
}
```

- [ ] **Step 2.5: Wrap `RenderOobWidgetAsync` with cache**

Replace the body of `RenderOobWidgetAsync` with a cache-aware wrapper. The rendered HTML is cached for 2 seconds keyed by widget ID and params:

```csharp
private async Task<string> RenderOobWidgetAsync(HttpContext context, string widgetId)
{
    try
    {
        var widgetParams = ExtractWidgetParams(context, widgetId);
        var cacheKey = ComputeWidgetCacheKey(widgetId, widgetParams);

        if (_widgetCache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        var html = widgetId switch
        {
            "summary" => await RenderPartialAsync(context,
                "/Views/StyloBot/Dashboard/_SummaryStats.cshtml",
                await BuildSummaryStatsModelAsync(context)),
            "visitors"   => await RenderVisitorPartialAsync(context, widgetParams),
            "countries"  => await RenderCountryPartialAsync(context, widgetParams),
            "endpoints"  => await RenderEndpointPartialAsync(context, widgetParams),
            "clusters"   => await RenderPartialAsync(context,
                "/Views/StyloBot/Dashboard/_ClustersList.cshtml",
                BuildClustersModel(context)),
            "useragents" => await RenderUaPartialAsync(context, widgetParams),
            "topbots"    => await RenderPartialAsync(context,
                "/Views/StyloBot/Dashboard/_TopBotsList.cshtml",
                BuildTopBotsModel()),
            "sessions"   => await RenderSessionPartialAsync(context, widgetParams),
            "recent"     => await RenderRecentActivityPartialAsync(context),
            "your-detection" => await RenderPartialAsync(context,
                "/Views/StyloBot/Dashboard/_YourDetection.cshtml",
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

Note: `InjectOobAttribute` is now called here (not at the call site), so remove it from any other place it was called for OOB renders.

- [ ] **Step 2.6: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded. If there are errors about `RenderSessionPartialAsync` not existing or method signature mismatches, those are fixed in Task 3.

- [ ] **Step 2.7: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs \
        Mostlylucid.BotDetection.UI/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(dashboard): add widget render cache and ExtractWidgetParams helper"
```

---

## Task 3: Fix render methods to read per-widget params

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`

All render methods currently read from `context.Request.Query` directly (or hardcode defaults). They need to accept an `IQueryCollection` override so the batch endpoint can pass per-widget params while individual partial endpoints continue working unchanged.

- [ ] **Step 3.1: Update `RenderVisitorPartialAsync` signature**

Change the method to accept an optional `IQueryCollection`:

```csharp
private async Task<string> RenderVisitorPartialAsync(HttpContext context, IQueryCollection? q = null)
{
    q ??= context.Request.Query;
    var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
    var filter    = q["filter"].FirstOrDefault()   ?? "all";
    var sortField = q["sort"].FirstOrDefault()      ?? "lastSeen";
    var sortDir   = q["dir"].FirstOrDefault()       ?? "desc";
    var page      = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
    var (items, totalCount, _, _) = visitorCache.GetFiltered(filter, sortField, sortDir, page, 24);
    var model = new VisitorListModel
    {
        Visitors  = items,
        Counts    = visitorCache.GetCounts(),
        Filter    = filter,
        SortField = sortField,
        SortDir   = sortDir,
        Page      = page,
        PageSize  = 24,
        TotalCount = totalCount,
        BasePath  = _options.BasePath.TrimEnd('/')
    };
    return await _razorViewRenderer.RenderViewToStringAsync(
        "/Views/StyloBot/Dashboard/_VisitorList.cshtml", model, context);
}
```

- [ ] **Step 3.2: Update `RenderCountryPartialAsync` to read params**

```csharp
private async Task<string> RenderCountryPartialAsync(HttpContext context, IQueryCollection? q = null)
{
    q ??= context.Request.Query;
    var sortField = q["sort"].FirstOrDefault() ?? "total";
    var sortDir   = q["dir"].FirstOrDefault()  ?? "desc";
    var page      = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
    var data  = await GetCountriesDataAsync();
    var model = BuildCountriesModel(sortField, sortDir, page, 20, data);
    return await _razorViewRenderer.RenderViewToStringAsync(
        "/Views/StyloBot/Dashboard/_CountriesList.cshtml", model, context);
}
```

- [ ] **Step 3.3: Update `RenderEndpointPartialAsync` to read params**

```csharp
private async Task<string> RenderEndpointPartialAsync(HttpContext context, IQueryCollection? q = null)
{
    q ??= context.Request.Query;
    var sortField = q["sort"].FirstOrDefault() ?? "total";
    var sortDir   = q["dir"].FirstOrDefault()  ?? "desc";
    var page      = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
    var data  = await GetEndpointsDataAsync(context);
    var model = BuildEndpointsModel(sortField, sortDir, page, 25, data);
    return await _razorViewRenderer.RenderViewToStringAsync(
        "/Views/StyloBot/Dashboard/_EndpointsList.cshtml", model, context);
}
```

- [ ] **Step 3.4: Update `RenderUaPartialAsync` to read params**

```csharp
private async Task<string> RenderUaPartialAsync(HttpContext context, IQueryCollection? q = null)
{
    q ??= context.Request.Query;
    var filter    = q["filter"].FirstOrDefault() ?? "all";
    var sortField = q["sort"].FirstOrDefault()   ?? "requests";
    var sortDir   = q["dir"].FirstOrDefault()    ?? "desc";
    var page      = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
    var cached = _aggregateCache.Current.UserAgents;
    var uas    = cached.Count > 0 ? cached : await ComputeUserAgentsFallbackAsync();
    var model  = BuildUserAgentsModel(filter, sortField, sortDir, page, 25, uas);
    return await _razorViewRenderer.RenderViewToStringAsync(
        "/Views/StyloBot/Dashboard/_UserAgentsList.cshtml", model, context);
}
```

- [ ] **Step 3.5: Add `RenderSessionPartialAsync` (extract from `BuildSessionsModel` inline)**

Find the `"sessions"` case in the old `RenderOobWidgetAsync` - it called `BuildSessionsModel(context)` directly. Replace with a proper render method:

```csharp
private async Task<string> RenderSessionPartialAsync(HttpContext context, IQueryCollection? q = null)
{
    q ??= context.Request.Query;
    var filter = q["filter"].FirstOrDefault();  // null, "bot", or "human"
    var page   = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
    var model  = BuildSessionsModel(context, filter, page);
    return await _razorViewRenderer.RenderViewToStringAsync(
        "/Views/StyloBot/Dashboard/_SessionsList.cshtml", model, context);
}
```

Then update `BuildSessionsModel` to accept `filter` and `page` parameters instead of deriving them from context internally. Find the existing `BuildSessionsModel(HttpContext context)` and change its signature to:

```csharp
private SessionsListModel BuildSessionsModel(HttpContext context, string? filter = null, int page = 1)
```

Update its body to use the passed `filter` and `page` parameters rather than reading from `context.Request.Query` (if it currently does so), or simply pass them through to the data fetch call.

- [ ] **Step 3.6: Update individual partial endpoints to keep working**

Find where the individual partial endpoints (not batch) call these render methods - e.g., `case "partials/visitors":`. These call the render methods WITHOUT the `q` override, so they pass `null` and fall back to `context.Request.Query`. Verify nothing changed for those routes.

- [ ] **Step 3.7: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3.8: Commit**

```bash
git add Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "fix(dashboard): all widget render methods now respect per-widget query params"
```

---

## Task 4: Update JS coordinator to pass widget state and add periodic refresh

**Files:**
- Modify: `Mostlylucid.BotDetection.UI/TagHelpers/SbLiveUpdatesTagHelper.cs`

- [ ] **Step 4.1: Update `flush()` to read `data-sb-params` per widget**

Find `flush()` in `SbLiveUpdatesTagHelper.cs` (around line 103). Replace with:

```javascript
function flush() {
    var ids = Object.keys(pending);
    if (ids.length === 0) return;
    pending = {};

    var qs = new URLSearchParams();
    qs.set('widgets', ids.join(','));

    ids.forEach(function(wid) {
        var el = document.querySelector('[data-sb-widget="' + wid + '"]');
        if (!el) return;
        var raw = el.getAttribute('data-sb-params');
        if (!raw) return;
        try {
            new URLSearchParams(raw).forEach(function(val, key) {
                if (val !== '' && val !== 'undefined' && val !== 'null')
                    qs.set(wid + '.' + key, val);
            });
        } catch (e) { /* malformed params - skip */ }
    });

    var url = BASE + '/partials/update?' + qs.toString();
    if (typeof htmx !== 'undefined') {
        htmx.ajax('GET', url, { target: 'body', swap: 'none' });
    }
}
```

- [ ] **Step 4.2: Add periodic refresh timer**

Find the `refresh-interval` attribute handling in `SbLiveUpdatesTagHelper.cs`. The tag helper already has configurable attributes (hub-url, base-path, debounce, show-status). Add a `refresh-interval` attribute (seconds, default 30, 0 = disabled).

In the C# part of the tag helper, add:

```csharp
[HtmlAttributeName("refresh-interval")]
public int RefreshInterval { get; set; } = 30;
```

Then in the emitted JS, after the SignalR connection setup, add the periodic refresh logic. Find where `connection.on('BroadcastInvalidation', ...)` is defined and add below it:

```javascript
// Periodic refresh fallback - fires even when SignalR is quiet
var REFRESH_INTERVAL_MS = {RefreshIntervalMs};
if (REFRESH_INTERVAL_MS > 0) {
    setInterval(function() {
        // Queue all visible widgets for refresh
        document.querySelectorAll('[data-sb-widget]').forEach(function(el) {
            var wid = el.getAttribute('data-sb-widget');
            if (wid) pending[wid] = true;
        });
        flush();
    }, REFRESH_INTERVAL_MS);
}
```

Where `{RefreshIntervalMs}` is a C# interpolation of `RefreshInterval * 1000` in the tag helper's `Process` method.

- [ ] **Step 4.3: Build and verify**

```bash
dotnet build Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4.4: Commit**

```bash
git add Mostlylucid.BotDetection.UI/TagHelpers/SbLiveUpdatesTagHelper.cs
git commit -m "feat(dashboard): SignalR flush passes widget state params; add periodic refresh fallback"
```

---

## Task 5: Manual smoke test

There are no unit tests for the middleware render methods (they depend on Razor rendering infrastructure). Manual verification is the right approach.

- [ ] **Step 5.1: Run the demo app**

```bash
dotnet run --project Mostlylucid.BotDetection.Demo
```

- [ ] **Step 5.2: Open the dashboard**

Navigate to `http://localhost:5080/_stylobot`. Open browser DevTools (Network tab).

- [ ] **Step 5.3: Verify initial render has `data-sb-params`**

Inspect any widget div (e.g., `#visitor-list`). Confirm it has `data-sb-params="page=1&filter=all&sort=lastSeen&dir=desc"`.

- [ ] **Step 5.4: Navigate to page 2 on the visitor list**

Click the page 2 button. Confirm the HTMX swap updates the widget HTML and the new root element has `data-sb-params="page=2&filter=all&sort=lastSeen&dir=desc"`.

- [ ] **Step 5.5: Verify SignalR update respects page 2**

In DevTools Network tab, wait for a SignalR invalidation to fire (make a request to the demo app to trigger detection). Observe the `/partials/update` call. Confirm the URL contains `visitors.page=2`. Confirm the widget stays on page 2 after the update.

- [ ] **Step 5.6: Verify periodic refresh**

Wait 30 seconds (or temporarily set `refresh-interval="5"` on the `<sb-live-updates>` tag helper in the dashboard layout). Confirm that `/partials/update?widgets=...` is called even without any SignalR events.

- [ ] **Step 5.7: Verify cache efficiency**

Open 3 browser tabs, all on the default view (page 1, no filter). Trigger a SignalR event. Confirm only one render happens on the server (check logs for "Failed to render OOB widget" absence, and check that the debounced batch calls all return quickly from cache).

- [ ] **Step 5.8: Final commit if any fixups were needed**

```bash
git add -A
git commit -m "fix(dashboard): smoke test fixups for stateful widget refresh"
```

---

## Self-Review

**Spec coverage:**
- Widget state preserved on SignalR update: Tasks 1 (data-sb-params) + 4 (flush reads params) + 3 (render methods use params). Covered.
- Batch update efficiency: single `/partials/update` call with per-widget params. Task 4. Covered.
- Server-side cache for default state: Task 2. Covered.
- Periodic refresh fallback: Task 4. Covered.
- All stateful widgets covered: visitors, countries, endpoints, useragents, sessions. Tasks 1 + 3. Covered.
- Stateless widgets (summary, topbots, clusters, recent, your-detection): no params needed, still work via cache with empty param set. Covered.

**Placeholder scan:** None found.

**Type consistency:**
- `ExtractWidgetParams` returns `IQueryCollection` - used in `RenderVisitorPartialAsync(context, IQueryCollection? q)`, `RenderCountryPartialAsync(context, IQueryCollection? q)`, etc. Consistent.
- `RenderSessionPartialAsync(context, IQueryCollection? q)` calls `BuildSessionsModel(context, filter, page)` - signature added in Step 3.5. Consistent.
- `ComputeWidgetCacheKey(widgetId, IQueryCollection)` used in `RenderOobWidgetAsync`. Consistent.
