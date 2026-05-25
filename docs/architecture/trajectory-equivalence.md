# Trajectory equivalence + soft-merge

**Status:** SPEC / PARKED. No FOSS implementation today; this doc captures the design so future commercial work can pick it up without re-derivation.

## The problem

The metastable identity layer (see [`fingerprint-match.md`](fingerprint-match.md)) handles "is this the same visitor under surface rotation?" by matching weighted-cosine over the identity vector. But two genuine questions sit just above it:

1. **Behavioural convergence under sustained rotation.** Two distinct fingerprints (different IPs, different UAs, different headers - different enough that the matcher allocated two separate fingerprints) may produce *converging behavioural trajectories* over time: same pages in the same order, same per-request timing distribution, same drift directions when challenged. We want to recognise that they're behaviourally the same actor without destroying either identity.

2. **Direction prediction.** A fingerprint's centroid drifts as observations accumulate. The *direction* of that drift is itself a signal: a fingerprint sliding toward bot-shape (lower request entropy, fewer static-asset fetches, tighter timing distribution) before the bot probability has crossed any threshold gives the operator early warning. Currently we score the *current* vector but never the *direction* of the drift.

## Goals

- **Audit-preserving soft-merge**: when two fingerprints' trajectories converge, link them into a `TrajectoryFamily` without merging or destroying either. Both keep their distinct ids, centroids, displayed names, and full surface-dim history (IP/UA/headers per row). The dashboard surfaces them as siblings.
- **Trajectory traces are first-class persistent state**, sized for retention horizon (e.g. last 90 days of compressed deltas per fingerprint).
- **Direction prediction** runs as a separate analyst contributor, emits `trajectory.drift_direction` + `trajectory.bot_likelihood_30d` signals.
- **Never fully merges** - the user's exact phrasing. Two fingerprints stay distinct entities forever; the family is a *view*, not a replacement.

## Core types

```csharp
/// Compressed snapshot of a fingerprint's identity vector at a point in time.
/// Stored at allocation, then every N observations OR on significant drift.
public sealed record TrajectoryPoint
{
    public required string FingerprintId { get; init; }
    public required DateTime ObservedUtc { get; init; }
    public required int ObservationCount { get; init; }    // monotonic counter on the fingerprint
    public required float[] Vector { get; init; }          // current centroid snapshot
    public required double DriftFromPrior { get; init; }   // weighted-L2 vs the previous point
    public string? TopDriftSlot { get; init; }             // which slot moved most
}

/// A family of fingerprints that behaviourally converged. NEVER replaces the
/// member fingerprints - they keep their ids, centroids, distinct names. The
/// family is a *grouping* the dashboard shows; SQL joins preserve the per-
/// fingerprint surface-dim history that's the audit trail.
public sealed record TrajectoryFamily
{
    public required string FamilyId { get; init; }
    public required IReadOnlyList<string> MemberFingerprintIds { get; init; }
    public required DateTime FormedUtc { get; init; }
    public required double MinPairwiseDtwScore { get; init; }   // tightest pair in the family
    public required string ConvergenceReason { get; init; }     // "DTW < 0.15 over last 30 days"
}
```

## Tables

```sql
-- Per-fingerprint trajectory snapshots. Bounded retention (90 days default)
-- + downsample older points (1/hour after 7 days, 1/day after 30 days).
CREATE TABLE IF NOT EXISTS fingerprint_trajectory (
    fingerprint_id     TEXT NOT NULL,
    observed_at        INTEGER NOT NULL,
    observation_count  INTEGER NOT NULL,
    vector             BLOB    NOT NULL,
    drift_from_prior   REAL    NOT NULL,
    top_drift_slot     TEXT,
    PRIMARY KEY (fingerprint_id, observed_at)
);
CREATE INDEX idx_fingerprint_trajectory_fp  ON fingerprint_trajectory(fingerprint_id, observed_at DESC);

-- Family membership. A fingerprint can be in zero or one family at a time.
-- Family membership is reversible (split when divergence widens).
CREATE TABLE IF NOT EXISTS trajectory_family (
    family_id         TEXT PRIMARY KEY,
    formed_at         INTEGER NOT NULL,
    convergence_score REAL    NOT NULL,
    convergence_reason TEXT   NOT NULL
);

CREATE TABLE IF NOT EXISTS trajectory_family_member (
    family_id         TEXT NOT NULL,
    fingerprint_id    TEXT NOT NULL,
    joined_at         INTEGER NOT NULL,
    left_at           INTEGER,                    -- soft-delete; preserves audit
    PRIMARY KEY (family_id, fingerprint_id)
);
CREATE INDEX idx_traj_family_member_fp ON trajectory_family_member(fingerprint_id) WHERE left_at IS NULL;
```

## Similarity metric

**Soft-DTW** (differentiable dynamic time warping) over the vector-delta sequences. Per-fingerprint trajectory is a sequence of `TrajectoryPoint.Vector` snapshots; DTW aligns sequences of different lengths sampled at different cadences, which is the right shape (two visitors observed at different rates can still be compared).

