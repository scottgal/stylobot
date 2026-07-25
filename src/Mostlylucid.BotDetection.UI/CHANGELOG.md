# Changelog

All notable changes to the Mostlylucid.BotDetection.UI package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> The **root [`CHANGELOG.md`](../../CHANGELOG.md)** is the authoritative source across the whole solution. Entries below cover the package-visible surface; for detection-engine, gateway, console, and cross-cutting changes, follow the root changelog.

## [8.5.0] - 2026-07-25

Dashboard read-path hardening. Full notes in the root [`CHANGELOG.md`](../../CHANGELOG.md#850---2026-07-25).

### Breaking Changes

- **`POST /admin/reload` removed** (`StyloBotAdminMiddleware`, `StyloBotDashboardOptions`) — FOSS has
  no runtime configuration reload; hot-reload is commercial-only.
- **`DashboardMaterializerOptions` converted from `IOptionsMonitor<T>` to plain `IOptions<T>`** — a
  brief regression (promoted to `IOptionsMonitor` for a since-superseded incident stabiliser, then
  reverted) settled on the FOSS-wide no-runtime-reload rule; a materializer config change now needs a
  process restart.

### Added

- Real navigable endpoint-detail page (`{basePath}/endpoint/{method}/{path}`) rendered inside the full
  dashboard shell, replacing the htmx inline-swap panel.
- MODE/METHOD/STATUS filters on `SbEndpointsList`, plus `EndpointClassifier.ClassifyMode` (path-shape
  taxonomy: Content/Api/Static/Realtime/Other).
- "Show self-probe" audience toggle (`all_incl_internal`) alongside the existing internal-only filter.
- "Your Signature" nav link on `BotDetectionHeader`.
- `INavVisibilityPolicy` — glob-pattern sidebar row hiding under `Dashboard:HiddenPaths`.
- `DashboardMaterializerCoordinator.MarkDirtyAsync(pageKey)` — out-of-band forced re-warm hook for a
  commercial gateway-push relay.
- `DashboardRefreshCadence` / `DashboardRowFreshness` / `DashboardMaterializerAdaptiveController` —
  per-page-key freshness classes (Aggregate/Live), the cross-class MIN-invariant, and an EMA-based
  adaptive interval controller that scales refresh cost under pressure.
- Clusters/TopBots/Sessions/Threats now compose through the content cache (`DashboardPageResult`
  row-extra slices, `DashboardRowRawFetchers`), joining the pre-existing content-cache rows.
- Shared `_DetectionReasons.cshtml` partial for signature-detail (replaces duplicated inline rendering).

### Fixed

- `_Traffic.cshtml` reads through the content cache instead of `IDashboardEventStore` directly — was
  five live REST round-trips per render on the website's remote topology (p99=10s bimodal tail).
- `?window=` selector now threads through Clusters/TopBots/Sessions/Threats/UserAgents (previously
  hardcoded/ignored).
- `SbWidgetTagHelper` warming/empty 3-state contract extended to charts (was list-widgets-only).
- `DetectionDataExtractor` reads the in-process signature via `SignatureAtom.TryGetMultifactor`
  instead of a dead/mismatched key, fixing "Your Detection"/"Your Signature" falling back to a
  transient, non-persisted hash.
- Signature-detail renders through the shared V2 shell (was a stale pre-V2 nav strip via
  `isMainPage: true`); the "You:" pill is fully clickable, not just its trailing text.
- `DashboardMaterializerCoordinator` registered as an injectable singleton (was `AddHostedService`-only).
- `_TrafficPanels.cshtml` and the endpoints partial no longer hardcode the `/dashboard` mount path or
  drop the active filter on follow-up sort/page requests.
- Site page filters (`SbEndpointsListTagHelper`) bind on first paint, not only after an `hx-get`.

### Performance

- Materializer tick loop is due-time gated per envelope instead of unconditional every `Tick10s`.
- Prewarms all four dashboard window tokens (6h/24h/7d/30d); demand-tier ranking reads hotness through
  the sliding-cache atom's own `AccessCount`/`LastAccess` accessor (no duplicate counter); bounded
  concurrency waves with a per-tick wall-clock budget (`MaxTickDurationMs`).
- Cold-miss on a hot cache now fires a fire-and-forget priority re-warm instead of waiting for the next
  scheduled tick.
