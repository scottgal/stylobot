# Fingerprint is the render source — implementation plan

**Goal:** Stop rendering session-vector projections anywhere in the dashboard. Render the fingerprint (centroid + recent observations + drift-from-archetype). One read path: `IFingerprintReader.GetFingerprintAsync(IdentityFingerprintId)` from `HttpContext.Items`. One projection function shared by every surface. "Session effect" = the morph applied to the centroid as sessions are absorbed, not the session vectors themselves.

**Why we got here:** Today's commits introduced `SignatureAggregate.LatestSessionVector` + `ISignatureVectorSink` + `SessionRequestRecorderContributor`, all built on the wrong premise that the session vector is the canonical render source. It isn't. Per the architecture doc (`docs/architecture/fingerprint-match.md`) the fingerprint shape IS the identity — centroid weighted by per-fingerprint dims, recent unabsorbed observations as "current forms", archetype origin as the seed. Session vectors are an INTERNAL signal for the orchestrator's HNSW void detection / velocity analysis. Visible nowhere. The 12-axis clock labels (Browsing / Path Diversity / API Activity / etc.) are session-vector axes; they don't naturally map to the centroid's D=110-180 layout and projecting centroid through them is fabrication.

---

## Anti-goals

- Do **not** project the centroid through the existing 12-axis "Clock" labels. They were designed for the session vector. Forcing the centroid into them invents axes the centroid doesn't encode.
- Do **not** keep any read-path that surfaces a session-vector polygon. Internal usage by orchestrator detectors stays; rendering surfaces are removed.
- Do **not** add a third cache. The fingerprint store is the source; in-memory caching is a separate piece of work for a future spec, not bundled in.
- Do **not** preserve the broken `LatestSessionVector` field as a transitional shim.

---

## Phase A — Revert today's wrong-premise infrastructure

The work to delete cleanly:

**Delete:**
- `src/Mostlylucid.BotDetection/Data/ISignatureVectorSink.cs`
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionRequestRecorderContributor.cs`
- `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/sessionrequestrecorder.detector.yaml`

**Restore to start-of-day:**
- `src/Mostlylucid.BotDetection.UI/Services/SignatureAggregateCache.cs` — remove `LatestSessionVector` field on `SignatureAggregate`, remove `RecordLatestVector` + `EnsureRow` methods, drop the `ISignatureVectorSink` interface implementation. The cache stays; it's still the right shape for what it does (bot aggregate rows for the Top Bots table). Just no session-vector or vector-sink concerns on it.
- `src/Mostlylucid.BotDetection.UI/Models/DashboardTopBotEntry.cs` — remove `LatestSessionVector` property.
- `src/Mostlylucid.BotDetection.UI/Extensions/StyloBotDashboardServiceExtensions.cs` — remove the `ISignatureVectorSink` DI registration.
- `src/Mostlylucid.BotDetection.UI/Middleware/DetectionBroadcastMiddleware.cs` — remove the pre-orchestrator `EnsureRow` call AND the post-`_next` encode-and-push block AND the `MultiFactorSignatureService` constructor parameter we added today.
- `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/SessionVectorContributor.cs` — restore the in-method `RecordRequestAsync` call + boundary signal writes. Revert the `_vectorSink` parameter / field. Revert the `internal static` visibility on `TemplatizePath` and `BuildFingerprintContext` (back to `private static`). The contributor goes back to being the only writer; we accept the priority-30 quorum-exit blind spot for the orchestrator's session-vector internal use, because nothing the user sees depends on it anymore.
- `src/Mostlylucid.BotDetection/Services/SessionAtomizerService.cs` — revert the `ISignatureVectorSink` ctor parameter + the `_vectorSink?.RecordLatestVector` call.
- `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` — remove the `SessionRequestRecorderContributor` DI line.
- `src/Mostlylucid.BotDetection.UI/Middleware/StyloBotDashboardMiddleware.cs` — `/api/sessions/signature/{sig}` handler: remove the cache-driven "current" row insertion. The per-session list reads stay as-is (those are real persisted-session reads). Phase B replaces what was rendered for the "current shape" with a fingerprint read; for now the endpoint just returns persisted sessions without a synthesised current row.
- `src/Mostlylucid.BotDetection.UI/ViewComponents/BotDetectionDetailsViewComponent.cs` — restore three-tier ladder TEMPORARILY (live-store → persisted-store fallback) so the home card renders something while Phase B is in progress. Drop the `RadarShape` tier (still wrong projection). Note inline that this is the transitional state and Phase B replaces it with a fingerprint read.

**Single commit** for the revert. No deployment yet — Phase B continues in the same Maxo cycle.

---

## Phase B — Read path: fingerprint via `IFingerprintReader`

The dashboard surfaces that show a polygon now read the fingerprint, not a session vector.

**B1 — make `IFingerprintReader` resolvable from the dashboard layer.**

Already registered via `services.TryAddSingleton<Identity.IFingerprintReader>(...)` in FOSS `ServiceCollectionExtensions.cs:690`. The dashboard's `BotDetectionDetailsViewComponent` and `StyloBotDashboardMiddleware` take `IFingerprintReader` as a ctor dependency; ASP.NET DI resolves it from the same container.

Verify the reader is registered for both Sqlite and Postgres modes (the commercial Postgres adapter must also bind a Postgres-backed `IFingerprintReader`). If only Sqlite is wired today, that's a separate gap — flag it explicitly during execution; don't silently fall back to Sqlite when Postgres is the configured store.

**B2 — resolve `IdentityFingerprintId` from `HttpContext.Items`.**

`BotDetectionMiddleware:437` already writes `context.Items[SignalKeys.IdentityFingerprintId] = vb.IdentityFingerprintId;`. The view component reads that key. When the value is null (genuinely no archetype-match-yet window — should be near-zero for real visitors once the matcher is wired), the view falls through to the "Calibrating" placeholder. Same behaviour as today; the placeholder is genuine pre-match, not "session vector hasn't been written."

**B3 — `BotDetectionDetailsViewComponent`:**

```csharp
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly IFingerprintReader _fingerprintReader;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        IFingerprintReader fingerprintReader)
    {
        _extractor = extractor;
        _fingerprintReader = fingerprintReader;
    }

    public async Task<IViewComponentResult> InvokeAsync(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();

        if (context?.Items[SignalKeys.IdentityFingerprintId] is string fpId
            && !string.IsNullOrEmpty(fpId))
        {
            var fp = await _fingerprintReader.GetFingerprintAsync(fpId, context.RequestAborted);
            if (fp is not null)
                model = model with { Fingerprint = fp };
        }

        return View(viewName, model);
    }
}
```

Add `public Fingerprint? Fingerprint { get; init; }` to `DetectionDisplayModel` (replaces / lives alongside the now-removed `ClockAxes` field — TBD in B5 whether ClockAxes survives).

**B4 — `/api/sessions/signature/{sig}` "current shape" returned from fingerprint, not synthesised.**

The endpoint already takes a signature in the URL. Resolve fingerprint id from signature via `IFingerprintReader.LookupFingerprintIdAsync(primarySignature)`. Then load the fingerprint. Insert one "current" row in the response array whose payload is the fingerprint projection (per B5). Persisted-session rows in the same response are unchanged (they remain useful as the per-event timeline alongside the headline polygon).

**B5 — projection: centroid + drift → visualisation.**

This is the design call. Two clean options, presented in increasing scope:

**Option 1: bucketed-magnitude polygon (small scope, ships tonight).**

Group the centroid's D slots by the natural semantic buckets from the doc (Network, Locale, Header bag, HTTP-library tells, Transport, Session, Quality). Compute one magnitude per bucket as `sum(centroid[slot] * effective_weight[slot])` over slots in that bucket. Renders as a 7-axis radar.

Strengths: maps directly to what the centroid actually encodes; the doc names these buckets explicitly; per-fingerprint weights are honoured so the polygon shows what's discriminating for THIS fingerprint, not just raw magnitudes.

Weaknesses: 7 axes is sparser than the current 12-axis radar. The bucket-sum collapses detail — a fingerprint with strong TLS-JA4 and weak ALPN reads identical to one with weak TLS-JA4 and strong ALPN at the bucket level.

**Option 2: drift-from-archetype overlay (medium scope, more meaningful).**

Project the centroid + the archetype-origin centroid through the same bucket sum, then render:
- Solid polygon: current centroid (where this fingerprint sits now).
- Dotted ghost: archetype-origin centroid (where it started).
- Difference highlighted: the "session effect" — which buckets have drifted from the archetype seed since first observation.

The archetype id is stored on the fingerprint row (`ArchetypeOrigin`). Loading the archetype's centroid is one lookup against `identity_archetypes`. Adds one more read per render, still cheap.

**Recommendation: Option 2.** That IS the "session effect" the user described — what the absorbed observations have done to the shape. It's the visualisation the architecture deserves. Option 1 ships faster but loses the temporal narrative.

**B6 — view template changes.**

`src/Mostlylucid.BotDetection.UI/Views/Shared/Components/BotDetectionDetails/Default.cshtml` — replace the SVG polygon block:

- Compute the bucket-magnitudes from the fingerprint inline (or move into a `FingerprintRadarProjection` static helper in `Services/`).
- Render the 7-axis radar grid + axis labels + solid polygon (current) + ghost polygon (archetype origin, dotted/lower opacity).
- "Calibrating fingerprint" placeholder when `Model.Fingerprint is null`.

Labels for the 7 axes match the doc's buckets verbatim: `Network`, `Locale`, `Headers`, `Tool`, `Transport`, `Session`, `Quality`.

**B7 — signature detail page headline polygon.**

`_SignatureDetail.cshtml` headline: same fingerprint read, same projection, byte-identical to the home card. The Behavioural Evolution panel below (currently the session-vector ghost overlay via `_BehavioralEvolution.cshtml`) is OUT of scope for Phase B. It either gets repurposed in Phase C (fingerprint snapshots) or removed. For Phase B it stays as-is — possibly visually broken since I removed the cache "current" row earlier — but it's a side panel, not the headline.

---

## Phase C — Fingerprint history snapshots (the morph timeline)

You said: "they should always exist as at least a stub. So we can adjust the number of snapshots to keep."

**Schema:** add `fingerprint_centroid_snapshots` to the SQLite identity DB.

```sql
CREATE TABLE fingerprint_centroid_snapshots (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint_id      TEXT NOT NULL REFERENCES fingerprints,
    snapshot_at         TEXT NOT NULL,
    centroid            BLOB NOT NULL,           -- float[D], copy of the centroid at this point
    centroid_maturity   INTEGER NOT NULL,        -- maturity at snapshot time
    quality             REAL NOT NULL,
    reason              TEXT NOT NULL            -- "archetype_seed" | "absorption_tick" | "drift_threshold"
);
CREATE INDEX ix_fp_centroid_snap ON fingerprint_centroid_snapshots(fingerprint_id, snapshot_at DESC);
```

**Snapshot triggers:**
- At fingerprint allocation — record `reason = "archetype_seed"` with the archetype centroid as-was.
- At each absorption tick — record `reason = "absorption_tick"` with the post-absorption centroid.
- At drift-threshold crossings (optional, future) — `reason = "drift_threshold"`.

**Retention:** `IdentityOptions.HistorySnapshotsPerFingerprint` (default 24, range 0-200, 0 disables). FIFO eviction keeps the most recent N plus the archetype-seed row (which is never evicted — it's the origin).

**Write path:**
- `FingerprintAbsorptionService` emits a snapshot row inside the absorption transaction (same transaction that updates `fingerprints` + `fingerprints_vec`).
- `IFingerprintMatcher.AllocateNewAsync` (the path that seeds a new fingerprint from an archetype) emits the seed snapshot atomically with the `fingerprints` row insert.

**Read path:**
- New `IFingerprintReader.GetCentroidSnapshotsAsync(fingerprintId, limit)` for the dashboard.
- Behavioural Evolution panel on the signature detail page renders the snapshots as a dotted-line timeline of overlay polygons (solid for current; faded ghosts for snapshots, oldest → newest in chronological order). The "session effect" is visible AS THE POLYGON MORPHS over time.

Phase C is its own commit + its own deploy after Phase A+B is verified working in browser.

---

## Phase D — LFU cache layer over `IFingerprintReader` (deferred)

Per the architecture you described: in-memory LFU cache, periodic write-back to persistence, drop/merge low-value writes under pressure. Wraps `IFingerprintReader` as a decorator. Out of scope tonight; opened as its own spec when needed for perf. Today the read-per-render is fine — fingerprints lookup is a single indexed PK read.

---

## Configurable settings (new)

- `IdentityOptions.HistorySnapshotsPerFingerprint` (default 24) — Phase C only.

No new magic numbers in code.

---

## Self-review

1. **Spec coverage:** every render surface that today shows a session-vector polygon (home card, signature-detail headline) reads the fingerprint in B3/B4/B7. Behavioural Evolution panel deferred to C, called out explicitly.
2. **Placeholder scan:** no TBDs except B5's design call (Option 1 vs Option 2) which is the only thing I want your decision on before code lands.
3. **Type consistency:** `IFingerprintReader.GetFingerprintAsync` returns `Fingerprint?` — same type both surfaces consume. New helper `FingerprintRadarProjection.Project(Fingerprint, IdentityArchetype?, IdentityVectorLayout)` returns a `BucketMagnitudes` record with the seven named buckets.
4. **Anti-goals:** no third cache, no session-vector render, no fabricated axis mapping. The 12-axis clock primitives in the dashboard get retired or repurposed alongside this; they were session-vector tools.
5. **Risk:** the centroid-bucket projection (B5) loses sub-bucket detail. Mitigation in Phase C: snapshots let analysts see how individual bucket magnitudes evolve, partially restoring resolution.

---

## What I want you to decide before I touch code

1. **B5: Option 1 vs Option 2.** Pick one.
2. **Phase C scope.** Is "snapshot at every absorption tick + at archetype seed" the right cadence, or do you want a different trigger (e.g. only at drift-threshold crossings to keep snapshot count low)?
3. **The session-vector polygon on the Behavioural Evolution panel.** Remove tonight, or leave for Phase C to repurpose? If we leave it, it shows the old projection until C replaces it — temporary visual inconsistency with the new home-card polygon.

Answers + go and I execute Phase A + B in one Maxo cycle. Phase C in a follow-up cycle once we've eyeballed Phase B on staging.
