# Session-vector on the aggregate cache — implementation plan

**Goal:** Eliminate the radar polygon divergence between the home-card `<bot-detection-details>` and the dashboard signature-detail page by routing every reader through the existing `SignatureAggregateCache`. One signature → one cached vector → one polygon.

**Architecture:** Add ONE new field (`LatestSessionVector`) to the existing `SignatureAggregate` class. The cache that already exists (`SignatureAggregateCache`, LFU + behaviour-based `EvictLfuBatch`) becomes the single source of truth for the per-signature vector. Existing writers that already produce a vector (`SessionVectorContributor` on orchestrator wave-30, `SessionAtomizerService` on session finalisation) update the field via the same `CreateNew` / `Update` paths they already use for other aggregate fields. Existing persistence (`SessionPersistenceService` writing on `SessionStore.SessionFinalized`) is unchanged — durability stays a side-effect, not a read path. Existing warmup (`SignatureAggregateCacheWarmupService` → `WarmFromDetections`) populates the field at startup from `latest.Vector` so the placeholder window is restart-only.

**Tech stack:** .NET 10, `SignatureAggregateCache` (existing in `Mostlylucid.BotDetection.UI/Services/`), `SessionStore` + `ISessionStore` + `SessionPersistenceService` (existing in `Mostlylucid.BotDetection/Data/` + `Services/`).

---

## Anti-goals (what this plan refuses to do)

- Do **not** add a `SessionVectorCache` or any new cache class. ONE cache, one field.
- Do **not** add a `BoundedChannel<Vector>` writer. The existing finalisation event + `SessionPersistenceService` already covers durability.
- Do **not** introduce a "tiered fallback" (`live → persisted → radar shape`). The cache is the single read path. Miss → placeholder.
- Do **not** touch the orchestrator's detection-path semantics. Vector computation happens where it already happens; the only new work is one method call to push the value to the cache.

---

## File structure

**Modify:**
- `src/Mostlylucid.BotDetection.UI/Services/SignatureAggregateCache.cs` — add field + write API + warmup wiring + thin `EnsureRow` for pre-orchestrator seed.
- `src/Mostlylucid.BotDetection.UI/Middleware/DetectionBroadcastMiddleware.cs` — call `EnsureRow(primarySignature)` BEFORE invoking the inner orchestrator so the aggregate exists by wave-30. `UpdateFromDetection` continues to overlay the full event after orchestrator returns.
- `src/Mostlylucid.BotDetection.UI/Models/DashboardTopBotEntry.cs` — expose field on the read DTO.
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs` — write-through to cache when the contributor encodes a vector.
- `src/Mostlylucid.BotDetection/Services/SessionAtomizerService.cs` — write-through to cache when atomisation produces the finalised vector.
- `src/Mostlylucid.BotDetection.UI/ViewComponents/BotDetectionDetailsViewComponent.cs` — drop the `SessionStore` / `ISessionStore` ladder, read from cache only.
- `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` (the `/api/sessions/signature/{sig}` endpoint, lines ~1880–1970) — read the focused `visible[0]` clockAxes from the cache; `live` row inserted from accumulator stays as a separate ghost overlay but is **not** the focused polygon.

**Ordering correction (added 2026-05-27):** the original plan assumed wave-30 `SessionVectorContributor` would find an aggregate row already in the cache. It doesn't — the cache's only write call site (`UpdateFromDetection` in `DetectionBroadcastMiddleware`) fires AFTER the orchestrator completes. First-visit signatures had their vector dropped by `TryGetValue`. Fix: seed a thin aggregate via `EnsureRow` BEFORE the orchestrator runs. The aggregate now exists by the time any wave needs to write to it, no first-visit drop, no `GetOrAdd` stub inside the sink.

**No files created. No files deleted.** All wiring is via existing types.

---

## Cross-project wiring

`SessionVectorContributor` and `SessionAtomizerService` live in `Mostlylucid.BotDetection` (the FOSS detection layer). `SignatureAggregateCache` lives in `Mostlylucid.BotDetection.UI` (the dashboard layer). The detection layer must not take a hard dependency on the dashboard cache type.

**Solution:** define an interface `ISignatureVectorSink` in `Mostlylucid.BotDetection/Data/` with a single method:

```csharp
void RecordLatestVector(string primarySignature, float[] vector);
```

`SignatureAggregateCache` implements it. DI registers the same instance as both `SignatureAggregateCache` and `ISignatureVectorSink`. The two writers take `ISignatureVectorSink?` (nullable; gateway-mode without the dashboard package is a legitimate null). No coupling, no new compilation unit, no new background service.

---

## Tasks

### Task 1: Field + interface + write API

**Files:**
- Create: `src/Mostlylucid.BotDetection/Data/ISignatureVectorSink.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Services/SignatureAggregateCache.cs`
- Modify: `src/Mostlylucid.BotDetection.UI/Models/DashboardTopBotEntry.cs`

**Step 1.1 — interface:**

```csharp
// src/Mostlylucid.BotDetection/Data/ISignatureVectorSink.cs
namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Write-side abstraction for the dashboard's per-signature cached
///     session vector. Detection-layer code (SessionVectorContributor,
///     SessionAtomizerService) calls <see cref="RecordLatestVector"/> when
///     it computes a fresh vector; the dashboard layer's
///     SignatureAggregateCache implements this and routes the value onto
///     SignatureAggregate.LatestSessionVector. Null in gateway-mode
///     deployments without the dashboard package.
/// </summary>
public interface ISignatureVectorSink
{
    void RecordLatestVector(string primarySignature, float[] vector);
}
```

**Step 1.2 — `SignatureAggregate` field (in `SignatureAggregateCache.cs`, the inner `public sealed class SignatureAggregate`):**

Add:
```csharp
/// <summary>
///     Latest 118+ dim session vector for this signature, as produced by
///     <see cref="Mostlylucid.BotDetection.Orchestration.ContributingDetectors.SessionVectorContributor"/>
///     on wave-30 OR by <see cref="Mostlylucid.BotDetection.Services.SessionAtomizerService"/> on
///     finalisation. Single source of truth for every dashboard surface
///     that projects a behavioural radar polygon; read via
///     <see cref="SignatureAggregateCache.TryGet"/>. Null until first
///     write or after a cold restart before the warmup service has
///     hydrated.
/// </summary>
public float[]? LatestSessionVector;
```

**Step 1.3 — implement `ISignatureVectorSink` on `SignatureAggregateCache`:**

```csharp
public sealed class SignatureAggregateCache : ISignatureVectorSink
{
    // ... existing fields ...

