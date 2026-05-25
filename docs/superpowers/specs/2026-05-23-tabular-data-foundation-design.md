# Tabular Data Foundation - shared chrome, inline data, in-place updates

**Status:** Draft for review
**Author:** Scott + Claude (paired)
**Date:** 2026-05-23

## Problem

Every tabular surface on the FOSS dashboard (`Views/StyloBot/Dashboard/Index.cshtml` and the eight `Views/Shared/Components/Sb*List/Default.cshtml` partials) reinvents its own toolbar, pagination, sort headers, threat labels, intent labels, and probability columns. Each rebuild has ended in a different shape:

- Five separate "filter chip" implementations (Visitors has them, Top Bots does not, Threats has a different set, Endpoints has none)
- Three different pagination footers (`_Pagination.cshtml`, a custom Prev/Next in `SbTopBots/Default.cshtml`, none at all in `SbEndpointsList`)
- Threat band shown as the literal string "High" / "Medium" / "Low" in every partial - wide text columns where a 16px icon would do
- Intent shown as the literal string "Scraper" / "Probe" / "AI" - same waste
- Bot probability shown as "78%" with no visual scale
- Hit-rate-over-time shown as a single integer "1247" with no trend at all

SignalR live updates make this worse, not better. The current OOB swap (`WidgetRenderHelpers.InjectOobAttribute`, Middleware/WidgetRenderHelpers.cs:44) injects `hx-swap-oob="true"` on the widget root, which is an **outerHTML replacement**. Every beacon destroys and rebuilds the entire widget. Scroll position resets, sort indicators flash, expanded rows close, focus is lost, horizontal scroll snaps back to zero. Five prior attempts wrapped this in View Transitions, debounces, and sessionStorage rehydration - none of them stopped the destroy-and-rebuild. The user describes the result as "a flickery resetting mess."

## Non-goals

This spec is the **foundation**. It does NOT:

