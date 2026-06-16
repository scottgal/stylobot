# Metastable Fingerprint Identity (6.4.7+)

The identity layer that learns *who keeps coming back* - even when their IP, UA, or both rotate. Off by default; flip on with `BotDetection:Identity:Enabled = true`.

This is the user-facing reader. The full design with rationale and contracts lives in [`docs/architecture/fingerprint-match.md`](../../../docs/architecture/fingerprint-match.md).

## What it does

Treats each visitor as a *shape* - a learned vector centroid, a per-fingerprint weight vector, and a cloud of recent observations - instead of a single hash. Two-pass match:

1. **L1 (microseconds, hot path)** - looks up `fingerprint_keys[primary_signature]` and runs a quick weighted-cosine confirm. When IP+UA stays put (the overwhelming majority of human traffic), this is all that runs.
2. **L2 (~ms, slow path)** - falls back to a vector cosine search over fingerprint centroids and recent observations when L1 misses or the confirm score is too low. Bots that rotate IP/UA pay this cost; humans don't.

Result: stable visitors keep the sub-ms fast path. Rotating identities still resolve to a single fingerprint. The shape *is* the identity, not the hash that points at it.

## When it earns its keep

You need this when the legacy `PrimarySignature` (HMAC of IP + UA) keeps fragmenting on:

- **Mobile carriers and CGNAT** - IP changes between requests, UA stable.
- **Headless automation rotating proxies** - IP changes per request, UA stable, behavioural shape stable.
- **CDN-warm content** - same client, multiple POPs, different observed source IPs.
- **Human visitors browsing across networks** - laptop hops from home Wi-Fi to phone tether to office VPN.

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

Both contributors implement `IFoundationContributor` - they run unconditionally under any policy. Policy filters classifiers, never identity.

## What lives in `fingerprints.db`

A separate SQLite file from the main detection DB, so you can rotate or delete the identity store without touching reputation or session data:

| Table | Holds |
|---|---|
| `fingerprints` | One row per identity. Centroid + per-fp weight vector + maturity + cached bot probability + inferred client type + counts + persistent trust state (see below). |
| `fingerprint_keys` | `primary_signature → fingerprint_id` for the L1 point lookup. Re-bound atomically when Pass 2 corrects Pass 1. |
| `fingerprint_observations` | Per-request vectors awaiting absorption. Background ticks fold mature ones into the centroid. |
| `fingerprint_corrections` | One row per Pass-2-corrects-Pass-1 disagreement, with the differentiator that drove the per-fp weight update. Audit + tuning trail. |
| `identity_dimension_weights` | Single row holding the calibrated global per-dim weight vector. |
| `identity_archetypes` | Refined archetype centroids (the YAML-loaded versions live in memory; persisted versions are the calibration-refined ones). |
| `identity_vector_layout` | Versioned vector layout. Mismatch on startup = fail loud, not silent corruption. |

### Persistent trust state (7.5+)