    public void RecordLatestVector(string primarySignature, float[] vector)
    {
        if (string.IsNullOrEmpty(primarySignature) || vector is not { Length: >= 118 }) return;
        if (!_entries.TryGetValue(primarySignature, out var agg)) return;
        lock (agg.SyncRoot)
        {
            agg.LatestSessionVector = vector;
        }
    }
}
```

Rationale for `TryGetValue` (not `GetOrAdd`): we only update if the cache already holds the signature. The aggregate is created by the existing live-detection write path (`CreateNew`/`Update` from `RecordDetection`). Vectors arrive AFTER that path runs. Adding an entry just for a vector with no detection context creates an orphan row that the dashboard can't display. If we miss because the aggregate hasn't been seeded yet, the next request for that signature creates it via the normal path and the next vector lands cleanly.

**Step 1.4 — propagate the field through `CreateNew`, `WarmFromDetections`, `ToEntry`:**

- `CreateNew` already takes a `DashboardDetectionEvent`. The event has no `LatestSessionVector` field. Leave `agg.LatestSessionVector = null` here.
- `WarmFromDetections` (startup hydration): when `latest.Vector` is non-empty, decode via `SqliteSessionStore.DeserializeVector` and set `LatestSessionVector = vector`. This is the warm-start path that prevents the calibration placeholder lasting beyond the restart window. Wrap in `try { ... } catch { /* ignore malformed */ }` matching the pattern of the existing JSON deserialisation calls in `ToEntry`.
- `ToEntry`: add `LatestSessionVector = agg.LatestSessionVector` so downstream readers can pull from `DashboardTopBotEntry`.

**Step 1.5 — `DashboardTopBotEntry` field:**

```csharp
/// <summary>
///     Latest 118+ dim session vector for this signature (same field
///     that lives on <c>SignatureAggregate</c>). Projected to a 12-axis
///     clock polygon by <c>ClockAxesResolver.FromSessionVector</c> on
///     both the home card and the signature detail page so the polygon
///     reads identically across surfaces. Null until the first vector
///     is written or after a cold restart before warmup hydrates.
/// </summary>
public float[]? LatestSessionVector { get; init; }
```

**Step 1.6 — DI registration:**

In `StyloBotDashboardServiceExtensions.AddStyloBotDashboard(...)`, find the existing `services.AddSingleton<SignatureAggregateCache>(...)` registration and add:

```csharp
services.AddSingleton<ISignatureVectorSink>(sp => sp.GetRequiredService<SignatureAggregateCache>());
```

so the two writers can resolve the same instance as a sink.

**Step 1.7 — `EnsureRow` thin-seed API on `SignatureAggregateCache`:**

```csharp
/// <summary>
///     Idempotent thin-row seed. Creates an aggregate with only the
///     <paramref name="primarySignature"/> populated when no row exists.
///     Called from <see cref="DetectionBroadcastMiddleware"/> BEFORE the
///     orchestrator runs so wave-30 contributors (e.g.
///     SessionVectorContributor) writing through
///     <see cref="ISignatureVectorSink"/> always find a row to update.
///     First-impression latency over first-impression accuracy: the
///     visitor's home card renders a real polygon on the first page
///     load instead of "calibrating" until visit two.
/// </summary>
public void EnsureRow(string primarySignature)
{
    if (string.IsNullOrEmpty(primarySignature)) return;
    _entries.TryAdd(primarySignature, new SignatureAggregate
    {
        HitCount = 0,                  // detection write later increments
        RiskBand = null,               // overlaid by Update
        BotProbability = 0,
        Confidence = 0,
        FirstSeen = DateTime.UtcNow,
        LastSeen  = DateTime.UtcNow,
        IsBot = false,
    });
}
```

`TryAdd` is a no-op when the row already exists, so the call is cheap on every request.

**Step 1.8 — wire `EnsureRow` into `DetectionBroadcastMiddleware`:**

The middleware already resolves the primary signature before invoking the orchestrator (the signature is needed to deduplicate detections per-request via `HttpContext.Items`). Right after the signature is computed and BEFORE the call to the orchestrator, add:

```csharp
_signatureCache.EnsureRow(primarySignature);
```

Inject `SignatureAggregateCache` into the middleware's constructor alongside the existing dependencies.

**Step 1.9 — commit:**

```bash
git add src/Mostlylucid.BotDetection/Data/ISignatureVectorSink.cs \
        src/Mostlylucid.BotDetection.UI/Services/SignatureAggregateCache.cs \
        src/Mostlylucid.BotDetection.UI/Models/DashboardTopBotEntry.cs \
        src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs
git commit -m "feat(cache): add LatestSessionVector field to SignatureAggregate + sink"
```

---

### Task 2: Wire the two writers to the sink

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs`
- Modify: `src/Mostlylucid.BotDetection/Services/SessionAtomizerService.cs`

**Step 2.1 — `SessionVectorContributor`:**

The contributor already encodes a vector on every wave-30 invocation (line ~177 and ~201). It calls `SessionVectorizer.Encode(currentSession, fpContext)` and assigns to `currentVector`. Right after that variable is set, push to the sink:

```csharp
_vectorSink?.RecordLatestVector(signature, currentVector);
```

Add `ISignatureVectorSink? vectorSink = null` to the constructor and store as `_vectorSink`. Two write sites because the contributor encodes a vector in two branches (one for the search-projection path, one without). Both branches push the same way.

**Step 2.2 — `SessionAtomizerService`:**

The atomizer service builds a finalised `vector` inside the per-`sigGroup` loop (line ~103, where `SerializeVector(vector)` already runs). Immediately after `var vector = ...` is computed, push:

```csharp
_vectorSink?.RecordLatestVector(sigGroup.Key, vector);
```

Add `ISignatureVectorSink? vectorSink = null` to the constructor and store.

**Step 2.3 — DI:** both classes are already registered. Both will pick up the optional sink dependency automatically once Task 1's registration lands.

**Step 2.4 — commit:**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs \
        src/Mostlylucid.BotDetection/Services/SessionAtomizerService.cs
git commit -m "feat(detection): write-through session vectors to aggregate cache"
```

---

### Task 3: Read swap on the home card

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/ViewComponents/BotDetectionDetailsViewComponent.cs`

**Step 3.1 — collapse the resolver to one call:**

Replace the entire `ResolveClockAxesAsync` body with a synchronous cache read. Drop `SessionStore` and `ISessionStore` from the constructor. The view component becomes:

```csharp
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly SignatureAggregateCache _cache;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        SignatureAggregateCache cache)
    {
        _extractor = extractor;
        _cache = cache;
    }

    public IViewComponentResult Invoke(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();

        var primarySig = model.Signatures?.PrimarySignature;
        if (!string.IsNullOrEmpty(primarySig) &&
            _cache.TryGet(primarySig, out var agg) &&
            agg.LatestSessionVector is { Length: >= 118 } vec)
        {
            var axes = ClockAxesResolver.FromSessionVector(vec);
            if (axes is not null) model = model with { ClockAxes = axes };
        }

        return View(viewName, model);
    }
}
```

Note: signature changes from `Task<IViewComponentResult> InvokeAsync` to `IViewComponentResult Invoke` because there is no longer any async work. Razor handles both signatures.

**Step 3.2 — view template stays as it is.** The "Calibrating fingerprint" placeholder already in `Default.cshtml` (from commit `4da8fd3`) handles the `ClockAxes is null` case. No change.

**Step 3.3 — commit:**