- Redesign the Overview tab layout (separate follow-up)
- Move or delete the world map (separate follow-up; the user's call is to drop it from Overview and leave it in Countries)
- Add new tabs, new endpoints, new data
- Add filters or sparklines to specific tables (each table swaps in primitives in its own follow-up commit, AFTER this foundation lands)

## Non-negotiables

1. **Server-rendered Razor + HTMX.** No client-side state stores. No JSON-then-render-in-JS. No virtual DOM. No chart libraries for sparklines. The server emits the SVG `<path>` string. Filter state lives in `data-sb-params` on the DOM, nowhere else.
2. **Inline data.** Sparkline trend data ships in the same payload as the row data. No new endpoints. No second fetches.
3. **Updates mutate, never replace.** SignalR beacons update the contents of the data region only. Chrome (toolbar, sort headers, pagination) is SSR'd once and stays put until the user clicks something.
4. **One change point per concern.** `InjectOobAttribute` is the single switch for swap mechanics. `_ThreatIcon` is the single source for threat visual representation. Each primitive owns its concern; no duplicate switches scattered across partials.

## Design

### 1. Two-region widget contract

Every widget partial (`Views/Shared/Components/Sb*List/Default.cshtml`) is restructured into two regions:

```html
<div id="@(Model.WidgetId)-list"
     data-sb-widget="@Model.WidgetId"
     data-sb-depends="signature,summary"
     data-sb-params="page=@Model.Page&filter=@Model.Filter&...">

    <!-- CHROME - SSR'd once, never touched by SignalR.
         Toolbar (filter chips, search, time-window), sort headers, help icon. -->
    @await Html.PartialAsync("_Primitives/_TableToolbar", toolbarModel)

    <!-- DATA REGION - the only thing SignalR mutates. -->
    <div id="@(Model.WidgetId)-data" data-sb-data-region>
        <table>
            <thead>...sort headers...</thead>
            <tbody>...rows...</tbody>
        </table>
    </div>

    <!-- CHROME - pagination footer. -->
    @await Html.PartialAsync("_Primitives/_TablePagination", paginationModel)
</div>
```

Constraints:

- The data region MUST be a single direct child element with `data-sb-data-region` and a stable id (`{widgetId}-data`).
- The data region contains ONLY data - no toolbar, no pagination, no sort headers. Sort headers stay in chrome so their indicators don't flash on every beacon.
- Chrome elements MUST NOT have ids that collide with the data region id.

### 2. OOB injection (single change point)

`WidgetRenderHelpers.InjectOobAttribute` switches behaviour:

- **Old:** finds the first tag in the response, injects `hx-swap-oob="true"`. HTMX outerHTML-replaces the widget root.
- **New:** finds the element with `data-sb-data-region`, injects `hx-swap-oob="innerHTML"`. HTMX innerHTML-replaces the data region's children. Chrome HTML in the response is sent but ignored - no matching OOB target.

If a partial does not contain `data-sb-data-region`, the helper falls back to the old behaviour with a `_logger.LogWarning` so we notice unmigrated partials in dev.

Regex change in WidgetRenderHelpers.cs:11:
- Add a second regex matching the first element with `data-sb-data-region` attribute.
- Inject `hx-swap-oob="innerHTML"` on that element.

### 3. User-initiated updates remain outerHTML

When the user clicks a filter chip, sort header, or pagination control, the existing `hx-get` direct calls with `hx-target="#widget-root"` and `hx-swap="outerHTML"` still fire. Chrome AND data re-render together. User expects the chrome to update (filter chip highlights, page number changes, sort arrow flips). This path is unchanged.

The two paths are distinguishable by URL: SignalR uses `/partials/update?widgets=...` (always renders with innerHTML OOB injected on the data region). User clicks use `/partials/{widget}?...` (no OOB injection at all - the response IS the new widget root, replacing the old one wholesale).

### 4. Edge case: total count drifts under SignalR

User is on page 14, total drops from 135 → 132 between beacons. The new page 14 is empty.

The data region renders an empty-state message:
```html
<div class="text-xs text-base-content/40 text-center py-6">
  No items on this page - <a href="..." class="link">jump to page 1</a> or refresh
</div>
```
Pagination chrome stays put (says "page 14 of 14" - stale, but not broken). User clicks the jump link or any pagination control → full widget re-render via the user-initiated path, chrome catches up.

### 5. The eight primitive partials

All live in `Views/StyloBot/Dashboard/_Primitives/`. Each accepts a typed model record, has zero side effects, and emits one self-contained HTML snippet.

#### `_ThreatIcon.cshtml`
Input: `ThreatIconModel(string Band, double BotProbability)`.
Output: a single `<i>` with the right Boxicons class and DaisyUI colour, `title="High - 78% bot probability"` for native browser tooltip.
Mapping:
- `Critical` → `bx-shield-x text-error` (filled red shield)
- `High` → `bx-shield-quarter text-error` (red)
- `Medium` → `bx-shield-quarter text-warning` (amber)
- `Low` → `bx-shield text-success` (green)
- `None` / null → `bx-shield text-base-content/30` (muted)

Single source: every table cell that currently emits "High" / "Medium" / "Low" text swaps to `<partial _ThreatIcon ... />`.

#### `_IntentIcon.cshtml`
Input: `IntentIconModel(string Intent)`.
Output: `<i>` with appropriate icon + `title` tooltip.
Mapping (using `BotDisplayHelpers` for canonical intent strings):
- `Scraper` → `bx-spider`
- `Probe` → `bx-search-alt`
- `AI` / `AiBot` → `bx-brain`
- `Tool` → `bx-wrench`
- `Browser` → `bx-globe`
- `Crawler` → `bx-network-chart`
- `Unknown` → `bx-help-circle text-base-content/30`

#### `_RiskBar.cshtml`
Input: `RiskBarModel(double Probability, string Band)`.
Output: 5-segment horizontal bar, segments filled proportional to probability, colour matching `_ThreatIcon`'s colour mapping. Lifted from the existing Investigate signature cards - extracted into a partial so all tables share it.
Width: 60px. Height: 8px. Replaces "78%" text columns (the text becomes the `title` tooltip on the bar).

#### `_Sparkline.cshtml`
Input: `SparklineModel(int[] HumanTrend, int[] BotTrend, int WindowMinutes)`.
Output: inline SVG, **server-emitted path strings**, no client JS:
```html
<svg width="60" height="18" viewBox="0 0 60 18" class="overflow-visible">
  <path d="M0,17 L1,15 L2,16 ..." stroke="var(--color-success)" fill="none" opacity="0.5" stroke-width="1" />
  <path d="M0,18 L1,18 L2,12 ..." stroke="var(--color-error)" fill="none" stroke-width="1.2" />
</svg>
```
The partial computes path coords from the int arrays (auto-scales y to the max value in either array). Title attribute carries the totals: `title="60-min: 1,247 bot · 308 human"`.

Sparkline data ships **inline** with the row payload. No new endpoint. No second fetch.

#### `_CountryFlag.cshtml`
Input: `CountryFlagModel(string? Code, string? Name)`.
Output: `<img>` referencing `/_content/Mostlylucid.BotDetection.UI/flags/{code}.svg` with `title="@Name"` for tooltip. Falls back to a `--` glyph when code is null / "XX". Lifted from the inline flag rendering already in `SbTopBots/Default.cshtml`.

#### `_TimeAgo.cshtml`
Input: `TimeAgoModel(DateTime Utc, string? RelativeText)`.
Output: `<span title="2026-05-23T09:43:21Z">3m ago</span>`. Relative text computed server-side (`Model.TimeAgo` already exists on most rows).

#### `_TableToolbar.cshtml`
Input: `TableToolbarModel(string WidgetId, string BasePath, IReadOnlyList<FilterChip> Chips, string? ActiveFilter, bool ShowSearch, bool ShowTimeWindow, string? ActiveTimeWindow)`.
Output:
- Filter chips row (HTMX `hx-get` to widget partial route with the new filter, target = widget root, swap = outerHTML)
- Optional search box (HTMX `hx-get` with debounce trigger)
- Optional time-window pills (10m / 1h / 6h / 24h)
Each chip carries `count` shown in muted text next to the label, matching the existing SbVisitorList pattern (which becomes a consumer of this partial, not a separate implementation).

#### `_TablePagination.cshtml`
Input: `TablePaginationModel(int Page, int PageSize, int TotalCount, Func<int,string> PageUrl, Func<int,string> PageSizeUrl, string TargetId)`.
Output:
- Page-size dropdown (10 / 25 / 50 / 100) - HTMX `hx-get` to widget partial with the new pageSize, resets page=1
- Numbered pages with ellipsis: `‹ 1 … 5 6 [7] 8 9 … 14 ›` (max 7 visible page numbers)
- "Showing 51–75 of 135" text
Replaces both `_Pagination.cshtml` (which is too thin) AND the custom Prev/Next blocks in `SbTopBots/Default.cshtml` and elsewhere.

### 6. Sparkline data plumbing

Server-side: `DashboardTopBotEntry` (and the parallel row records for Visitors, Threats, Endpoints, UserAgents) get a new field:

```csharp
public int[] Trend { get; init; } = Array.Empty<int>();
public int[] HumanTrend { get; init; } = Array.Empty<int>();
```

`SignatureAggregateCache.GetTopBots()` and the equivalent build paths populate these from the existing detection event stream during their existing tick. The store query is one extra `GROUP BY signature, minute_bucket` clause on the same scan that already builds aggregates - no new round trips, no separate cache.

Window: last 60 minutes, one bucket per minute. Bucket count is fixed (60) so the SVG path math is constant.

If the trend arrays are empty (cold start, no data), the partial emits an empty `<svg>` placeholder of the same dimensions so column widths don't reflow when data arrives.

### 7. Migration order

Foundation lands FIRST, in this order:

1. Add the eight primitive partials + their `_Primitives/Models/*.cs` records.
2. Change `WidgetRenderHelpers.InjectOobAttribute` to target `data-sb-data-region` with `hx-swap-oob="innerHTML"`, with fallback warning.
3. Add `Trend` / `HumanTrend` fields to row records + populate in aggregate cache.

Then, ONE WIDGET AT A TIME (each its own PR - easy to revert if a regression slips):

4. `SbTopBots` (Live Activity) - split chrome / data, swap in `_ThreatIcon`, `_IntentIcon`, `_RiskBar`, `_Sparkline`, `_CountryFlag`, `_TimeAgo`, `_TableToolbar`, `_TablePagination`.
5. `SbVisitorList` - same swap-in.
6. `SbThreatsList` - same.
7. `SbEndpointsList` - same (no Intent column; `_RiskBar` for bot-rate; sparkline for hits).
8. `SbSessionsList` - same.
9. `SbUserAgentsList` - same.
10. `SbCountriesList` - same.

Each migration is mechanical once the primitives exist. The widget chrome stops re-rendering on SignalR after step 2 lands - every widget benefits immediately, even the ones not yet migrated to primitives.

## Testing

- Unit tests for `_Primitives/Models/*.cs` records (trivial).
- Unit test for `InjectOobAttribute`: feed it a sample widget HTML with `data-sb-data-region`, assert the OOB attribute lands on the data region with `innerHTML`, not on the widget root.
- Unit test for the fallback path: HTML without `data-sb-data-region` still gets OOB injected on the root with `true` (preserves the old behaviour for partials not yet migrated).
- Browser smoke test (Playwright) at `https://www.stylobot.net/dashboard?tab=overview`:
  - Load the page, scroll the Live Activity table horizontally to position N.
  - Wait for a SignalR beacon (visible via `#sb-connection-status` activity).
  - Assert: scroll position is still N. Sort indicator is still where it was. Filter chip is still highlighted. Page number is unchanged.
  - Click "Next Page". Assert: chrome AND data re-render, page number now N+1.
- Per the memory rule "Repro first then fix": run the smoke test against prod (www.stylobot.net) before declaring done.

## What this design explicitly rejects

- **Idiomorph.** Proposed earlier, withdrawn. DOM-diff has edge cases with `<tbody>` and Alpine, and adds a vendor dependency. The two-region contract achieves the same scroll/focus/state preservation with zero dependencies.
- **Row-append / row-merge updates.** SignalR never appends. It always sends the current page's full data and innerHTML-replaces. No append, no merge, no JS-side dedup.
- **A separate sparkline endpoint.** Sparkline data is inline with the row payload. No `/api/timeseries-by-signature`. No second fetch.
- **Client-side state libraries.** No React, no Vue, no Alpine `$store` for table state. Filter / sort / page state lives in `data-sb-params` on the DOM, server-rendered.

## Decisions locked in

- **Search box target:** widget root. Filter-chip counts must update with the search, so the chrome re-renders too. Same path as filter chips and pagination - `hx-get` to the widget's partial route, `hx-target="#widget-root"`, `hx-swap="outerHTML"`. Debounced trigger: `keyup changed delay:300ms`.
- **Time-window pill scope:** filters the row set. The sparkline window matches the active pill automatically - when the pill is `1h`, the row set is signatures active in the last hour and the sparkline shows 60 one-minute buckets; when `24h`, the row set widens and the sparkline shows 24 one-hour buckets (still 60-ish data points by varying the bucket size). Bucket count stays fixed at 60 so the SVG path math is constant.