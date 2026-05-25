# Behavioral Evolution Panel - Design

**Date:** 2026-05-24
**Repo:** stylobot (FOSS UI) - consumed by stylobot-commercial dashboard and the marketing site.

## Problem

The signature/visitor detail page renders three separate panels for behavioral history:

1. **Behavioral History** - filmstrip of small radar thumbnails for past sessions.
2. **Behavioral Sessions** - tabular list (Started · Duration · Requests · Dominant · Bot % · Risk · Paths).
3. **Behavioral Shape** - single radar of the *currently focused* session with prev/play/next navigation.

The three panels share data and tell adjacent stories, but the relationship between them is invisible until you click around. The filmstrip thumbnails are too small to read shape detail; the radar shows only one session at a time, so evolution across sessions requires watching the play animation rather than seeing it in one frame.

## Solution

Collapse the three panels into one **Behavioral Evolution** card that overlays sessions as ghost polygons on a single radar, ordered by recency, with opacity that falls off with age. The current (or focused) session is drawn solid. The session table moves to the right of the radar as a vertical card stack; clicking a row focuses that session.

The radar uses a **12-axis clock** layout: 8 semantic projection axes plus 4 distilled Markov state shares, grouped into 4 quadrants of 3 axes each so the eye can scan regions rather than individual spokes.

## Axis Layout

12 axes at 30° intervals, starting from 12 o'clock and proceeding clockwise:

| Hour | Axis | Source | Quadrant |
|------|------|--------|----------|
| 12 | Browsing | `radarAxes[0]` (semantic) | Footprint |
| 1 | API Activity | `radarAxes[1]` (semantic) | Footprint |
| 2 | Asset Share | `StateFreqs[Asset]` (markov) | Footprint |
| 3 | Realtime Share | `StateFreqs[WS] + [SSE] + [SignalR]` (markov) | Surface |
| 4 | Form / Search | `StateFreqs[Form] + [Search]` (markov) | Surface |
| 5 | Auth Pressure | `radarAxes[3]` (semantic) | Surface |
| 6 | Burst Speed | `radarAxes[5]` (semantic) | Cadence |
| 7 | Timing | `radarAxes[4]` (semantic) | Cadence |
| 8 | Path Diversity | `radarAxes[7]` (semantic) | Cadence |
| 9 | 404 Share | `StateFreqs[404]` (markov) | Signal |
| 10 | Scan / Probe | `radarAxes[2]` (semantic) | Signal |
| 11 | Fingerprint | `radarAxes[6]` (semantic) | Signal |

The four quadrants render with very subtle background washes (≈ 2% alpha) and small uppercase quadrant labels (`Footprint`, `Surface`, `Cadence`, `Signal`) just inside the outer ring, so each region's story reads at a glance:

