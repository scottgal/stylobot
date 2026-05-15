# Metastable Fingerprint Identity (6.4.7+)

The identity layer that learns *who keeps coming back* — even when their IP, UA, or both rotate. Off by default; flip on with `BotDetection:Identity:Enabled = true`.

This is the user-facing reader. The full design with rationale and contracts lives in [`docs/architecture/fingerprint-match.md`](../../../docs/architecture/fingerprint-match.md).

## What it does

Treats each visitor as a *shape* — a learned vector centroid, a per-fingerprint weight vector, and a cloud of recent observations — instead of a single hash. Two-pass match:

1. **L1 (microseconds, hot path)** — looks up `fingerprint_keys[primary_signature]` and runs a quick weighted-cosine confirm. When IP+UA stays put (the overwhelming majority of human traffic), this is all that runs.
2. **L2 (~ms, slow path)** — falls back to a vector cosine search over fingerprint centroids and recent observations when L1 misses or the confirm score is too low. Bots that rotate IP/UA pay this cost; humans don't.

Result: stable visitors keep the sub-ms fast path. Rotating identities still resolve to a single fingerprint. The shape *is* the identity, not the hash that points at it.

## When it earns its keep

You need this when the legacy `PrimarySignature` (HMAC of IP + UA) keeps fragmenting on:

- **Mobile carriers and CGNAT** — IP changes between requests, UA stable.
- **Headless automation rotating proxies** — IP changes per request, UA stable, behavioural shape stable.
- **CDN-warm content** — same client, multiple POPs, different observed source IPs.
- **Human visitors browsing across networks** — laptop hops from home Wi-Fi to phone tether to office VPN.

Without identity enabled, each of those produces a fresh `signatures` row, a fresh reputation cache miss, and a fresh detection cost. With it on, all four resolve back to one `fingerprints` row.

## How a request flows through it

```
Request →
  IdentityVectorContributor (foundation, priority 5)
    composes a vector from upstream signals (TLS, H2, headers, locale, transport)
    + raw header set
    → writes signal: identity.vector
  ↓
  FingerprintMatchContributor (foundation, priority 6)
    Pass 1: fingerprint_keys[primary_signature] lookup + confirm
    Pass 2 (only if L1 misses or fails confirm): IIdentityAnchorIndex.SearchAsync
    → writes signals: identity.fingerprint_id, identity.match_score,
                      identity.is_new_fingerprint, identity.is_correction,
                      identity.client_type, identity.cached_bot_probability
  ↓
  All downstream classifiers see the resolved identity via state.GetSignal<string>(SignalKeys.IdentityFingerprintId)
```

Both contributors implement `IFoundationContributor` — they run unconditionally under any policy. Policy filters classifiers, never identity.

## What lives in `fingerprints.db`

A separate SQLite file from the main detection DB, so you can rotate or delete the identity store without touching reputation or session data:

| Table | Holds |
|---|---|
| `fingerprints` | One row per identity. Centroid + per-fp weight vector + maturity + cached bot probability + inferred client type + counts. |
| `fingerprint_keys` | `primary_signature → fingerprint_id` for the L1 point lookup. Re-bound atomically when Pass 2 corrects Pass 1. |
| `fingerprint_observations` | Per-request vectors awaiting absorption. Background ticks fold mature ones into the centroid. |
| `fingerprint_corrections` | One row per Pass-2-corrects-Pass-1 disagreement, with the differentiator that drove the per-fp weight update. Audit + tuning trail. |
| `identity_dimension_weights` | Single row holding the calibrated global per-dim weight vector. |
| `identity_archetypes` | Refined archetype centroids (the YAML-loaded versions live in memory; persisted versions are the calibration-refined ones). |
| `identity_vector_layout` | Versioned vector layout. Mismatch on startup = fail loud, not silent corruption. |

## The four background services

All hosted; all dormant when `Identity:Enabled = false`.

| Service | Cadence | What it does |
|---|---|---|
| `FingerprintAbsorptionService` | `Drift.DriftCheckIntervalSeconds` (default 5s) | Folds mature observations into centroids via maturity-weighted mean. Applies per-fp stability learning. Recomputes inferred client type; emits drift log when classification flips. |
| `FingerprintDriftService` | `Drift.DriftCheckIntervalSeconds` (default 5s) | Re-verifies L1-confirmed fingerprints whose `cached_score_updated_at` is older than `CachedScoreTtlSeconds` (default 60s). Closes the "L1 still observes" guarantee. |
| `IdentityWeightCalibrationService` | `Calibration.CalibrationIntervalMinutes` (default 30m) | Computes global per-dim weights via Fisher discriminant ratio. Refines archetype centroids by absorbing descendant means (cap-bounded by `ArchetypeRefinementCap`). |
| `IdentityGlobalWeightsCache` | `Weights.GlobalRefreshSeconds` (default 60s) | Pulls the latest calibrated weights into memory. Matcher composes them multiplicatively with per-fp weights at every confirm + Pass 2 call. |

## Configuration

