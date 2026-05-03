# Centroid Freshness

Content sequence detection compares each visitor's request pattern against a stored centroid — the expected sequence of resources a real browser loads for a given page. When a deploy changes that sequence (new JS bundles, renamed CSS, restructured HTML), legitimate browsers diverge from the stale centroid. Without mitigation, this looks identical to bot behavior: sessions that load resources in unexpected order, skip expected assets, or request paths that no longer exist.

Centroid freshness is StyloBot's answer to this problem. Two complementary mechanisms detect deploy events and suppress divergence scoring for 1 hour on affected paths, preventing false positives against real users in the window between a deploy and centroid rebuild.

---

## The Problem

`ContentSequenceContributor` tracks the sequence of resources each browser loads during a document request session. It builds a centroid per endpoint — the weighted average of observed resource load sequences — and measures how much each incoming session diverges from that centroid. High divergence is a strong bot signal: scrapers, crawlers, and headless browsers routinely skip CSS/JS, load assets in wrong order, or omit font loading entirely.

This works well under steady-state conditions. It breaks during deploys.

### What happens during a deploy

A typical deploy on an ASP.NET Core + Tailwind site:

1. `tailwind.min.css` hash changes. Content hash in the filename may change too: `/vendor/css/tailwind.min.css?v=abc` becomes `/vendor/css/tailwind.min.css?v=def`.
2. `app.js` is re-bundled. Import paths shift.
3. The HTML document now references different asset URLs.
4. Browsers with cached copies of the old HTML load new assets against old paths — producing 404s.
5. Browsers seeing the new HTML load new assets — producing a sequence the centroid has never seen.

Both groups diverge heavily from the stored centroid. If 40% or more of sessions on `/blog/post` diverge within a rolling 1-hour window, that is almost certainly a content change, not a bot wave. A bot wave attacking a single endpoint typically targets fewer sessions with more systematic divergence; it also tends to trigger other signals (UA anomalies, IP reputation, header inconsistency). Mass divergence across normal-looking sessions with clean UA and no other signals is the deploy signature.

Two mechanisms detect this:

- **Divergence rate spike** — `EndpointDivergenceTracker` watches the divergence rate per endpoint in a rolling window. When it crosses the threshold, the endpoint centroid is marked stale.
- **Static asset fingerprint change** — `AssetHashMiddleware` + `AssetHashStore` fingerprint static assets using ETag and Last-Modified headers. When the fingerprint changes, the asset path is marked stale in `CentroidSequenceStore`.

---

## How It Works

### Divergence Rate Spike Path

This is the primary path for deploy detection. It activates 1–2 sessions after a deploy, as browsers start loading the new assets.

`EndpointDivergenceTracker` maintains a sliding window per endpoint path. On every document request, `ContentSequenceContributor` calls `RecordSession(path)`. On every continuation request where divergence is detected, it calls `RecordDivergence(path)`.

`IsStale(path)` returns true when both conditions are met:

- `TotalSessions >= 10` — minimum observations to avoid noise on low-traffic paths
- `DivergenceCount / TotalSessions >= 0.40` — 40% of sessions are diverging

When staleness is confirmed:

1. `CentroidSequenceStore.MarkEndpointStale(path)` — records the current timestamp for the path
2. `EndpointDivergenceTracker.Reset(path)` — clears the window to prevent repeated marks within the hour
3. A log entry records the event for diagnostics

From that point, `CentroidSequenceStore.IsEndpointStale(path)` returns true for 1 hour. Downstream detectors that would otherwise emit bot signals based on divergence check this flag and return neutral contributions instead.

### Static Asset Fingerprint Path

`AssetHashMiddleware` runs on the response side, wrapping the full pipeline. For every request to a tracked static extension, it inspects the outgoing response headers and computes a fingerprint:

- **ETag** — used if present; most reliable because it is server-controlled and changes precisely when content changes
- **Last-Modified + Content-Length** — fallback combination for servers without ETags
- **Skip** — if neither header is present, the response is not tracked

The fingerprint is stored in SQLite via `AssetHashStore`. On subsequent requests for the same path, the stored fingerprint is compared to the new one. If it changed:

1. `changed_at` is updated in the SQLite record
2. The path is added to the in-memory `_recentChanges` index with the current timestamp
3. `CentroidSequenceStore.MarkEndpointStale(path)` is called for the **asset path** (e.g., `/vendor/css/tailwind.min.css`)

Note that the asset path and the document path are distinct. The asset fingerprint mechanism marks the asset path stale in `CentroidSequenceStore`, not the parent document path. The divergence-rate mechanism marks the document path stale. See the [Path Separation](#path-separation) section for what this means in practice.

### The Suppression Window

`CentroidSequenceStore` stores stale timestamps in a `ConcurrentDictionary<string, DateTimeOffset>`. The staleness window is fixed at 1 hour. During this window:

- `ContentSequenceContributor` writes `sequence.centroid_stale = true` to the blackboard
- `ResourceWaterfallContributor` reads this signal and returns a neutral contribution instead of bot signals
- `CacheBehaviorContributor` reads this signal and returns a neutral contribution instead of bot signals

After 1 hour, staleness expires automatically. No action is needed — the detectors begin scoring normally, and the centroid rebuilds from fresh post-deploy sessions over the following hours.

To resume scoring immediately after a centroid is rebuilt (e.g., via a manual rebuild operation), call `CentroidSequenceStore.ClearEndpointStale(path)`.

---

## Components

### EndpointDivergenceTracker

Tracks per-path divergence rate in a rolling time window.

**Operations:**

- `RecordSession(path)` — increment total session count for this path; called on every document request
- `RecordDivergence(path)` — increment divergence count; called by `ContentSequenceContributor` when a continuation request diverges from the centroid
- `IsStale(path)` — returns true if TotalSessions >= minimum and divergence rate >= threshold
- `Reset(path)` — clears the window for this path after marking stale

**Thread safety:** `ConcurrentDictionary.AddOrUpdate` is used for all window operations. This atomically checks for expired windows and replaces them, preventing a TOCTOU race where two concurrent requests both see an expired window and both replace it independently. Without this, two simultaneous diverging requests could each think they're initialising a fresh window and record only one divergence between them.

**Window expiry:** each `PathDivergenceWindow` carries a `WindowStart` timestamp. On `RecordSession` and `RecordDivergence`, if `DateTimeOffset.UtcNow - WindowStart > WindowDuration` (default 1 hour), the window is atomically replaced with a fresh one before recording.

**Default parameters:**

| Parameter | Default | Description |
|---|---|---|
| `divergenceThreshold` | 0.40 | Minimum divergence rate to trigger staleness (40%) |
| `minimumSessions` | 10 | Minimum sessions in window before evaluating threshold |
| `windowDuration` | 1 hour | Rolling window duration |

### AssetHashStore

SQLite-backed store for static asset fingerprints.

**Schema:** one row per asset path, storing the fingerprint string, original `first_seen_at`, and `changed_at` timestamp (updated only when the fingerprint actually changes).

**Operations:**

- `RecordHashAsync(path, fingerprint)` — upsert: if no existing record, insert; if fingerprint matches, no-op; if fingerprint differs, update `changed_at` and add to `_recentChanges`
- `IsRecentlyChanged(path)` — returns true if `_recentChanges` contains this path with a timestamp within the last 24 hours

**In-memory index:** `_recentChanges` is a `ConcurrentDictionary<string, DateTimeOffset>`. It is populated on startup by loading all records changed within the last 24 hours from SQLite. During operation, new changes are written to both SQLite and the in-memory index. An hourly background timer (`_evictionTimer`) sweeps `_recentChanges` and removes entries older than 24 hours.

**Write serialisation:** a `SemaphoreSlim(1,1)` serialises all writes to the store. For typical static asset traffic this is not a bottleneck — browsers cache static assets aggressively, so ETag/Last-Modified headers rarely change. The semaphore prevents concurrent upserts from racing on the same path.

**Tracked extensions:** `.css`, `.js`, `.woff`, `.woff2`, `.ttf`, `.eot`, `.otf`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.svg`, `.webp`, `.avif`, `.ico`

### AssetHashMiddleware

Response-side middleware that drives `AssetHashStore`. Registered BEFORE `BotDetectionMiddleware` in the pipeline, so it wraps the full detection pipeline and reads response headers after `_next(context)` returns.

```
Request → AssetHashMiddleware → BotDetectionMiddleware → Application
                ↑                                              |
                └──────────── response headers ───────────────┘
```

The middleware:

1. Checks whether the request path ends with a tracked static extension
2. Calls `_next(context)` to run the full pipeline
3. After the response is written, inspects `context.Response.Headers`
4. Computes a fingerprint (ETag preferred, Last-Modified+Content-Length fallback)
5. If a fingerprint was computed, calls `AssetHashStore.RecordHashAsync(path, fingerprint)`

Exceptions in steps 3–5 are swallowed. The middleware never fails or short-circuits a request. If `AssetHashStore` is not registered in DI, the middleware is not registered.

### CentroidSequenceStore Staleness API

Three methods manage staleness state for endpoint paths:

- `MarkEndpointStale(path)` — records `DateTimeOffset.UtcNow` for the path in an in-memory `ConcurrentDictionary`
- `IsEndpointStale(path)` — returns true if the recorded timestamp is within the last `StalenessWindowHours` (default: 1)
- `ClearEndpointStale(path)` — removes the entry; call after a manual centroid rebuild to immediately resume scoring

Staleness state is **in-memory only** — it is not persisted to SQLite. Loss on restart is intentional and acceptable. The suppression window exists to prevent false positives in the minutes-to-hours after a deploy. On restart, the process resets to a clean slate, and the divergence-rate mechanism will reactivate if the post-restart sessions still diverge from the stored centroid.

---

## Path Separation

The asset fingerprint mechanism and the divergence-rate mechanism operate on different path namespaces. This distinction matters.

`AssetHashMiddleware` records fingerprints and marks staleness for **asset paths**:
```
/vendor/css/tailwind.min.css  →  AssetHashStore  →  CentroidSequenceStore.MarkEndpointStale("/vendor/css/tailwind.min.css")
```

`ContentSequenceContributor` checks for stale centroids using **document paths**:
```
IsEndpointStale("/blog/post")  →  false  (unless divergence-rate path fired for this document path)
```

`IsRecentlyChanged(contentPath)` on a document request checks the asset path — this would only return true if the document path itself (e.g., `/`) was recorded as a static asset by the middleware, which does not happen for HTML responses.

In practice:

- The **asset fingerprint path** marks asset paths stale and writes `asset.content_changed = true` when `IsRecentlyChanged` happens to match the content path. In most deployments, this signal fires when the same path serves both a document and a static file (uncommon).
- The **divergence rate path** marks document paths stale and is the primary mechanism for false-positive suppression. It activates 1–2 sessions into a deploy when browsers start loading the new assets.

The two mechanisms complement each other without requiring direct coupling. A future enhancement could link asset staleness to parent document paths via a configurable path-prefix map (e.g., all assets under `/vendor/` → document path `/`), which would allow the asset fingerprint path to contribute to document-level suppression.

---

## How ContentSequenceContributor Uses These Signals

### On document requests

Before emitting any contributions, `ContentSequenceContributor` checks:

```csharp
bool assetChanged = _assetHashStore.IsRecentlyChanged(contentPath);
bool centroidStale = _centroidStore.IsEndpointStale(contentPath);
```

Both values are written to the blackboard:

| Signal | Type | Condition |
|---|---|---|
| `asset.content_changed` | bool | `AssetHashStore.IsRecentlyChanged(contentPath)` returned true |
| `sequence.centroid_stale` | bool | `CentroidSequenceStore.IsEndpointStale(contentPath)` returned true |

`EndpointDivergenceTracker.RecordSession(contentPath)` is always called, contributing to the rolling window.

### On continuation requests

When divergence is detected on a continuation request:

1. `contentPath` is read from the persisted `SequenceContext` (not the ephemeral blackboard — the document context persists across the session)
2. `EndpointDivergenceTracker.RecordDivergence(contentPath)` is called
3. If `tracker.IsStale(contentPath)`:
   - `CentroidSequenceStore.MarkEndpointStale(contentPath)`
   - `tracker.Reset(contentPath)`
   - A structured log entry is written: path, divergence rate, session count

### Downstream consumption

`ResourceWaterfallContributor` and `CacheBehaviorContributor` both read `sequence.centroid_stale` before emitting bot signals:

```csharp
if (GetSignal<bool>(state, SignalKeys.SequenceCentroidStale))
    return NeutralContribution("centroid_stale");
```

When the signal is present and true, these detectors return a neutral (zero-weight, zero-confidence) contribution. This prevents the 1-hour suppression window from being undermined by waterfall or cache signals that would otherwise fire on the same diverging sessions.

---

## Signals Emitted

| Signal | Written by | Type | Meaning |
|---|---|---|---|
| `sequence.centroid_stale` | `ContentSequenceContributor` | bool | The centroid for this endpoint was recently marked stale; divergence scoring is suppressed |
| `asset.content_changed` | `ContentSequenceContributor` | bool | `AssetHashStore` detected a recent fingerprint change for this content path |

---

## Deploy Event Lifecycle

What actually happens from the moment a deploy completes to the moment detection is fully operational again.

### Step 1 — Deploy completes

New assets are on disk. Old sessions still in-flight may cache old ETag values. New sessions will receive new ETags.

### Step 2 — First static asset request with new ETag

`AssetHashMiddleware` intercepts the response. The ETag does not match the stored fingerprint. `AssetHashStore.RecordHashAsync` records the change and calls `CentroidSequenceStore.MarkEndpointStale("/vendor/css/tailwind.min.css")`. The asset path is now stale.

For sites behind a CDN that caches responses, this step may be delayed until the CDN's cache is purged or TTLs expire. See [CDN Considerations](#cdn-considerations).

### Step 3 — First post-deploy browser sessions load the page

Browsers receive the new HTML with updated asset references. They begin loading new assets in a new sequence. `ContentSequenceContributor` compares these sequences against the stored centroid (built from pre-deploy sessions). Divergence is detected.

`RecordDivergence(documentPath)` increments the counter. With fewer than 10 sessions, `IsStale` returns false — divergence scoring proceeds normally for these first sessions. This is intentional: the minimum session threshold prevents a single errant request from triggering broad suppression.

### Step 4 — 10th diverging session crosses the threshold

Assuming the divergence rate is above 40%, `IsStale` returns true. `ContentSequenceContributor` marks the document path stale, resets the window, and logs the event. All subsequent sessions on this path receive `sequence.centroid_stale = true` for the next hour.

### Step 5 — 1-hour suppression window

During this window, `ResourceWaterfallContributor` and `CacheBehaviorContributor` return neutral contributions for this endpoint. Divergence from the stale centroid does not contribute to bot scores. Other detectors — UA analysis, IP reputation, header inconsistency, TLS fingerprinting — continue operating normally.

Real bots that happen to hit the site during a deploy are still detectable via these other signals. The suppression is narrow: it specifically covers the signals that fire on resource sequence divergence.

### Step 6 — Centroid rebuilds

Over the following hours, `ContentSequenceContributor` ingests post-deploy sessions and updates the centroid. After 1–6 hours of normal traffic (depending on session volume), the centroid converges on the new expected sequence.

### Step 7 — Suppression expires

After 1 hour, `IsEndpointStale` returns false. Normal divergence scoring resumes. The new centroid is now close enough to post-deploy reality that real browser sessions score low divergence again.

---

## CDN Considerations

Sites behind a CDN that aggressively caches static assets introduce a complication: the CDN may serve the old asset with the old ETag from cache even after a deploy. `AssetHashMiddleware` sees the response that ASP.NET Core generates — it does not intercept CDN-cached responses.

**Impact on the asset fingerprint path:**

`AssetHashStore` records fingerprint changes when ASP.NET Core serves the asset. If the CDN caches responses for 24 hours, `AssetHashMiddleware` may not see the new ETag for 24 hours after the deploy. The asset fingerprint path is effectively inactive for CDN-cached paths during this window.

**Impact on the divergence rate path:**

The divergence rate path is unaffected by CDN caching. It fires when browsers diverge from the centroid — which happens regardless of whether the asset was served from CDN or origin. As long as the document HTML reaches browsers with new asset references, browsers will load new sequences, and `ContentSequenceContributor` will detect the divergence.

**Recommended CDN configuration:**

- Use short cache TTLs for HTML documents (30–60 seconds), so browsers receive updated asset references quickly after a deploy.
- Use long cache TTLs for static assets with content-hashed filenames (`/vendor/css/tailwind.abc123.min.css`). Content-hashed assets have unique names per deploy, so the centroid naturally captures them as new paths rather than divergences.
- Purge CDN cache as part of your deploy process. This ensures `AssetHashMiddleware` sees the new ETags promptly.

For sites using content-hashed asset filenames, the divergence rate path is the primary protection mechanism and works correctly regardless of CDN behaviour.

---

## Configuration

`EndpointDivergenceTracker` and `AssetHashStore` are service-level infrastructure components, not detectors. They do not have YAML manifests and do not participate in the detector configuration system. Their parameters are set via constructor arguments at DI registration time.

As of 6.0.1-beta1, there are no `appsettings.json` overrides for these parameters. All tuning requires modifying DI registration.

**Default values:**

| Component | Parameter | Default |
|---|---|---|
| `EndpointDivergenceTracker` | Divergence threshold | 40% |
| `EndpointDivergenceTracker` | Minimum sessions | 10 |
| `EndpointDivergenceTracker` | Window duration | 1 hour |
| `AssetHashStore` | Recent change window | 24 hours |
| `AssetHashStore` | Eviction interval | 1 hour |
| `CentroidSequenceStore` | Staleness window | 1 hour |

**Future:** the commercial dashboard plans per-endpoint divergence threshold overrides. Some endpoints — `/random-article`, `/feed`, paginated indexes — have naturally high sequence variation that does not indicate content change. A lower minimum session count or higher threshold may be appropriate for these paths.

### Registration (automatic via UseStyloBot)

`AssetHashStore` and `AssetHashMiddleware` are registered as part of `AddBotDetection()` and inserted into the pipeline by `UseBotDetection()`:

```csharp
// Recommended: all components registered and ordered correctly
builder.Services.AddStyloBot(dashboard => {
    dashboard.AllowUnauthenticatedAccess = true; // dev only
});
app.UseRouting();
app.UseStyloBot();
```

`AssetHashMiddleware` is inserted before `BotDetectionMiddleware` automatically. Manual registration is not needed.

---

## Notes

**Why 40% and 10 sessions?**

40% is deliberately high to avoid false triggers on endpoints with naturally variable resource sequences (SPAs that lazy-load, endpoints with A/B testing, randomised asset loading). A real bot wave rarely triggers uniform divergence at this rate because bots hit diverse endpoints; a content change hits every browser on the same endpoint.

10 sessions is the minimum to avoid triggering on cold paths. A path with 3 sessions and 2 divergences reads as 66% but is statistically meaningless. Paths under 10 sessions in the window continue scoring normally.

**Why in-memory staleness, not SQLite?**

Staleness state is process-local by design. A multi-instance deployment (load-balanced ASP.NET Core) will have each instance track divergence independently. The 10-session minimum provides natural noise suppression — each instance needs to observe enough traffic to confirm the deploy. In practice, a busy site reaches the threshold on each instance within minutes. A low-traffic site may not reach 10 sessions on every instance, but those instances are also not generating many false positives.

**Why 1 hour suppression?**

One hour is the upper bound on how long it typically takes a human visitor to make a second visit to the same page after a deploy. Sessions from the deploy window are the only ones that diverge; sessions an hour later are loading new content against a centroid that has started updating. One hour also matches common CDN TTLs for non-hashed assets — by the time the CDN expires, the suppression window is over.

**Staleness is not blanket suppression**

`sequence.centroid_stale` suppresses only divergence-based signals from `ResourceWaterfallContributor` and `CacheBehaviorContributor`. It does not suppress:

- UA detection
- IP reputation
- Header analysis
- TLS/TCP fingerprinting
- Behavioral waveform
- Session vector velocity

A bot that happens to arrive during a deploy is still fully detectable by these independent signals.

**SQLite writes are single-writer**

`AssetHashStore` uses a `SemaphoreSlim(1,1)` to serialise writes. If your site has hundreds of unique static asset paths changing simultaneously (a full CDN cache bust on a large asset library), there may be brief queueing on the semaphore. This does not affect request throughput — the write happens after the response is already sent to the client.
