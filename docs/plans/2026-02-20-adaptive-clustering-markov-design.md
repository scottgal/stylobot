# Adaptive Clustering, Markov Path Learning & Signal Diagnosticity

## Problem

Clustering never produces results due to overly aggressive thresholds and hardcoded similarity weights. The behavioral detector uses fixed rules instead of learning from population patterns. Signal weights are static, ignoring that feature diagnosticity varies by traffic mix and time of day.

## Architecture

Three interconnected layers in a feedback cycle:

```
Features → Adaptive Weights → Better Clusters → Better Cohorts → Better Drift Signals → Better Features
```

### Layer 1: Enriched Feature Extraction

Expand `SignatureGeoContext` from 3 to 8 fields by extracting from the signals dict already passed to `RecordRequestAsync`:

- `Latitude`, `Longitude` (from `geo.latitude`, `geo.longitude`)
- `ContinentCode` (from `geo.continent_code`)
- `RegionCode` (from `geo.region_code`)
- `IsVpn` (from `geo.is_vpn`)

Replace binary country match with hierarchical geo score:
- Same city → 1.0
- Same region → 0.85
- Same country → 0.7
- Haversine < 500km → 0.6
- Same continent → 0.4
- Different continent → 0.0

Add 6 Markov drift signals to FeatureVector (from Layer 3).

### Layer 2: Adaptive Diagnosticity Engine

`AdaptiveSimilarityWeighter` replaces hardcoded weights in `ComputeSimilarity()`.

Each clustering cycle (30s):
1. Compute coefficient of variation for each continuous feature
2. Compute Shannon entropy for each categorical feature
3. Normalize to sum to 1.0
4. Apply floor (0.02) and ceiling (0.20)

Weight shifts are themselves diagnostic — a sudden spike in ASN weight means one ASN became over-represented in bot traffic.

### Layer 3: Population Markov Model

Three levels of transition matrix with exponential decay:

**Per-signature chain** (half-life 1h): `Dictionary<string, Dictionary<string, DecayingCounter>>`, top-K=20 edges per source node. 1000 active signatures × 400 entries = ~20MB.

**Cohort baselines** (half-life 6h): Initial cohorts: `{datacenter, residential} × {new, returning}`. Leiden clusters become cohorts once stable. ~24 models × 2000 entries = ~2.5MB.

**Global baseline** (half-life 24h): Single model across all human-classified traffic. Cold-start fallback. ~4K entries = ~200KB.

**Path normalization** (new `PathNormalizer` class):
- `/product/12345` → `/product/{id}`
- `/api/v2/users/abc-def` → `/api/v{v}/users/{guid}`
- Drop query strings
- Group static assets → `{static}`
- Bucket by type: `{search}`, `{detail}`, `{list}`, `{auth}`, `{api}`

**Six drift signals per signature:**
1. Self-drift — JS divergence: signature's recent chain vs historical
2. Human-drift — JS divergence: signature's chain vs cohort baseline
3. Transition novelty — fraction of new edges in last N transitions
4. Entropy delta — change in path entropy over time
5. Loop score — fraction of A→B→A→B cycles
6. Sequence surprise — avg `-log P(transition)` under cohort baseline

### Transition Event Audit Trail

Every meaningful score change is recorded with full attribution:

```csharp
SignatureTransitionEvent {
    Signature, Timestamp, EventType,
    FromValue, ToValue, Trigger,
    ContributingSignals  // snapshot of top signals
}
```

In-memory ring buffer (50 per signature) + async batch write to TimescaleDB every 10s.

Event types: DriftSpike, EntropyDrop, LoopDetected, CohortShift, NoveltyBurst, ClusterJoined, ClusterPromoted, WeightShift.

### Storage

In-memory primary for all hot-path operations. Periodic snapshots to TimescaleDB:

```sql
CREATE TABLE markov_snapshots (
    id BIGSERIAL,
    scope TEXT,           -- 'global', 'cohort:datacenter-new', 'signature:A1B2C3'
    matrix_json JSONB,
    captured_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (id, captured_at)
);
SELECT create_hypertable('markov_snapshots', 'captured_at');

CREATE TABLE signature_transitions (
    id BIGSERIAL,
    signature TEXT NOT NULL,
    event_type TEXT NOT NULL,
    from_value DOUBLE PRECISION,
    to_value DOUBLE PRECISION,
    trigger TEXT,
    contributing_signals JSONB,
    occurred_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (id, occurred_at)
);
SELECT create_hypertable('signature_transitions', 'occurred_at');
```

Snapshots every 5 min for cohort/global models. Transition events batched every 10s.

### Escalation Ladder (Drift-Driven)

| Level | Condition | Response |
|-------|-----------|----------|
| Ambient | JS divergence < 0.2, no loops | No action |
| Mild | Self-drift 0.2-0.4 OR novelty > 0.3 | Add jitter |
| Medium | Human-drift > 0.4 OR loop > 0.5 | Reduce page size, slow |
| High | Self-drift > 0.6 AND human-drift > 0.5 | Challenge |
| Critical | Cluster + high drift + surprise > 3.0 | Block/deceive |

## Integration Points

- `SignatureCoordinator.RecordRequestAsync()` → extract geo from signals dict, call `MarkovTracker.RecordTransition()`
- `BotClusterService.RunClustering()` → call `AdaptiveSimilarityWeighter`, use drift signals in FeatureVector
- `AdvancedBehavioralContributor` → inject `MarkovTracker`, use drift signals for rich contribution reasons
- `ClusterContributor` → include transition events in narrative

## New Files

| File | Purpose |
|------|---------|
| `Markov/DecayingTransitionMatrix.cs` | Space-bounded, decayed, top-K transition matrix |
| `Markov/MarkovTracker.cs` | Per-signature + cohort + global chains, returns drift signals |
| `Markov/PopulationMarkovService.cs` | BackgroundService for async cohort updates + snapshots |
| `Markov/PathNormalizer.cs` | Route template extraction, type bucketing |
| `Markov/DriftSignals.cs` | Record type + computation (JS divergence, loops, etc.) |
| `Clustering/AdaptiveSimilarityWeighter.cs` | Diagnosticity engine |
| `Models/SignatureTransitionEvent.cs` | Audit trail model |
| `UI.PostgreSQL/Storage/MarkovSnapshotStore.cs` | TimescaleDB persistence |

## Modified Files

| File | Change |
|------|--------|
| `SignatureCoordinator.cs` | Expand SignatureGeoContext, call MarkovTracker |
| `BotClusterService.cs` | Adaptive weights, drift features, geo proximity |
| `AdvancedBehavioralContributor.cs` | Use drift signals from MarkovTracker |
| `ClusterContributor.cs` | Transition events in narrative |
| `BotDetectionOptions.cs` | Add MarkovOptions section |
| `ServiceCollectionExtensions.cs` | Register new services |

## Space Budget

~33MB total in-memory: 20MB signatures + 2.5MB cohorts + 200KB global + 10MB transition events.