```jsonc
{
  "BotDetection": {
    "Identity": {
      "Enabled": true,
      "Vector": {
        "AbsorptionMaturityThreshold": 5,    // fold obs after the fp sees N more requests
        "AbsorptionAgeDays": 30,             // also fold older obs on active fingerprints
        "ActiveWindowDays": 90,
        "ObservationSamplingRate": 1.0       // record every L1-confirmed obs (lower for very hot fps)
      },
      "Match": {
        "MergeThreshold": 0.92,              // weighted cosine required for confident match
        "LooseThreshold": 0.75,              // below = allocate new instead of force-merge
        "TopK": 10                           // candidates pulled per Pass 2 query
      },
      "Weights": {
        "CorrectionLearningRate": 0.05,
        "StabilityLearningRate": 0.01,
        "MinWeight": 0.1,                    // numeric stability bound, NOT a data cap
        "MaxWeight": 10.0,
        "GlobalRefreshSeconds": 60
      },
      "Drift": {
        "DriftCheckIntervalSeconds": 5,
        "DriftBatchSize": 50,
        "CachedScoreTtlSeconds": 60,
        "DriftWarningThreshold": 0.92        // weighted cosine below this = flagged drift
      },
      "Calibration": {
        "CalibrationIntervalMinutes": 30,
        "ArchetypeRefinementCap": 0.7        // max α per cycle; archetype never moves more than half its identity
      }
    }
  }
}
```

## Archetypes

Nine starter archetypes ship as embedded YAML in `Definitions/IdentityArchetypes/*.yaml` (residential browser, datacenter scraper, search bot, AI scraper, ops tool, residential mobile, kiosk, headless automation, generic CLI). They serve three roles:

- **Cold-start templates** — a new fingerprint allocates with a 70% observation / 30% archetype-prior centroid blend, plus the archetype's `dimension_mask` added on top of the per-fp weight prior.
- **Cluster labels for calibration** — Fisher ratios bucket fingerprints by `inferred_client_type`, which is the nearest-archetype id.
- **Self-refining anchors** — calibration blends each archetype's centroid toward the mean of its descendant fingerprints (cap-bounded). The YAML-defined `dimension_mask` stays untouched; only the centroid learns.

The system infers client type from observed behaviour. There is no manual tagging.

## Signals downstream consumers should read

| Signal | Type | Meaning |
|---|---|---|
| `identity.fingerprint_id` | `string` | Stable identity for this visitor across rotation. Use this instead of `PrimarySignature` for joins / cache keys / display. |
| `identity.match_score` | `double` | Weighted cosine of the request vector against the matched fingerprint's centroid. |
| `identity.is_new_fingerprint` | `bool` | True when the matcher allocated rather than matched. First-time visitor or rotation gap. |
| `identity.is_correction` | `bool` | True when Pass 2 disagreed with Pass 1. The L1 cache has been re-bound. |
| `identity.rotation_candidate` | `bool` | True when Pass 2 matched in the rotation band (between `LooseThreshold` and `MergeThreshold`). |
| `identity.client_type` | `string` | Nearest-archetype id — inferred from behaviour, not headers. |
| `identity.client_type_confidence` | `double` | Cosine score against the chosen archetype. |
| `identity.cached_bot_probability` | `double` | EWMA-smoothed verdict carried on the fingerprint row. Drift verifier keeps this honest. |

## What this replaces, what it doesn't

- **Replaces** the load-bearing role of `PrimarySignature` for cross-rotation identity. PrimarySignature still exists and is still computed — it's the L1 point lookup key — but downstream consumers should read `identity.fingerprint_id` for anything that needs to survive rotation.
- **Composes with** the verdict cache (see [`fingerprint-verdict-cache.md`](fingerprint-verdict-cache.md)). When Identity is enabled, `SignatureVerdictGate` reads both the per-signature aggregate and the per-fingerprint cached verdict and takes the fresher source. Because the fingerprint source survives IP+UA rotation, a returning visitor whose primary signature has changed inherits their prior verdict (`X-StyloBot-VerdictSource: identity-cache`) instead of paying for a fresh pipeline pass.
- **Doesn't replace** session vectors (see `behavioral-analysis.md`). Sessions remain the per-visit behavioural unit; identity is the cross-visit anchor those sessions hang off.
- **Doesn't replace** anonymous entity resolution. Entity resolution still runs the merge / split / convergence operations; it now has a stronger fingerprint identity to anchor on.

## Planned UI (task #38)

The dashboard "Identities" tab will surface per-fingerprint:
- The centroid as an 8-axis behavioural radar
- Observations-in-window count (so an operator can see how much fresh data the next absorption will fold)
- Manual re-verify button (forces an L2 check immediately, skipping the TTL gate)
- Manual AI/LLM-opinion button (runs the slow-path classifier and updates the cached verdict live)

## Operating notes

- `Identity:Enabled` is a flip-on switch — no migration step. The schema is created lazily on first request.
- Vector layout is versioned. Don't edit `IdentityVectorLayout.DefaultV1` in place if you've shipped to anyone — bump the version and migrate. Mismatched layouts fail loud at startup.
- Per-fp weights and global weights are clamped to `[MinWeight, MaxWeight]` purely for numeric stability. The clamp is *not* a "max boost" data cap — high-discriminating dims really do get amplified.
- The drift verifier emits structured warnings; no schema row for drift events yet. Pipe the log to your alerting system if drift rate matters operationally.
- `cached_bot_probability` and `cached_risk_band` are populated by the matcher when a confirmed match is read; the verdict cache reads them via `IdentityVerdictLookup` and composes them with the per-signature aggregate at gate time (fresher source wins).
- The brute-force `IIdentityAnchorIndex` is fine up to a few thousand active fingerprints; replacement with a `sqlite-vec` (vec0) backed implementation for higher scale is task #37.