Distance < `TrajectoryConvergenceThreshold` (default 0.15) over a sliding 30-day window → eligible for family merge.

Out of scope today: deeper trajectory metrics (Procrustes alignment, persistent-homology features). DTW is the well-understood baseline.

## Background service

`TrajectoryAnalysisService` (hosted, hourly tick):

1. For each fingerprint observed in the last hour, append a `TrajectoryPoint` to `fingerprint_trajectory` if `drift_from_prior > snapshot_threshold` OR `observation_count - last_snapshot >= snapshot_min_observations`.
2. Downsample older points per retention policy.
3. For fingerprints with >= 20 snapshot history, compute DTW against the k-nearest-vector-cosine candidates from `IIdentityAnchorIndex`. If any pair scores below the convergence threshold, create / extend a `TrajectoryFamily`.
4. For existing families, recompute the min pairwise DTW. If it exceeds `TrajectoryDivergenceThreshold` (default 0.30), mark the diverged member's `left_at` and emit a `trajectory.family_split` event.

## Direction prediction

Per-request contributor `TrajectoryDirectionContributor`:

1. Read this fingerprint's last 5 `TrajectoryPoint` snapshots.
2. Compute the per-dim mean delta vector across that window.
3. Project onto two reference axes derived from labelled archetypes: `bot_axis` (centroid of `bot_*` archetypes minus centroid of `human_*` archetypes) and `human_axis` (opposite).
4. Emit:
   - `trajectory.drift_direction` - `"toward_bot"` / `"toward_human"` / `"stable"`
   - `trajectory.bot_likelihood_30d` - projected position on `bot_axis` (0..1)
   - `trajectory.drift_velocity` - magnitude of the mean delta vector

This gives early warning of fingerprints sliding bot-ward before any threshold crosses.

## Dashboard surface

Per fingerprint detail page:

- **Trajectory mini-chart**: line plot of `drift_from_prior` over time (one point per snapshot). Visually shows whether the fingerprint is stabilising or churning.
- **Direction indicator**: arrow + colour for `trajectory.drift_direction`. Operator sees at-a-glance whether the actor is converging toward human or bot behaviour.
- **Family panel**: when the fingerprint is a `TrajectoryFamily` member, list sibling fingerprint ids + per-sibling surface-dim summary (IP, UA, country). The "this actor used these 5 different IPs and 3 UAs but behaviourally it's one person" view.
- **Audit toggle**: show / hide split-family history (`left_at IS NOT NULL` members).

## FOSS vs commercial split

FOSS could ship:
- The schema + snapshot service + the per-request direction contributor.
- The mini-chart on the dashboard.

Commercial:
- Soft-DTW + family management (cosine pre-filter + DTW compute is non-trivial cost; commercial deployments justify the per-fingerprint analysis budget).
- Per-customer trajectory dashboards, alerting on drift toward bot-shape, replay-by-trajectory investigation flows.

## Why parked

- **FOSS doesn't need it yet**: the current matcher + per-fingerprint EWMA bot probability covers the dominant use case. Trajectory analysis is the next-rung-up feature, not foundational.
- **Storage cost**: per-fingerprint snapshot history is several KB × population × retention. Worth paying when an operator has commercial-tier features that consume it; wasteful as an always-on FOSS feature.
- **DTW compute is non-trivial** at population scale (O(N²) pairwise without smart indexing). The k-nearest-cosine pre-filter helps but the cost profile fits commercial better than FOSS.
- **Soft-merge UX**: the family-grouping dashboard surface requires per-customer thought (which signals to show, when to surface alerts) that's commercial territory.

The bits that ARE worth implementing in FOSS later:

1. The `fingerprint_trajectory` table + snapshot service - pure persistence, modest cost.
2. The `TrajectoryDirectionContributor` - per-request, no DTW, just per-dim delta projection onto a bot/human axis. Useful even without family analysis.

If a third use case appears that wants per-fingerprint trajectory data (entity resolution v2, ML training corpus, replay-by-trajectory investigation), that's the trigger to ship parts 1+2 in FOSS and put 3 (family management) in commercial.

## Open questions for whoever picks this up

1. **Snapshot cadence**: per-N-observations or per-drift-threshold? Probably both, whichever fires first.
2. **Retention horizon**: 90 days seems right; needs validation against typical operator query patterns.
3. **Bot/human axis derivation**: which archetypes seed the reference axes? `IdentityArchetypeRegistry.AllArchetypes` filtered by `Category in {"browser", "bot"}` is the obvious starting point but archetype categorisation needs auditing first.
4. **Family ID format**: opaque ULID is the safe default; commercial may want vendor-prefixed IDs for cross-deployment correlation.
5. **Split semantics**: when a family member diverges, do we soft-delete (`left_at`) or hard-remove? Soft-delete preserves the audit trail the user explicitly called out as critical.