```bash
git add src/Mostlylucid.BotDetection.UI/ViewComponents/BotDetectionDetailsViewComponent.cs
git commit -m "fix(your-detection): read polygon from aggregate cache, drop fallback ladder"
```

---

### Task 4: Read swap on the dashboard signature-detail API

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` (the `/api/sessions/signature/{sig}` handler — around lines 1880–1970)

**Step 4.1 — the focused polygon (visible[0]):**

The endpoint currently returns sessions newest-first, with a synthetic `live` row inserted at index 0 when the in-memory accumulator is warm (line ~1949 `result.Insert(0, ...)`). The chart's `focused = visible[0]` then defaults to the live row.

Replace the live-row insertion with a single "current" row sourced from the cache:

- Before building the per-session list, do `_signatureCache.TryGet(decodedSignature, out var agg)`.
- If `agg?.LatestSessionVector is { Length: >= 118 } vec` then insert a synthetic row at index 0 with:
  - `Id = "current"`, `live = true` (preserves the chart's existing focused-by-default behaviour)
  - `clockAxes = ClockAxesResolver.FromSessionVector(vec)`
  - All other fields set to the same defaults the old `live` insertion used (`avgBotProbability = 0.0`, `transitionCounts = null`, etc.).
- Drop the existing block that reads `BotDetection.Analysis.SessionStore` and re-encodes a vector inline.

**Step 4.2 — the per-session ghost overlay (sessions list):**

The newest-first persisted session list (lines ~1881–1931) stays as-is — those are HISTORICAL sessions for the ghost overlay, each correctly projected via `ClockAxesResolver.FromSessionVector` on its own persisted vector. No change.

**Step 4.3 — detection-fallback block (lines ~1972 onwards):**

This synthesises a session from detection events when both the live accumulator and persisted store are empty. It's redundant once the cache is the source — if the cache has `LatestSessionVector`, the current row is built from it; if not, the user sees the "no sessions yet" empty state honestly. Delete the block.

**Step 4.4 — commit:**

```bash
git add src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs
git commit -m "fix(api): signature-sessions current row reads from aggregate cache"
```

---

### Task 5: Verify

**Step 5.1 — build:**

```bash
dotnet build src/Mostlylucid.BotDetection.UI/Mostlylucid.BotDetection.UI.csproj -c Debug
dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj -c Debug
```

Expected: 0 errors.

**Step 5.2 — interaction-test on staging (HARD GATE):**

Per the user's UI rule: real chrome-devtools / playwright drive, not DOM-existence check.

1. Navigate to `https://staging.stylobot.net/` — capture home-card polygon points.
2. Click through to `/dashboard/signature/<the visitor's primary sig>` — wait for the ApexCharts radar to render the focused polygon.
3. Read the SVG points of the focused polygon AND the `clockAxes` from the API response.
4. Project the home card's `polygon@points` back to magnitudes via the inverse of `x = 50 + cos(angle) * 35 * m`, compare against the API's `clockAxes`.
5. Magnitudes match within ε=0.001 → pass. Any axis differs → fail.

**Step 5.3 — restart resilience:**

`docker restart stylobot-test-website`, wait 30s for warmup, reload `/` — the home card polygon must reappear with magnitudes close to (but not necessarily identical to) the pre-restart values, projected from the latest persisted session that `SignatureAggregateCacheWarmupService` hydrated. "Calibrating" placeholder may flash briefly during the warmup gap.

---

## Configurable settings (new)

None. Every threshold this plan touches (vector minimum dim = 118, warmup window = 24h, cache MaxEntries) is already on existing options classes. No magic numbers introduced.

---

## Self-review checklist

1. **Spec coverage:** every surface that read a session vector before this plan (home card, signature-detail API focused polygon) now reads from `SignatureAggregateCache`. The persisted ghost-overlay sessions list stays unchanged because it is the historical ghost overlay, not the "current shape" surface — the user's "single source" rule applies to the canonical headline polygon, which is now exactly one source.
2. **Placeholder scan:** no TBDs, no "implement later". Every step shows the exact code.
3. **Type consistency:** `LatestSessionVector` is `float[]?` everywhere — on `SignatureAggregate` (mutable field), on `DashboardTopBotEntry` (init-only), and as the `RecordLatestVector` parameter. `ClockAxesResolver.FromSessionVector` accepts `float[]?` (returns null on null/short input), so no nullable-handling drift.
4. **Anti-goals honoured:** no new cache class, no new channel, no new background service, no new fallback ladder.

---

## Execution

Per user rules: commit on main, no auto-branch. Five small commits land sequentially: field+sink → contributor write → atomiser write → home-card read → API read. Each commit builds green on its own. Maxo build kicks once at the end, single deploy to staging.
