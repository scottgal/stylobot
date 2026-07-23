# Design: hidden nav links (visibility-only sidebar seam)

**Date:** 2026-07-23
**Origin:** Operator-approved FOSS seam so a commercial host (or a FOSS operator's own config) can hide specific dashboard nav rows — e.g. a "purchase" or "membership" row that only makes sense on the commercial site — without ever touching routing or detection.
**Status:** Implemented this pass (FOSS seam + FOSS default + `_SidebarV2.cshtml` wiring). Commercial site-nav wiring is a separate follow-up, owned by another agent — see "Out of scope" below.

## Problem

`_SidebarV2.cshtml` (the sidebar actually served — `DashboardLayoutOptions.V2Enabled` defaults true) renders a fixed set of static rows (Traffic / Visitors / Site / Policies / Configuration / Compliance) plus every registered `IDashboardPack`'s header + sub-rows, unconditionally. There was no seam for a host to hide a row from the sidebar while keeping its route reachable — hiding a row meant either forking the partial or actually removing the route, both wrong: FOSS must extend via seams, never fork (per the dashboard-dogfood ruling — one FOSS RCL, commercial extends additively), and detection/routing must never be touched to solve a display problem.

## Goal

A FOSS contribution seam that a caller (commercial host, or a FOSS operator via config) uses to hide specific nav rows from rendering. The underlying route stays fully reachable and fully detection-enabled — this is a rendering decision only, never an access-control decision. Default FOSS behaviour is unchanged (nothing hidden) unless configured.

Non-goals: authorization, route removal, `BotPolicyAttribute` changes, anything that makes a hidden page less reachable or less detected than a visible one.

## Design

### `INavVisibilityPolicy` (`Mostlylucid.BotDetection.UI/Services/INavVisibilityPolicy.cs`)

Same discipline as the existing precedent seam `ISignaturePolicyActionSlot`: resolved once via singleton constructor injection, called synchronously from the view (no per-render DI resolution, no `Component.InvokeAsync` round-trip).

```csharp
public interface INavVisibilityPolicy
{
    bool IsVisible(string path, bool isPrivilegedViewer);
}
```

- `path` — the nav row's logical id (`"traffic"`, `"visitors"`, `"purchase"`, `"{packId}/{subId}"` for pack sub-rows). No leading slash, no `BasePath` prefix.
- `isPrivilegedViewer` — true for viewers who must always see every row. Privileged viewers bypass hidden-path matching entirely: exactly one bypass tier, no further gating layered on top.

### `DefaultNavVisibilityPolicy` — FOSS default

Config-bound glob match against `Dashboard:HiddenPaths` (a `List<string>`, e.g. `["purchase**", "membership**"]`), reusing the existing `GlobToRegexCompiler` (`Compile(glob) -> anchored regex`) rather than writing a second glob matcher. A row is hidden (`IsVisible` returns `false`) only when a pattern matches AND the viewer is not privileged. Empty/missing config hides nothing — the safe FOSS default, no behaviour change out of the box.

Bound the same way `DashboardLayoutOptions` is bound (`services.AddOptions<NavVisibilityOptions>().BindConfiguration("Dashboard")`), consumed via `IOptionsMonitor<NavVisibilityOptions>.CurrentValue` (the same live-read pattern `EffectivePolicyComposer` uses for `BotDetectionOptions`, rather than caching a snapshot at construction) so a config reload (`POST /admin/reload`) picks up changed hidden-path patterns without a restart.

Registered via `TryAddSingleton` (mirrors `ISignaturePolicyActionSlot`) so a commercial host can register a richer implementation ahead of `AddStyloBotDashboard` if config-glob ever stops being enough — per the operator's ruling it isn't needed initially; FOSS config-glob is the whole feature for now.

### Glob semantics note

`GlobToRegexCompiler`'s `*` matches exactly one path segment (one-or-more non-slash characters) — it does **not** match zero characters and does not cross a `/`. To hide both a top-level row and everything nested under it in one pattern, use `**` (any depth, zero-or-more characters, e.g. `"purchase**"` matches `"purchase"` and `"purchase/checkout"`). A bare literal (`"purchase"`, no wildcard) hides only the exact row.

### `_SidebarV2.cshtml` wiring

`@inject INavVisibilityPolicy NavVisibility` at the top of the partial. Every static row (`traffic`, `visitors`, `site`, `policies`, `configuration`, `compliance`) is wrapped in `@if (NavVisibility.IsVisible("<id>", Model.IsPrivilegedViewer)) { <a>...</a> }`. The `@foreach (var pack in Model.Packs)` loop `continue`s past a pack whose `IsVisible(pack.Id, ...)` is false, and the nested `@foreach (var sub in pack.SubRows)` loop does the same per sub-row with path `$"{pack.Id}/{sub.Id}"`.

### `DashboardShellModel.IsPrivilegedViewer`

New property, default `false` (`Models/DashboardPartialModels.cs`):

```csharp
public const string PrivilegedViewerItemsKey = "sb.dashboard.privileged_viewer";
public bool IsPrivilegedViewer { get; init; } = false;
```

Threaded from `HttpContext.Items[DashboardShellModel.PrivilegedViewerItemsKey]` at the `new DashboardShellModel { ... }` construction site in `StyloBotDashboardMiddleware.ServeDashboardPageAsync`, rather than adding a constructor/DI dependency. Reasoning: FOSS never sets the key (so `IsPrivilegedViewer` is always `false` out of the box, matching the existing "safe FOSS default" pattern), but a commercial host's own middleware — running upstream of this one, after it resolves license/role — can set `HttpContext.Items["sb.dashboard.privileged_viewer"] = true` with **zero FOSS code-path changes**. This is the same shape as other `HttpContext.Items`-based handoffs already used in the codebase (e.g. `EndpointPolicyExtensionContext`'s "extensions MAY stash resolved data in `HttpContext.Items`" pattern) — per-request scoped, not sink-broadcast, no new DI surface.

