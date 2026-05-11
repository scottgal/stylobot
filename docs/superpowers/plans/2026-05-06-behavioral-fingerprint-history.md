# Behavioral Fingerprint History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a filmstrip + overlay view of a visitor's historical behavioral radar charts to both the signature detail page and session detail panel.

**Architecture:** New `SessionFingerprintsModel` + `_SessionFingerprints.cshtml` partial. New `/partials/session-fingerprints` endpoint reads `Vector[100..109]` (Markov stationary distribution) from existing SQLite sessions, renders filmstrip as pure SVG thumbnails (no JS library) and overlay as multi-series ApexCharts radar. Integrated into `_SignatureDetail.cshtml` (full mode) and `_SessionDetail.cshtml` (compact mode) via HTMX lazy-load.

**Tech Stack:** ASP.NET Core Razor partials, HTMX, ApexCharts, inline SVG, SQLite (via `ISessionStore.GetSessionsAsync`), `SqliteSessionStore.DeserializeVector`.

---

### Task 1: Add SessionFingerprintsModel to DashboardPartialModels.cs

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs:498` (after `SessionDetailModel`)

- [ ] **Step 1: Add the two new model classes**

Open `src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs`. After the closing `}` of `SessionDetailModel` (line ~498, before `ApprovalFormModel`), insert:

```csharp
/// <summary>
///     Per-session data for the behavioral fingerprint filmstrip/overlay.
///     StateFreqs is Vector[100..109] -the Markov stationary distribution
///     (time spent in each of 10 states, values 0..1 summing to ~1.0).
/// </summary>
public sealed class SessionFingerprintEntry
{
    public long Id { get; init; }
    public DateTime StartedAt { get; init; }
    public bool IsBot { get; init; }
    public string RiskBand { get; init; } = "Low";
    public double Probability { get; init; }
    public int RequestCount { get; init; }
    /// <summary>10 values from Vector[100..109]. May be all-zero if vector blob is absent.</summary>
    public float[] StateFreqs { get; init; } = new float[10];
}

public sealed class SessionFingerprintsModel
{
    public required string Signature { get; init; }
    /// <summary>Id of the session currently being viewed. Null when invoked from signature page.</summary>
    public long? CurrentSessionId { get; init; }
    public List<SessionFingerprintEntry> Sessions { get; init; } = [];
    public required string BasePath { get; init; }
    public string CspNonce { get; init; } = "";
    /// <summary>True when embedded in session detail panel (filmstrip only, 56px thumbnails, max 10 sessions).</summary>
    public bool CompactMode { get; init; }
}
```

- [ ] **Step 2: Build to confirm no errors**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -c Debug --no-restore 2>&1 | tail -5
```

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Models/DashboardPartialModels.cs
git commit -m "feat(dashboard): add SessionFingerprintsModel for behavioral fingerprint history"
```

---

### Task 2: Create _SessionFingerprints.cshtml partial

**Files:**
- Create: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionFingerprints.cshtml`

The partial renders in two modes:
- **Full mode** (`CompactMode = false`): mode toggle button (Filmstrip|Overlay), filmstrip of up to 20 SVG thumbnails (72px), plus a single-series-per-session ApexCharts overlay, plus an inline session detail target div.
- **Compact mode** (`CompactMode = true`): filmstrip only, 56px thumbnails, max 10 sessions, no overlay, no mode toggle.

SVG polygon math: 10 axes at angles `−π/2 + i·2π/10`. A value of 1.0 reaches the outer ring at radius `r`. Ring grid lines drawn at 50% and 100%.

- [ ] **Step 1: Create the partial file**

Create `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionFingerprints.cshtml` with this content:

```razor
@using Mostlylucid.BotDetection.UI.Models
@using System.Text.Json
@model SessionFingerprintsModel
@{
    var bp = Model.BasePath;
    var thumbSize = Model.CompactMode ? 56 : 72;
    var thumbR = Model.CompactMode ? 20 : 26;
    var thumbCx = thumbSize / 2;
    var thumbCy = thumbSize / 2;
    var maxSessions = Model.CompactMode ? 10 : 20;
    var sessions = Model.Sessions.Take(maxSessions).ToList();
    var containerId = "sfp-" + Guid.NewGuid().ToString("N")[..8];

    // Build SVG polygon points string for a 10-axis radar
    string SvgPolygon(float[] freqs, int cx, int cy, int r)
    {
        var pts = new System.Text.StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / 10;
            var v = (double)(freqs.Length > i ? Math.Clamp(freqs[i], 0f, 1f) : 0f);
            var x = cx + r * v * Math.Cos(angle);
            var y = cy + r * v * Math.Sin(angle);
            if (i > 0) pts.Append(' ');
            pts.Append($"{x:F1},{y:F1}");
        }
        return pts.ToString();
    }

    // Decagon ring at a given radius (grid reference lines)
    string SvgRing(int cx, int cy, int r)
    {
        var pts = new System.Text.StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / 10;
            var x = cx + r * Math.Cos(angle);
            var y = cy + r * Math.Sin(angle);
            if (i > 0) pts.Append(' ');
            pts.Append($"{x:F1},{y:F1}");
        }
        return pts.ToString();
    }

    // Relative time label
    string TimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt;
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
        return dt.ToString("MMM d");
    }

    // Color by risk band
    string FillColor(bool isBot, string riskBand) => riskBand switch {
        "VeryHigh" or "High" => "#f87272",
        "Elevated" or "Medium" => "#fbbf24",
        _ => isBot ? "#f87272" : "#36d399"
    };

    var overlaySessions = sessions.Take(10).ToList();
    var overlayData = overlaySessions.Select((s, i) => new {
        label = TimeAgo(s.StartedAt),
        data = s.StateFreqs.Select(v => Math.Round(v * 100, 1)).ToArray(),
        isBot = s.IsBot,
        id = s.Id
    }).ToList();
    var overlayJson = JsonSerializer.Serialize(overlayData);
}

@if (sessions.Count == 0)
{
    <div class="text-xs text-base-content/40 text-center py-4">
        No finalized sessions yet. Sessions persist after 30 minutes of inactivity.
    </div>
}
else
{
    <div id="@containerId" data-fingerprint-container data-mode="filmstrip">

        @* Header with mode toggle (full mode only) *@
        @if (!Model.CompactMode && sessions.Count > 1)
        {
            <div class="flex items-center justify-between mb-2">
                <span class="text-[10px] text-base-content/40">@sessions.Count session@(sessions.Count != 1 ? "s" : "") &middot; most recent first</span>
                <div class="flex rounded-lg overflow-hidden border border-base-300 text-[10px]">
                    <button class="sfp-mode-btn px-2 py-1 bg-base-100 font-medium"
                            data-action="toggle-fingerprint-mode"
                            data-container="@containerId"
                            data-mode="filmstrip">Filmstrip</button>
                    <button class="sfp-mode-btn px-2 py-1 text-base-content/40"
                            data-action="toggle-fingerprint-mode"
                            data-container="@containerId"
                            data-mode="overlay">Overlay</button>
                </div>
            </div>
        }

        @* Filmstrip *@
        <div class="sfp-filmstrip overflow-x-auto pb-1">
            <div class="flex gap-2 w-max">
                @foreach (var entry in sessions)
                {
                    var isCurrent = entry.Id == Model.CurrentSessionId;
                    var fillColor = FillColor(entry.IsBot, entry.RiskBand);
                    var ring50 = SvgRing(thumbCx, thumbCy, thumbR / 2);
                    var ring100 = SvgRing(thumbCx, thumbCy, thumbR);
                    var polygon = SvgPolygon(entry.StateFreqs, thumbCx, thumbCy, thumbR);
                    var borderClass = isCurrent ? "border-2 border-primary" : "border border-base-300";
                    <div class="flex flex-col items-center gap-0.5 shrink-0">
                        @{
                            var probPct = (entry.Probability * 100).ToString("F0");
                            var thumbTitle = $"{(entry.IsBot ? "Bot" : "Human")} · {probPct}% · {entry.RequestCount} req";
                        }
                        <button class="rounded-lg @borderClass hover:border-primary/60 transition-colors bg-base-200 p-0.5"
                                hx-get="@bp/partials/session-detail?id=@entry.Id&sig=@Uri.EscapeDataString(Model.Signature)"
                                hx-target="#sfp-session-detail-@containerId"
                                hx-swap="innerHTML"
                                title="@thumbTitle">
                            <svg width="@thumbSize" height="@thumbSize" viewBox="0 0 @thumbSize @thumbSize" xmlns="http://www.w3.org/2000/svg">
                                <polygon points="@ring100" fill="none" stroke="currentColor" stroke-width="0.5" class="text-base-content/10"/>
                                <polygon points="@ring50" fill="none" stroke="currentColor" stroke-width="0.5" class="text-base-content/10"/>
                                <polygon points="@polygon" fill="@fillColor" fill-opacity="0.3" stroke="@fillColor" stroke-width="1.5"/>
                            </svg>
                        </button>
                        <span class="text-[9px] text-base-content/40">@TimeAgo(entry.StartedAt)</span>
                    </div>
                }
            </div>
        </div>

        @* Overlay (full mode only, hidden by default) *@
        @if (!Model.CompactMode && sessions.Count > 1)
        {
            <div class="sfp-overlay hidden mt-2">
                @* Preset buttons *@
                <div class="flex items-center gap-2 mb-2">
                    <span class="text-[10px] text-base-content/40">Sessions:</span>
                    @foreach (var n in new[] { 3, 5, 10 })
                    {
                        var available = Math.Min(n, overlaySessions.Count);
                        <button class="sfp-overlay-preset btn btn-xs btn-ghost text-[10px] @(n == 5 ? "btn-active" : "")"
                                data-count="@available"
                                data-chart="@containerId-chart">@available</button>
                    }
                </div>
                <div id="@containerId-chart" style="height:240px;" data-overlay-count="5"></div>
            </div>
        }

        @* Session detail target (full mode only) *@
        @if (!Model.CompactMode)
        {
            <div id="sfp-session-detail-@containerId" class="mt-3"></div>
        }
    </div>

    @* Script: toggle, overlay presets, ApexCharts overlay render *@
    @if (!Model.CompactMode && sessions.Count > 1)
    {
        <script type="application/json" id="@containerId-data">@Html.Raw(overlayJson)</script>
        <script nonce="@Model.CspNonce">
        (function() {
            var containerId = '@Html.Raw(containerId)';
            var container = document.getElementById(containerId);
            var filmstrip = container ? container.querySelector('.sfp-filmstrip') : null;
            var overlay   = container ? container.querySelector('.sfp-overlay')   : null;
            var chartEl   = document.getElementById(containerId + '-chart');
            var dataEl    = document.getElementById(containerId + '-data');
            if (!container || !dataEl) return;

            var allSessions = JSON.parse(dataEl.textContent);
            var chart = null;

            var axisLabels = ['Page','API','Asset','WS','SignalR','SSE','Form','Auth','404','Search'];
            var opacities  = [0.20, 0.15, 0.12, 0.08, 0.05, 0.05, 0.05, 0.05, 0.05, 0.05];
            var strokeWidths = [2.0, 1.5, 1.0, 0.8, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5];

            function breakUniform(arr) {
                if (!arr || arr.length === 0) return arr;
                var allSame = arr.every(function(v) { return v === arr[0]; });
                if (allSame) { var out = arr.slice(); out[0] = Math.min(100, out[0] + 0.1); return out; }
                return arr;
            }

            function buildOverlay(count) {
                var subset = allSessions.slice(0, count);
                var isDark = document.documentElement.getAttribute('data-theme') !== 'sb-light';
                var labelColor = isDark ? 'rgba(255,255,255,0.25)' : 'rgba(0,0,0,0.25)';
                var series = subset.map(function(s, i) {
                    return { name: s.label, data: breakUniform(s.data) };
                });
                var colors = subset.map(function(s, i) {
                    return i === 0 ? '#38bdf8' : '#a78bfa';
                });
                var fillOp = subset.map(function(_, i) { return opacities[i] || 0.05; });
                var strokeW = subset.map(function(_, i) { return strokeWidths[i] || 0.5; });

                if (!chart) {
                    chart = new ApexCharts(chartEl, {
                        chart: { type: 'radar', height: 240, toolbar: { show: false }, animations: { enabled: false }, background: 'transparent' },
                        series: series,
                        colors: colors,
                        xaxis: { categories: axisLabels },
                        yaxis: { show: false, min: 0, max: 100 },
                        fill: { opacity: fillOp },
                        stroke: { width: strokeW },
                        markers: { size: 0 },
                        legend: { show: true, position: 'bottom', fontSize: '9px', labels: { colors: labelColor } },
                        plotOptions: { radar: { polygons: { strokeColors: labelColor, connectorColors: labelColor, fill: { colors: ['transparent'] } } } },
                        dataLabels: { enabled: false },
                        theme: { mode: isDark ? 'dark' : 'light' }
                    });
                    chart.render();
                } else {
                    chart.updateOptions({ series: series, colors: colors, fill: { opacity: fillOp }, stroke: { width: strokeW } });
                }
                if (chartEl) chartEl.dataset.overlayCount = count;
            }

            // Mode toggle (delegated from document in Index.cshtml for dashboard,
            // and handled here for standalone signature detail page)
            container.querySelectorAll('[data-action="toggle-fingerprint-mode"]').forEach(function(btn) {
                btn.addEventListener('click', function(e) {
                    e.stopPropagation();
                    var mode = btn.dataset.mode;
                    container.dataset.mode = mode;
                    if (filmstrip) filmstrip.classList.toggle('hidden', mode === 'overlay');
                    if (overlay)   overlay.classList.toggle('hidden', mode === 'filmstrip');
                    container.querySelectorAll('.sfp-mode-btn').forEach(function(b) {
                        var isActive = b.dataset.mode === mode;
                        b.classList.toggle('bg-base-100', isActive);
                        b.classList.toggle('font-medium', isActive);
                        b.classList.toggle('text-base-content/40', !isActive);
                    });
                    if (mode === 'overlay' && !chart) {
                        var count = parseInt(chartEl ? (chartEl.dataset.overlayCount || '5') : '5', 10);
                        buildOverlay(count);
                    }
                });
            });

            // Preset buttons
            container.querySelectorAll('.sfp-overlay-preset').forEach(function(btn) {
                btn.addEventListener('click', function() {
                    var count = parseInt(btn.dataset.count, 10);
                    container.querySelectorAll('.sfp-overlay-preset').forEach(function(b) {
                        b.classList.toggle('btn-active', b === btn);
                    });
                    buildOverlay(count);
                });
            });
        })();
        </script>
    }
}
```

