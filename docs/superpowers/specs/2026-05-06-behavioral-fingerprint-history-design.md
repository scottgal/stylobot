# Behavioral Fingerprint History Design

## Goal

Show how a visitor's behavioral shape evolves across sessions in the dashboard. A filmstrip or overlay of session radar charts lets analysts spot rotation, drift, and evasion patterns at a glance.

## Architecture

Pure dashboard layer addition. No new database tables, no changes to the detection pipeline. Reads existing session vector data (`Vector[100..109]`) from the SQLite sessions table.

**New files:**
- `src/Mostlylucid.BotDetection.UI/Models/SessionFingerprintsModel.cs` — models
- `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionFingerprints.cshtml` — partial

**Modified files:**
- `StyloBotDashboardMiddleware.cs` — new endpoint handler + model builder
- `_SignatureDetail.cshtml` — HTMX-load the full history component
- `_SessionDetail.cshtml` — HTMX-load a compact context strip

## Data Model

```csharp
public sealed class SessionFingerprintEntry
{
    public long Id { get; init; }
    public DateTime StartedAt { get; init; }
    public bool IsBot { get; init; }
    public string RiskBand { get; init; } = "Low";
    public double Probability { get; init; }
    public int RequestCount { get; init; }
    public float[] StateFreqs { get; init; } = [];  // Vector[100..109], 10 values summing to 1.0
}

public sealed class SessionFingerprintsModel
{
    public string Signature { get; init; } = "";
    public long? CurrentSessionId { get; init; }   // null when viewed from signature page
    public List<SessionFingerprintEntry> Sessions { get; init; } = [];
    public string BasePath { get; init; } = "";
    public string? CspNonce { get; init; }
    public bool CompactMode { get; init; }          // true when embedded in session detail panel
}
```

`StateFreqs` maps to 10 Markov states in order: PageView, ApiCall, StaticAsset, WebSocket, SignalR, ServerSentEvent, FormSubmit, AuthAttempt, NotFound, Search. Values are proportions (sum to 1.0); multiply by 100 for percentage radar axes.

## New Endpoint

**Route:** `GET {basePath}/partials/session-fingerprints?signature={sig}&currentId={id}&compact={true|false}`

Registered in `StyloBotDashboardMiddleware.cs` alongside the other partials routes.

**Handler** (`ServeSessionFingerprintsPartialAsync`):
1. Parse `signature`, optional `currentId` (long), optional `compact` (bool).
2. Query SQLite: `SELECT id, started_at, is_bot, risk_band, avg_bot_probability, request_count, vector FROM sessions WHERE signature = @sig ORDER BY ended_at DESC LIMIT 20`.
3. For each row, deserialize the 129-float vector blob, slice `[100..109]` for `StateFreqs`.
4. Build `SessionFingerprintsModel` and render `_SessionFingerprints.cshtml`.

Sessions with null/zero-length vector blobs are included but rendered with all-zero state freqs (a flat circle, visually distinct).

## Partial: `_SessionFingerprints.cshtml`

### Header

Card header with title ("Behavioral History" full / "Session Context" compact) and, in full mode only, a toggle button pair: **Filmstrip** | **Overlay**. Active mode persists in a `data-mode` attribute on the container div, toggled by a `data-action="toggle-fingerprint-mode"` click (handled via the existing document-level event delegation pattern).

### Filmstrip Mode (default)

Horizontally scrollable row of SVG radar thumbnails. Each thumbnail is pure SVG (no ApexCharts - too heavy for many small instances).

**SVG polygon construction:** Given 10 values for N axes, place axis endpoints at `(cx + r * v * cos(angle), cy + r * v * sin(angle))` where `angle = -π/2 + i * 2π/10`. Connect points to form a filled polygon. Render two rings at 0.5 and 1.0 radius for reference.

**Per-thumbnail:**
- 72×72px SVG
- Fill color: `#f87272` (bot/high risk) or `#36d399` (human/low risk), 30% opacity
- Stroke: same color, 1.5px
- Border: highlighted (2px primary color) when `entry.Id == Model.CurrentSessionId`
- Label below: short relative time (`2h ago`, `3d ago`)
- Wraps in `<button data-action="load-session-detail" data-session-id="...">`

