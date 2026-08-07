# Detection Compression Fold — single temporal store

**Status:** implemented on `feat/streaming-compression` (development branch; not on main).

## Problem

The `detections` table grows without bound — every detection event was persisted as a
raw row with full per-request detail (user agent, path, justification text). At fleet
scale this degrades query performance and wastes disk. Retention alone (deleting rows
after 30 days) bounds the table but keeps every recent row at full size; this design
adds the *compression* half of the forgetting curve.

## Shape: one store, no bucket tables

There is exactly **one table** and **one row shape**. Aging never creates summary row
*types*, bucket tables, or materialized aggregates — but a summary row CAN be a fused
version of the rows it absorbed, within the same table. A row's resolution is a
monotone function of (age × inverse importance), with two tiers:

- Rows younger than `HotWindow` (default 2h) keep full per-request detail.
- Past `HotWindow`, rows whose write-time importance is below `ImportanceFloor`
  (default 0.4) are **fused**: one summary row per (signature, hour-bucket, domain,
  country, bot_type) carrying exact aggregate counters (`hit_count`, `bot_count`,
  `bytes_sum`, `ms_sum`, `ms_max`), and the absorbed rows are **deleted** — this is
  what actually bounds table growth. Importance decides WHO fuses; the hour bucket
  decides the fusion GRANULARITY. Enforcement rows (block/challenge/throttle/rate/
  honeypot/simulation actions) and threat rows (score at/above
  `FusionThreatCeiling`, default 0.5) are exempt — they are the audit trail and the
  evidence feed, and keep their own row.
- Past `FullAbsorptionAge` (default 48h), the rows that did NOT fuse (important,
  enforcement, threat) lose their detail columns — an old row is its own summary.
- Rows still live until `DetectionRetention` (default 30d) deletes them — the fold is
  the *compress* dial, retention the *erase* dial of the same forgetting curve.

The fused/summarised row keeps everything the dashboard aggregates on: counts, bot
probability, confidence, risk band, action, threat score/band, domain/host, and the
numeric KPI columns (`response_bytes`, `processing_time_ms`). Dashboard reads return
identical shapes AND identical values with or without compression — count queries
weight fused rows by their counters (`SUM(hit_count)`), drill-downs exclude them
(`fused = 0`). Only the per-request drill-down detail ages out.

### The fusion key and exactness

Fused rows group by (signature, hour-bucket, domain, country, bot_type), so
domain-filtered reads, country stats, the bot/human split (via `bot_count` computed
with the same floor the reads use), and internal-traffic exclusion (via the key's
`bot_type`) stay EXACT. Risk-band distributions read the signatures table, and visitor
segment counts are `COUNT(DISTINCT signature)` over a signatures join — both exact
with fused rows. Fused rows anchor their timestamp to the hour bucket start, so
time-series bucketing lands them in their own bucket. A group split across fold ticks
(one batch boundary) merges into the existing fused row instead of duplicating.

### Why not plain time-bucket aggregation?

The operator's correction stands: no separate bucket tables, no materialized
aggregates. Fusion keeps ONE store and ONE row shape — the summary row IS a row of the
same table with the same columns, flagged `fused = 1`. The time bucket appears only as
the fusion granularity for the low-importance tier, never as a storage tier.

## Importance — computed once at write time

`DetectionImportance.ComputeWeight` runs when the row lands (in
`SqliteDashboardEventStore.AddDetectionAsync`) and is stored on the row in the
`importance_weight` column; the fold reads it back, never recomputes it.

```
weight = clamp01( BotScoreWeight × botProbability
                + ThreatScoreWeight × clamp01(threatScore / ThreatScoreNormalizer)
                + ActionWeight × actionBonus )
```

- **Bot score** — the row's own classification (0.5).
- **Threat score** — pipeline threat score, already normalized 0..1 (0.3).
- **Action** — enforcement ranking, because action policy names are free-form the
  bonus is keyword-ranked: block 1.0, challenge 0.8, honeypot/simulation 0.7,
  throttle 0.6, rate-limit 0.4, else 0. Enforcement rows are the audit trail and keep
  detail longest (0.2).

The blend weights, normalizer, floor, and windows are all knobs on
`TemporalStoreOptions` (bound from `StyloBot:Dashboard:TemporalStore`) — no magic
numbers. Defaults are tuned for the FOSS single-host SQLite store (forgets faster
than a fleet-scale store would).

## The fold pass