- [ ] **Step 2: Build to confirm no errors**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -c Debug --no-restore 2>&1 | tail -5
```

Expected: `0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionFingerprints.cshtml
git commit -m "feat(dashboard): add _SessionFingerprints partial (filmstrip + overlay)"
```

---

### Task 3: Add endpoint handler and route in StyloBotDashboardMiddleware.cs

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs`

Two changes: (a) add a `case` in the partials route switch, (b) add the handler method.

- [ ] **Step 1: Wire the route**

Find the `case "partials/session-detail":` block (around line 413). Add the new case immediately after `case "partials/signature-sessions":` (around line 417):

```csharp
            case "partials/session-fingerprints":
                await ServeSessionFingerprintsPartialAsync(context);
                break;
```

The full block after your edit should look like:

```csharp
            case "partials/session-detail":
                await ServeSessionDetailPartialAsync(context);
                break;
            case "partials/signature-sessions":
                await ServeSignatureSessionsPartialAsync(context);
                break;
            case "partials/session-fingerprints":
                await ServeSessionFingerprintsPartialAsync(context);
                break;
```

- [ ] **Step 2: Fix ServeSessionDetailPartialAsync to look up by ID**

The existing handler at line ~3197 currently ignores the `id` query param and always fetches the most recent session for the signature. This means clicking a filmstrip thumbnail would always show the wrong session. Fix it by using the ID when provided.