Full mode shows up to 20; compact mode shows up to 10. Overflow scrolls horizontally with `overflow-x: auto`.

Clicking a thumbnail fires the existing session detail load: `hx-get="{basePath}/partials/session-detail?id={sessionId}"` targeting `#session-detail-panel` (same target the sessions list uses).

### Overlay Mode (full mode only)

Single ApexCharts radar with multiple series. Rendered in a nonced `<script>` block.

Series ordering: index 0 = most recent (or CurrentSession if set), higher indices = older. Colors and opacity by age:

| Index | Fill opacity | Stroke width | Color |
|-------|-------------|--------------|-------|
| 0 | 0.20 | 2.0 | #38bdf8 (current) or risk color |
| 1 | 0.15 | 1.5 | #a78bfa |
| 2 | 0.12 | 1.0 | #a78bfa |
| 3 | 0.08 | 0.8 | #a78bfa |
| 4+ | 0.05 | 0.5 | #a78bfa |

Preset buttons **3 | 5 | 10** at the top right control series count. Active preset stored in `data-overlay-count` on the container. Clicking a preset re-renders the chart in place (pure JS, no HTMX round-trip) by slicing the already-loaded session data.

The `breakUniform` guard from `_EndpointDetail.cshtml` is reused here to prevent NaN polygon errors.

X-axis categories: PageView, ApiCall, Asset, WebSocket, SignalR, SSE, Form, Auth, 404, Search (abbreviated for space).

Chart height: 260px full mode, not shown in compact mode.

## Integration: `_SignatureDetail.cshtml`

Add a new card section above the sessions table:

```html
<div class="rounded-xl border overflow-hidden mb-4" ...>
    <div class="flex items-center justify-between px-3 py-1.5 border-b" ...>
        <span class="text-[10px] font-semibold text-base-content/70">BEHAVIORAL HISTORY</span>
    </div>
    <div class="p-3"
         hx-get="@bp/partials/session-fingerprints?signature=@Uri.EscapeDataString(Model.Signature)"
         hx-trigger="load"
         hx-swap="innerHTML">
        <div class="text-xs text-base-content/40 text-center py-4">Loading...</div>
    </div>
</div>
```

## Integration: `_SessionDetail.cshtml`

Add a compact section below the existing Markov radar, before the Paths Visited section:

```html
<div class="mt-4"
     hx-get="@bp/partials/session-fingerprints?signature=@Uri.EscapeDataString(Model.Signature)&currentId=@Model.Id&compact=true"
     hx-trigger="load"
     hx-swap="innerHTML">
    <div class="text-xs text-base-content/40 text-center py-2">Loading context...</div>
</div>
```

Compact mode: filmstrip only (no overlay toggle, no ApexCharts), max 10 sessions, 56×56px thumbnails.

## Event Delegation

Two new actions added to the existing document-level click handler in `Index.cshtml`:

- `toggle-fingerprint-mode`: toggles `data-mode` between `filmstrip` and `overlay` on the nearest `[data-fingerprint-container]`, toggles visibility of `.sb-filmstrip` and `.sb-overlay` divs.
- `load-session-detail`: reads `btn.dataset.sessionId`, fires `htmx.ajax('GET', url, {target: '#session-detail-panel', swap: 'innerHTML'})`.

## Empty State

When `Sessions.Count == 0`: show a message "No finalized sessions yet. Sessions persist after 30 minutes of inactivity." This surfaces the retrogressive boundary behavior to analysts who may be puzzled by empty history.

When `Sessions.Count == 1`: show filmstrip only (single thumbnail), suppress the mode toggle - overlay with one series isn't useful.

## Testing

- Render correctly with 0, 1, 5, 20 sessions
- Current session highlighted in filmstrip
- Overlay breakUniform guard prevents NaN with flat behavioral profiles
- Compact mode hides overlay and toggle
- Clicking thumbnail loads session detail in panel
- Preset buttons 3/5/10 correctly slice series in overlay mode
