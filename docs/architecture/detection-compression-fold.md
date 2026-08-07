# Detection Compression Fold — single temporal store

**Status:** implemented on `feat/streaming-compression` (development branch; not on main).

## Problem

The `detections` table grows without bound — every detection event was persisted as a
raw row with full per-request detail (user agent, path, justification text). At fleet
scale this degrades query performance and wastes disk. Retention alone (deleting rows
after 30 days) bounds the table but keeps every recent row at full size; this design
adds the *compression* half of the forgetting curve.

## Shape: one store, no bucket tables

There is exactly **one table** and **one row shape**. Aging never creates summary rows,
bucket tables, or materialized aggregates. A row's resolution is a monotone function of
(age × inverse importance):

- Rows younger than `HotWindow` (default 2h) keep full per-request detail.
- Past `HotWindow`, rows whose write-time importance is below `ImportanceFloor`
  (default 0.4) are **folded**: the verbose per-request TEXT detail columns are nulled.
- Past `FullAbsorptionAge` (default 48h), every row folds regardless of importance —
  an old row is its own summary.
- Rows still live until `DetectionRetention` (default 30d) deletes them — the fold is
  the *compress* dial, retention the *erase* dial of the same forgetting curve.

The folded row keeps everything the dashboard aggregates on: counts, bot probability,
confidence, risk band, action, threat score/band, domain/host, and the numeric KPI
columns (`response_bytes`, `processing_time_ms`). Dashboard reads therefore return
identical shapes with or without compression — only the drill-down detail ages out.

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
stores and test fakes compile unchanged). Two sub-passes per tick, each
`ORDER BY importance_weight ASC, timestamp ASC LIMIT FoldBatchSize`:

1. Rows older than `HotWindow` with weight below `ImportanceFloor`.
2. Rows older than `FullAbsorptionAge` (all weights).

The `importance_weight, timestamp` composite index (`idx_det_compression`) serves
both passes. The drain marker `method IS NOT NULL` skips already-folded rows so the
batch always advances to new rows. The fold is idempotent and single-flight
(one subscriber, one batch window per tick — the one-at-a-time slow path; no
fire-and-forget bursts contending the writer).

Foldable detail columns (nulled): `method`, `path`, `user_agent_raw`,
`referrer_host`, `ua_device_class`, `risk_justification`. Everything else survives.
Reads use their existing missing-value conventions (NULL method reads as `""`, NULL
path as `/`).

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
        "ThreatScoreNormalizer": 1.0
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