Find the block in `ServeSessionDetailPartialAsync` that reads:
```csharp
        var sessions = await sessionStore.GetSessionsAsync(Uri.UnescapeDataString(sig), 1);
        if (sessions.Count == 0)
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session not found</div>");
            return;
        }

        var s = sessions[0];
```

Replace it with:
```csharp
        var decodedSig = Uri.UnescapeDataString(sig);
        BotDetection.Data.PersistedSession? s = null;

        if (long.TryParse(idStr, out var sessionId) && sessionId > 0)
        {
            // Find the specific session by ID within recent sessions for this signature
            var candidates = await sessionStore.GetSessionsAsync(decodedSig, 50);
            s = candidates.FirstOrDefault(x => x.Id == sessionId);
        }

        if (s == null)
        {
            // Fall back to most recent session
            var fallback = await sessionStore.GetSessionsAsync(decodedSig, 1);
            s = fallback.Count > 0 ? fallback[0] : null;
        }

        if (s == null)
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session not found</div>");
            return;
        }
```

- [ ] **Step 4: Add the handler method**

Add `ServeSessionFingerprintsPartialAsync` immediately after the closing `}` of `ServeSignatureSessionsPartialAsync`. The handler:
1. Reads `signature`, optional `currentId` (long), optional `compact` (bool) from query.
2. Calls `sessionStore.GetSessionsAsync(signature, compact ? 10 : 20)`.
3. For each session, calls `SqliteSessionStore.DeserializeVector(session.Vector)` and takes `[100..109]`.
4. Builds `SessionFingerprintsModel` and renders `_SessionFingerprints.cshtml`.