### Config shape

```json
{
  "Dashboard": {
    "HiddenPaths": ["purchase**", "membership**"]
  }
}
```

FOSS ships no entries in `HiddenPaths` — the array is empty by default so nothing is hidden. The example path names above are illustrative only; FOSS code contains no hard-coded path literals (no "purchase"/"membership" anywhere in `INavVisibilityPolicy.cs` or `DefaultNavVisibilityPolicy`) — those are supplied entirely by whichever host's config sets `Dashboard:HiddenPaths`.

## Out of scope (separate follow-up)

Wiring `Dashboard:HiddenPaths` (or a richer `INavVisibilityPolicy` override) into the actual `stylobot-commercial` site nav/config is **not** part of this change — that repo's nav wiring is owned by a separate agent. This spec only delivers the FOSS seam, the FOSS config-glob default, and the `_SidebarV2.cshtml` wiring point.

## Affected files

- New: `Mostlylucid.BotDetection.UI/Services/INavVisibilityPolicy.cs` (`INavVisibilityPolicy`, `DefaultNavVisibilityPolicy`, `NavVisibilityOptions`).
- `Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs` — `AddOptions<NavVisibilityOptions>().BindConfiguration("Dashboard")` + `TryAddSingleton<INavVisibilityPolicy, DefaultNavVisibilityPolicy>()`, registered next to `ISignaturePolicyActionSlot`.
- `Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs` — `DashboardShellModel.IsPrivilegedViewer` + `PrivilegedViewerItemsKey` constant.
- `Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` — thread `IsPrivilegedViewer` from `HttpContext.Items` at the shell-model construction site.
- `Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SidebarV2.cshtml` — `@inject` + per-row visibility checks (static rows + pack rows + pack sub-rows).
- `Mostlylucid.BotDetection.Test/UI/NavVisibilityPolicyTests.cs` (new) — unit tests for `DefaultNavVisibilityPolicy` (glob hides, no-match shows, empty/missing config shows everything, privileged bypass, case-insensitive match) + a source-assertion test pinning that `_SidebarV2.cshtml` actually injects and calls `NavVisibility.IsVisible(...)` for every row.
- `Mostlylucid.BotDetection.Test/UI/SidebarV2PackNavTests.cs` — existing test renders `_SidebarV2.cshtml` through a hand-built `ServiceCollection`; updated to register the FOSS default so the new `@inject` resolves.

## Testing

- Unit: `DefaultNavVisibilityPolicy` — glob match hides (non-privileged), no match leaves visible, empty config shows everything, missing `HiddenPaths` key shows everything, privileged viewer bypasses a matching pattern, path matching is case-insensitive.
- Source assertion: `_SidebarV2.cshtml` contains `@inject INavVisibilityPolicy NavVisibility` and calls `IsVisible(...)` for every static row id, `pack.Id`, and `$"{pack.Id}/{sub.Id}"`.
- Regression: full `UI`/`Dashboard` test filter — no new failures beyond the pre-existing, unrelated `DashboardLinkIntegrityTests` hardcoded-mount failure on `_TrafficPanels.cshtml`.