- **Footprint (12-2 o'clock)** - what is the visitor fetching?
- **Surface (3-5 o'clock)** - which surface is it pressing?
- **Cadence (6-8 o'clock)** - how is it pacing?
- **Signal (9-11 o'clock)** - what is the detector saying?

All 12 axes are normalised to `[0, 1]`. The markov projections may be 0 for a very-early session that has not accumulated a state distribution yet; those axes simply sit at the origin with no special-case rendering.

## Component Layout

The Behavioral Evolution card is a single rounded panel containing, top to bottom:

1. **Header row** - title (`⬢ Behavioral Evolution`), session-overlay count summary (`5 of 8 sessions overlaid`), Play button.
2. **Body grid** - two columns:
   - **Left (flex 1)** - 420 × 420 SVG/ApexCharts radar.
   - **Right (280 px)** - vertical session card stack (one row per session, most recent first).
3. **Metrics strip** - six labelled metric cells for the focused session: Duration, Requests, Dominant, Bot Prob, Maturity, Entropy.
4. **Axis legend** - four-column reference of axes grouped by quadrant. Always visible (no toggle) so operators can read the radar without memorising hour positions.

A focused session is highlighted in the right-hand list with a 2 px teal left border and a slightly raised background tint. Hovering a row in the list temporarily raises that session's polygon to focused intensity for preview; leaving reverts.

## Data Flow

Two small static helpers in a new `Services/ClockProjection.cs`, kept narrow and unit-testable:

```csharp
public static double[] ProjectMarkovTo4Axes(float[] stateFreqs10);
public static double[] Compose12Axes(double[] semantic8, double[] markov4);
```

`ProjectMarkovTo4Axes` returns `[ Asset, Realtime, Form/Search, 404 ]` from the 10-element state-freq vector. `Compose12Axes` interleaves the existing 8-axis semantic projection with the 4-axis Markov projection into the fixed clock order documented in the Axis Layout table above. The existing `/api/sessions/signature/{id}` endpoint composes once per session and attaches a `clockAxes: number[12]` field on the response, alongside the existing `radarAxes`. The client never recombines axes - the server emits the final 12-axis vector. The `radarAxes` field stays on the wire for any other consumers.

Both source vectors are already available at the API layer:

- 8-axis semantic comes from `VectorRadarProjection.Project(sessionVector)` (finalised + live in-memory paths) or `ProjectDetectionRadarTo8Axes(shape16)` (detection-fallback path).
- 10-axis state freqs come from `sessionVector[100..109]` for finalised + live, and are an empty `float[10]` (zeros) for the detection-fallback path where no session vector exists yet. Markov hours therefore sit at the origin until the session has accumulated a vector - the intended "session in progress" reading.

No schema changes. No new persistence.

When the panel loads it fetches once from `/api/sessions/signature/{id}`, holds the result in a closure, and re-renders on focus/hover/play purely client-side. SignalR `invalidated(keys)` events for this signature trigger a full reload of the partial via HTMX, matching the rest of the detail page's update pattern.

## Interaction Model

| Action | Effect |
|--------|--------|
| Click session row | Row becomes the focus polygon (solid). Previous focus drops to ghost. Metrics strip updates. URL hash updates to `#session=<id>` so the focus is shareable / preserved on reload. |
| Hover session row | Raise that session's stroke/opacity to focus intensity temporarily. Others dim slightly. Leave: revert. |
| Play | Cycles focus through sessions oldest → newest at `PlayIntervalMs`. Button toggles to Pause. Auto-stops on last session or on any manual click. |
| Keyboard `←` / `→` | Move focus one session older / newer (when panel has DOM focus). |
| SignalR invalidate | HTMX-reload the partial. The focused session id (from hash) is preserved if still present. |

## Opacity & Stroke Curves

Opacity is a function of **age**, not list position, so a 3-day-old session is distinctly fainter than a 30-minute-old one even when they are adjacent in the list.

```
ageMinutes  = (now − session.startedAt) in minutes
ghostOpacity = clamp(MaxGhostOpacity * exp(−ageMinutes / HalfLifeMinutes),
                    MinGhostOpacity, MaxGhostOpacity)
fillOpacity  = ghostOpacity
strokeOpacity = clamp(ghostOpacity * 3, MinStrokeOpacity, 1.0)
```

The focused polygon ignores this curve and always renders at `FocusFillOpacity` / `FocusStrokeOpacity` with `CurrentStrokeWidth`. Ghosts use `GhostStrokeWidth`. The oldest visible ghost is dashed (`stroke-dasharray="2,2"`) to read as "historical".

Colour scheme: focused and recent ghosts use teal (`var(--sb-accent)`); ghosts older than `BlueShiftAfterMinutes` shift to slate-blue. This is a perceptual cue that "this session is from a different era", and matches the dashboard's existing accent palette.

## Files

### New

- `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_BehavioralEvolution.cshtml`
  Razor partial. Renders the panel shell + axis legend + metrics-strip skeleton + an inline `<script>` that fetches `/api/sessions/signature/<id>` and renders the radar via ApexCharts plus the session card stack via DOM creation (no `innerHTML` with untrusted content; matches existing pattern at `_SignatureDetail.cshtml:739`).
- `src/Mostlylucid.BotDetection.UI/Models/BehavioralEvolutionOptions.cs`
  Options record. Default values listed in the **Configurable Settings** section. Registered under `BotDetection:Dashboard:BehavioralEvolution`.

### Modified

- `src/Mostlylucid.BotDetection.UI/Services/ClockProjection.cs` (new) - `ProjectMarkovTo4Axes` and `Compose12Axes` static methods.
- `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`
  In `ServeSignatureSessionsApiAsync` (line 1834), attach a `clockAxes` field to every anonymous session entry - finalised path, live-in-memory path, and detection-fallback path. Existing `radarAxes` stays.
- `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml`
  Remove the three existing panels (lines 193-240) and the inline radar script (lines 695-832). Insert a `@Html.Partial("_BehavioralEvolution", model)` invocation.
- `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`
  No DTO type added - the `/api/sessions/signature/{id}` response is built as an anonymous object inline in the middleware. The new `clockAxes` field is attached at construction time alongside the existing `radarAxes`. If a typed DTO ever replaces the anonymous object, that's where `ClockAxes` would live.

### Kept (not touched)

- `Views/Shared/Components/BotDetectionDetails/Default.cshtml` - renders the visitor's current detection state, not session history. Not affected by this work.
- `Views/StyloBot/Dashboard/_SessionDetail.cshtml` - embeds `_SessionFingerprints` in compact-filmstrip mode for its identity column. Continues to do so; the compact path of `_SessionFingerprints.cshtml` is unchanged.

- `_SessionFingerprints.cshtml` - survives because it is still used by the compact identity card on the main dashboard's identity column. Its mode toggle / filmstrip behaviour is unchanged for that caller.

## Configurable Settings

All numeric and behavioural knobs live on `BehavioralEvolutionOptions`. Defaults:

```csharp
public sealed record BehavioralEvolutionOptions
{
    public int    MaxOverlaySessions      { get; init; } = 5;
    public double HalfLifeMinutes         { get; init; } = 240;     // 4 hours
    public double MinGhostOpacity         { get; init; } = 0.03;
    public double MaxGhostOpacity         { get; init; } = 0.65;
    public double MinStrokeOpacity        { get; init; } = 0.20;
    public double FocusFillOpacity        { get; init; } = 0.20;
    public double FocusStrokeOpacity      { get; init; } = 1.00;
    public double CurrentStrokeWidth      { get; init; } = 2.5;
    public double GhostStrokeWidth        { get; init; } = 1.0;
    public int    PlayIntervalMs          { get; init; } = 1500;
    public int    RingCount               { get; init; } = 4;
    public double BlueShiftAfterMinutes   { get; init; } = 720;     // 12 hours
    public bool   ShowQuadrantBackgrounds { get; init; } = true;
    public bool   ShowAxisLegend          { get; init; } = true;
    public bool   ShowMetricsStrip        { get; init; } = true;
}
```

Bound from `BotDetection:Dashboard:BehavioralEvolution` and passed to the view via the existing partial-model plumbing. The values reach the client by inlining them into the panel's `data-*` attributes on the root element, which the inline script reads at boot - no per-request JS payload from `IOptions`.

## Testing

Existing test surfaces extend rather than fork:

- **Unit (`Mostlylucid.BotDetection.Test/UI/Primitives`)** - `ProjectTo12AxisClockTests`: every-axis-zero case, every-axis-one case, missing `StateFreqs` (length-zero array) case, mixed semantic+markov case with hand-computed expected values.
- **Integration (`Mostlylucid.BotDetection.Test/UI`)** - `BehavioralEvolutionPartialTests`: render the partial against a fixture signature with 0, 1, 5, and 20 sessions; assert HTML structure (axis legend present, session row count capped by `MaxOverlaySessions`, focused row class applied to first row by default).
- **API contract** - `SignatureSessionsApiTests`: assert `clockAxes` is a 12-length number array on every returned session, that `radarAxes` is still present, and that markov-empty sessions return 4 zero values for the markov hours rather than omitting them.
- **Browser interaction (chrome-devtools-mcp)** - load the live signature detail page on prod (per the `repro-first / verify-in-browser` memory rules), drive click on a non-focused session row, assert the focused polygon's stroke colour changes and the metrics strip updates. Drive Play and assert frames advance. (DOM-existence and API-fetch checks do not count for the UI gate.)

## Out of Scope (this spec)

Nothing. All decisions are scoped in. Specifically:

- The `_SessionFingerprints.cshtml` filmstrip stays available because it is still consumed by another caller; it is not removed.
- 8 vs 16 vs 10 axis toggles do not appear - the 12-axis clock is the only view.
- No new persisted columns. No schema migration.

If, during implementation, a new question surfaces, raise it for explicit scoping rather than deciding silently.