```csharp
    private async Task ServeSessionFingerprintsPartialAsync(HttpContext context)
    {
        var signature = Uri.UnescapeDataString(context.Request.Query["signature"].FirstOrDefault() ?? "");
        _ = long.TryParse(context.Request.Query["currentId"].FirstOrDefault(), out var currentId);
        _ = bool.TryParse(context.Request.Query["compact"].FirstOrDefault(), out var compact);

        context.Response.ContentType = "text/html";

        if (string.IsNullOrEmpty(signature))
        {
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>No signature specified</div>");
            return;
        }

        var sessionStore = context.RequestServices.GetService<BotDetection.Data.ISessionStore>();
        if (sessionStore == null)
        {
            await context.Response.WriteAsync("<div class='text-xs text-base-content/40 py-4 text-center'>Session store unavailable</div>");
            return;
        }

        var limit = compact ? 10 : 20;
        var raw = await sessionStore.GetSessionsAsync(signature, limit);

        var entries = raw.Select(s =>
        {
            var vector = BotDetection.Data.SqliteSessionStore.DeserializeVector(s.Vector);
            var stateFreqs = new float[10];
            if (vector != null && vector.Length >= 110)
                Array.Copy(vector, 100, stateFreqs, 0, 10);
            return new Models.SessionFingerprintEntry
            {
                Id          = s.Id,
                StartedAt   = s.StartedAt,
                IsBot       = s.IsBot,
                RiskBand    = s.RiskBand,
                Probability = s.AvgBotProbability,
                RequestCount = s.RequestCount,
                StateFreqs  = stateFreqs
            };
        }).ToList();

        var cspNonce = context.Items.TryGetValue("CspNonce", out var nonceObj) && nonceObj is string nonce && nonce.Length > 0
            ? nonce
            : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        var model = new Models.SessionFingerprintsModel
        {
            Signature       = signature,
            CurrentSessionId = currentId == 0 ? null : currentId,
            Sessions        = entries,
            BasePath        = _options.BasePath.TrimEnd('/'),
            CspNonce        = cspNonce,
            CompactMode     = compact
        };

        var html = await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/_SessionFingerprints.cshtml", model, context);
        await context.Response.WriteAsync(html);
    }
```

- [ ] **Step 5: Build to confirm no errors**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -c Debug --no-restore 2>&1 | tail -5
```

Expected: `0 Error(s)`

- [ ] **Step 6: Smoke-test the endpoint manually**

Start the demo app:
```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo
```

Request the partial with any known signature (empty signature returns the placeholder):
```bash
curl -s "http://localhost:5080/stylobot/partials/session-fingerprints?signature=test" | head -5
```

Expected: HTML containing either `No finalized sessions yet` or `<div id="sfp-`.

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "feat(dashboard): add /partials/session-fingerprints endpoint, fix session-detail id lookup"
```

---

### Task 4: Integrate into _SignatureDetail.cshtml

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml:226` (before the Behavioral Sessions card)

Add a new "Behavioral History" card **before** the existing `<!-- Sessions (Markov chain behavioral sessions) -->` card (around line 226). The HTMX load uses `hx-trigger="load"` so it fires immediately when the page renders.