The `fingerprints` table gained four columns in 7.5 to survive process restarts (gap analysis 2026-06-15, Gap #4). Previously trust was an in-memory one-way latch on `SignatureCoordinator` and was lost on restart.

| Column | Type | Description |
|---|---|---|
| `claim_status` | TEXT | `unverified` (default) / `verified` / `spoofed` / `behaviourally-trusted` |
| `verification_method` | TEXT | How the claim was verified: `ip_range`, `fcrdns`, `forward_dns`, `nodeinfo`, or `behavioural-trust`. Null when `unverified`. |
| `verified_at` | TEXT (ISO-8601 UTC) | Timestamp of first successful verification. Null when `unverified`. |
| `trust_observations` | INTEGER | Counter incremented on each request that matches the claimed identity's expected behavioural pattern. Transitions to `behaviourally-trusted` when it crosses the configured threshold (Gap #5). |

The verifier contributors (`VerifiedBotContributor`, `FediverseDomainContributor`) read `claim_status` and `verified_at` at request entry and skip re-verification when the cached result is still within `TrustOptions.TrustCacheTtl`, emitting `verifiedbot.cached` instead.

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

- **Cold-start templates** - a new fingerprint allocates with a 70% observation / 30% archetype-prior centroid blend, plus the archetype's `dimension_mask` added on top of the per-fp weight prior.
- **Cluster labels for calibration** - Fisher ratios bucket fingerprints by `inferred_client_type`, which is the nearest-archetype id.
- **Self-refining anchors** - calibration blends each archetype's centroid toward the mean of its descendant fingerprints (cap-bounded). The YAML-defined `dimension_mask` stays untouched; only the centroid learns.

The system infers client type from observed behaviour. There is no manual tagging.

## Display name composition (7.5+, claim-first)

`FingerprintNameComposer` applies a four-priority naming chain. The key change in 7.5 is that Priority 1 is now the UA-string claim, not the matched archetype. The matcher runs at Priority 6 before `UserAgentContributor` at Priority 10, so the YAML bot-pattern catalog is scanned directly from the raw UA string rather than waiting for the cached `ua.bot_name` signal.

| Priority | Source | When it fires |
|---|---|---|
| 1 | UA-string CLAIM via YAML bot-pattern catalog (`BotPatternLoader.MatchUserAgent`) | UA matches a catalogued bot/tool/fediverse/AI-scraper pattern. Per-instance discriminator (`+URL` hostname) appended for fediverse UAs. `(!)` appended when `VerifiedBotContributor` flagged spoofed or rDNS mismatch. |
| 2 | Matched archetype name + drift variance (`identity.archetype_name`) | Only when archetype kind is `human-browser`. Bot-shaped archetypes fall through to Priority 3 to avoid mislabelling real browsers that drift onto a bot centroid. |
| 3 | UA family + OS characterization (`ua.family` / `user_agent.os`) | Parsed from signals or directly from the raw UA string when signals haven't been written yet. |
| 4 | Raw UA prefix (first 48 chars, truncated with `…`) | Last resort when no structured name is available. Treated as a fallback by hysteresis so a later real Priority 1-3 name can override it. |

Returns null only when the request carries no UA at all.

## Signals downstream consumers should read

| Signal | Type | Meaning |
|---|---|---|
| `identity.fingerprint_id` | `string` | Stable identity for this visitor across rotation. Use this instead of `PrimarySignature` for joins / cache keys / display. |
| `identity.match_score` | `double` | Weighted cosine of the request vector against the matched fingerprint's centroid. |
| `identity.is_new_fingerprint` | `bool` | True when the matcher allocated rather than matched. First-time visitor or rotation gap. |
| `identity.is_correction` | `bool` | True when Pass 2 disagreed with Pass 1. The L1 cache has been re-bound. |
| `identity.rotation_candidate` | `bool` | True when Pass 2 matched in the rotation band (between `LooseThreshold` and `MergeThreshold`). |
| `identity.client_type` | `string` | Nearest-archetype id - inferred from behaviour, not headers. |
| `identity.client_type_confidence` | `double` | Cosine score against the chosen archetype. |
| `identity.cached_bot_probability` | `double` | EWMA-smoothed verdict carried on the fingerprint row. Drift verifier keeps this honest. |

## What this replaces, what it doesn't

- **Replaces** the load-bearing role of `PrimarySignature` for cross-rotation identity. PrimarySignature still exists and is still computed - it's the L1 point lookup key - but downstream consumers should read `identity.fingerprint_id` for anything that needs to survive rotation.
- **Composes with** the verdict cache (see [`fingerprint-verdict-cache.md`](fingerprint-verdict-cache.md)). When Identity is enabled, `SignatureVerdictGate` reads both the per-signature aggregate and the per-fingerprint cached verdict and takes the fresher source. Because the fingerprint source survives IP+UA rotation, a returning visitor whose primary signature has changed inherits their prior verdict (`X-StyloBot-VerdictSource: identity-cache`) instead of paying for a fresh pipeline pass.
- **Doesn't replace** session vectors (see `behavioral-analysis.md`). Sessions remain the per-visit behavioural unit; identity is the cross-visit anchor those sessions hang off.
- **Doesn't replace** anonymous entity resolution. Entity resolution still runs the merge / split / convergence operations; it now has a stronger fingerprint identity to anchor on.

## Slow-path coordinator

The fast path (cache hits, L1 confirm wins) is sub-ms and never touches the coordinator. The coordinator gates only the SLOW work - Pass 2 vector search, correction writes, observation absorption, EWMA updates, on-demand drift verification - so under burst the fast path never blocks; it falls through to cached or default verdicts.

Four layered defences:
- **Keyed serialisation per fingerprint id** - at most one slow-path operation in flight per fp; bursts that match an in-flight call coalesce, and the matcher falls back to the L1 candidate's verdict
- **Priority scheduling** - global priority queue ordered by risk score with aging boost; high-risk fingerprints (already-suspicious, ambiguity-probing, drift-flagged) preempt; operator-triggered work (Re-verify / Run AI from the dashboard) always runs first and bypasses the breaker
- **Admission control** - per-fp queued cap plus global queue depth cap with drop-oldest backpressure; the freshest few requests under sustained burst set the verdict for all
- **Circuit breaker** - when the global queue stays >80% full for 5s, new non-operator work sheds and callers fall back to the fast-path default; auto-resets when depth drops below 30% for 10s; degradation surfaces as `identity.slow_path_shed` and `X-StyloBot-VerdictSource: identity-cache` headers so operators see the state, not silent failure

Worker pool size is configurable (`Coordinator.WorkerCount`, default 4); 1 makes dispatch strictly serial. Per-fp ordering is enforced by an in-flight tracker - a worker dequeueing an item whose fp is already in flight requeues with a small priority penalty.

## Ambiguity-persistence meta-signal (anti-boundary-probing)

An adversary who understands the gate semantics can engineer requests to live in the *ambiguity band* - just novel enough to trip Pass 2 every time, knowing the slow path is always one request behind the fast path's emitted verdict. Cluster-inheritance fallback closes most of that gap, but a probe-the-boundary attacker is engineered to NOT cluster cleanly.

The fingerprint row carries an EWMA-smoothed `ambiguity_persistence` value, bumped on every match outcome:
- L1 confirm success → pushes toward 0 (this fp is settled)
- L1 confirm fail / Pass 2 correction / rotation candidate / new allocation → pushes toward 1 (this fp keeps living in the ambiguity zone)

When the value crosses `Drift.AmbiguityProbingThreshold` (default 0.4), the matcher emits `identity.ambiguity_probing = true` as a positive bot signal. Even when the slow-path coordinator is shedding under adversarial burst, the EWMA bump still happens on every request (a single atomic UPDATE…RETURNING). So the matcher's fast path keeps recording the boundary-probing pattern even when slow-path enrichment is shed - the adversary loses the "always one request behind" advantage.

Surfaces in the Identities dashboard as a colour-banded "Ambig" column (red ≥40%, amber ≥20%, muted otherwise). A fingerprint with high ambiguity_persistence + low correction_count is the classic engineered-to-stay-ambiguous signal.

## Identities dashboard tab (shipped in 6.4.7)

Surfaces every metastable fingerprint with the columns an operator needs to triage drift candidates and boundary-probers:
- Fingerprint id (short) + archetype origin badge
- Inferred client type + confidence
- Total observation count
- **Unabsorbed observation count** - the freshness budget the next absorption tick will fold
- Correction count (Pass-2-corrects-Pass-1 events)
- **Ambig %** - colour-banded boundary-probing score (see above)
- Cached verdict (probability + risk band)
- Last verified, last seen
- Two action buttons:
  - **Re-verify** - `POST /api/identities/{id}/reverify` runs `FingerprintDriftService.VerifyOneAsync` on demand (skips the `CachedScoreTtlSeconds` gate, bumps `cached_score_updated_at`, returns the row HTML for HTMX swap). Routed through the slow-path coordinator with `OperatorReverify` priority - always runs even when the breaker is open.
  - **Run AI** - `POST /api/identities/{id}/run-ai` invokes `IdentityAiOpinionService` which builds a prompt from the fingerprint's metadata, sends it to the registered `ILlmProvider`, parses the JSON reply, and updates `cached_bot_probability` + `cached_risk_band` live. Returns the row HTML; status surfaces as `X-StyloBot-AiOpinion-Status` header (one of `ok`, `identity-disabled`, `not-found`, `no-llm-provider`, `llm-not-ready`, `llm-error`, `parse-error`).

Sorted by unabsorbed-count desc so drift candidates float to the top.

## Operating notes

- `Identity:Enabled` is a flip-on switch - no migration step. The schema is created lazily on first request.
- Vector layout is versioned. Don't edit `IdentityVectorLayout.DefaultV1` in place if you've shipped to anyone - bump the version and migrate. Mismatched layouts fail loud at startup.
- Per-fp weights and global weights are clamped to `[MinWeight, MaxWeight]` purely for numeric stability. The clamp is *not* a "max boost" data cap - high-discriminating dims really do get amplified.
- The drift verifier emits structured warnings; no schema row for drift events yet. Pipe the log to your alerting system if drift rate matters operationally.
- `cached_bot_probability` and `cached_risk_band` are populated by the matcher when a confirmed match is read; the verdict cache reads them via `IdentityVerdictLookup` and composes them with the per-signature aggregate at gate time (fresher source wins).
- The brute-force `IIdentityAnchorIndex` is fine up to a few thousand active fingerprints. Beyond that, install the [sqlite-vec](https://github.com/asg017/sqlite-vec/releases) native extension (`vec0.dylib` / `vec0.so` / `vec0.dll`) on the OS library search path and the store auto-loads it at init; KNN dispatches to the vec0 virtual tables and the brute-force path becomes a per-call fallback (used only when vec0 errors out mid-flight). Override the load path with `BotDetection:Identity:Engine:SqliteVecExtensionPath`.