`DetectionCompressionFold` subscribes to `ScheduleCoordinator` `Tick5m`
(`CostHint.Low`) and drives `IDashboardEventStore.FoldAgedDetectionsAsync` — the
audited store API (the interface default is a no-op returning 0, so non-SQLite
stores and test fakes compile unchanged). Two PARTITIONED sub-passes per tick, each
`ORDER BY importance_weight ASC, timestamp ASC LIMIT FoldBatchSize` — no row is ever
claimed by both:

1. **Fusion** (rows older than `HotWindow`, weight below `ImportanceFloor`,
   non-enforcement, threat below the ceiling): group + representative + counters,
   delete the absorbed rows. The representative becomes the fused summary row
   (detail cleared, timestamp anchored to the bucket start).
2. **Full absorption** (rows older than `FullAbsorptionAge` that are NOT
   fusion-eligible — weight at/above the floor, enforcement, or threat): detail
   columns nulled.

The partition matters: if pass 2 could null a fusion-eligible row's `method` before
pass 1 fused it, the row would lose its drain marker and never fuse (the original
bug, caught by the 30-day plateau test).

The `importance_weight, timestamp` composite index (`idx_det_compression`) serves
both passes. The drain marker `method IS NOT NULL` skips already-folded/fused rows
so the batch always advances to new rows. The fold is idempotent and single-flight
(one subscriber, one batch window per tick — the one-at-a-time slow path; no
fire-and-forget bursts contending the writer).

Foldable detail columns (nulled): `method`, `path`, `user_agent_raw`,
`referrer_host`, `ua_device_class`, `risk_justification`. Everything else survives.
Reads use their existing missing-value conventions (NULL method reads as `""`, NULL
path as `/`).

## Read path

Count queries (summary, time-series, top-bots, domain stats, country stats/detail,
investigation summary + country) weight fused rows by their counters via shared
fused-aware SQL expressions (`CASE WHEN fused = 1 THEN hit_count ELSE 1 END` etc.)
and let fused rows pass audience filters (their split lives in the counters).
Drill-downs (detections list, endpoint stats, per-signature endpoints, threats,
investigation detail) exclude fused rows (`fused = 0`) — they are not real events.
`GetVisitorSegmentCountsAsync` needs no change (distinct signatures over the
signatures join), and honeypot rows never fuse (enforcement-exempt).

## Long-period stability (soak)

The 30-day plateau test in the test suite proves the steady state deterministically
(fusion + retention bound the table while counts stay exact). For the real thing,
`scripts/soak/run-compression-soak.sh` drives the existing k6 corpus against an
isolated gateway with `CompressionEnabled=true` for hours, sampling row count + DB
file size per window, and asserts the plateau: growth after warm-up must collapse to
a small fraction of the raw pipeline's accumulation. Run by the deploy lane on an
isolated rig (the script refuses :8190/staging).

## Tick wiring

Both maintenance services run on the schedule coordinator (one coordinator emits
ticks — no private `BackgroundService` loops):

| Service | Cadence | Gate |
|---|---|---|
| `DetectionCompressionFold` | `Tick5m` | `TemporalStore:CompressionEnabled` (default **false**) + SQLite store only |
| `DetectionRetentionPruner` | `Tick1h` | — (deletes past `DetectionRetention`) |

Both self-disable on viewer-mode hosts with no coordinator, mirroring
`DashboardMaterializerCoordinator`.

## Configuration

```json
{
  "StyloBot": {
    "Dashboard": {
      "TemporalStore": {
        "CompressionEnabled": true,
        "HotWindow": "02:00:00",
        "FullAbsorptionAge": "48:00:00",
        "ImportanceFloor": 0.4,
        "FoldBatchSize": 200,
        "BotScoreWeight": 0.5,
        "ThreatScoreWeight": 0.3,
        "ActionWeight": 0.2,
        "ThreatScoreNormalizer": 1.0,
        "FusionThreatCeiling": 0.5
      }
    }
  }
}
```

`CompressionEnabled` defaults to `false` — today's raw behavior is the default until a
host opts in and validates. The raw path stays forever.

## Verification

- Unit tests: importance math (blend, clamping, action ranking, operator tilt).
- Store tests (real SQLite): schema column, weight persistence, fold tiers (young /
  low-importance / important / past-full-absorption), batch drain order, idempotence.
- Read parity: `GetSummaryAsync` / `GetTimeSeriesAsync` / `GetDetectionsAsync` return
  identical shapes and exact counts before vs after folding.
- Service tests: tick folds only when enabled; no-op without a coordinator; pruner
  tick deletes past-retention rows via the audited API.

Non-SQLite stores (e.g. a fleet-scale Postgres store) inherit the no-op interface
default and implement their own fold in their own lane.