- [ ] **Step 1: Add the Behavioral History card**

In `_SignatureDetail.cshtml`, find the line:
```html
    <!-- Sessions (Markov chain behavioral sessions) - PRIMARY VIEW, loaded via HTMX -->
```
(around line 226). Insert the following block **immediately before** it:

```html
    <!-- Behavioral Fingerprint History - filmstrip + overlay of session radar shapes -->
    <div class="rounded-xl border mb-4" style="border-color: var(--sb-card-border); background: var(--sb-card-bg);">
        <div class="flex items-center gap-1 px-3 py-1.5 border-b" style="border-color: var(--sb-card-divider);">
            <i class="bx bx-history text-sm" style="color: var(--sb-accent);"></i>
            <span class="text-[10px] font-semibold text-base-content/70">BEHAVIORAL HISTORY</span>
        </div>
        <div class="p-3"
             hx-get="@bp/partials/session-fingerprints?signature=@Uri.EscapeDataString(Model.SignatureId)"
             hx-trigger="load"
             hx-swap="innerHTML">
            <div class="text-xs text-base-content/40 text-center py-4">Loading...</div>
        </div>
    </div>

```

- [ ] **Step 2: Build to confirm no errors**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -c Debug --no-restore 2>&1 | tail -5
```

Expected: `0 Error(s)`

- [ ] **Step 3: Verify in browser**

Start the demo:
```bash
dotnet run --project src/Mostlylucid.BotDetection.Demo
```

Visit any signature detail page (e.g., `http://localhost:5080/stylobot/signature/` + a known sig from the Visitors tab). Confirm:
- The "Behavioral History" card appears above "Behavioral Sessions"
- With no finalized sessions: shows "No finalized sessions yet. Sessions persist after 30 minutes of inactivity."
- With sessions (release DB or after 30 min): shows filmstrip thumbnails

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SignatureDetail.cshtml
git commit -m "feat(dashboard): integrate behavioral fingerprint history into signature detail page"
```

---

### Task 5: Integrate into _SessionDetail.cshtml (compact context strip)

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionDetail.cshtml:117` (after the Markov transitions grid, before Paths Visited)

Add a "Session Context" compact strip showing the visitor's other sessions as small filmstrip thumbnails with the current session highlighted. Uses `compact=true` to limit to 10 sessions and 56px thumbnails.

- [ ] **Step 1: Add the compact context section**

In `_SessionDetail.cshtml`, find the `@* Paths visited *@` comment (around line 117). Insert the following block **immediately before** it:

```razor
        @* Session context strip: other sessions for this signature *@
        <div class="mt-3">
            <h5 class="text-xs font-semibold mb-1 text-base-content/70">Session Context</h5>
            <div hx-get="@bp/partials/session-fingerprints?signature=@Uri.EscapeDataString(Model.Signature)&currentId=@Model.Id&compact=true"
                 hx-trigger="load"
                 hx-swap="innerHTML">
                <div class="text-[10px] text-base-content/40 py-2 text-center">Loading...</div>
            </div>
        </div>

```

- [ ] **Step 2: Build to confirm no errors**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -c Debug --no-restore 2>&1 | tail -5
```

Expected: `0 Error(s)`

- [ ] **Step 3: Verify in browser**

In the main dashboard, navigate to the Sessions tab (`http://localhost:5080/stylobot?tab=sessions`). Click any session. In the session detail panel on the right, confirm:
- A "Session Context" section appears below the Markov chain transitions
- With no other sessions for that signature: shows "No finalized sessions yet..."
- With multiple sessions: shows the filmstrip with the current session highlighted (primary border)

- [ ] **Step 4: Full integration check**

Navigate to a signature detail page from the dashboard. Confirm:
- "Behavioral History" card loads with filmstrip
- Clicking a thumbnail loads that session's full detail in the `#sfp-session-detail-*` div below the filmstrip
- The mode toggle (Filmstrip|Overlay) switches views
- In Overlay mode, the ApexCharts multi-series radar renders without NaN errors
- Preset buttons 3/5/10 re-render the overlay with the correct number of series

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/_SessionDetail.cshtml
git commit -m "feat(dashboard): add compact session context strip to session detail panel"
```