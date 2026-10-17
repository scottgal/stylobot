-- SqliteMetricSnapshotStore schema. Per-pack telemetry samples keyed by
-- (bucket_time, pack_id, instrument). The query shapes are pack-led:
--   GetSnapshotsAsync     WHERE pack_id = ? AND instrument = ? AND bucket_time range
--   GetLatestSnapshots    WHERE pack_id = ? AND bucket_time = (SELECT MAX(bucket_time) ...)
--   prune                 WHERE bucket_time < cutoff
-- so the serving indexes are pack-first (dbreview- 2026-08-14; the old
-- idx_ms_lookup led with bucket_time and could only range-scan the leading
-- column, filtering pack/instrument as residuals).

CREATE TABLE IF NOT EXISTS metric_snapshots (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    bucket_time TEXT    NOT NULL,
    pack_id     TEXT    NOT NULL,
    meter_name  TEXT    NOT NULL,
    instrument  TEXT    NOT NULL,
    tags        TEXT,
    value       REAL    NOT NULL,
    value_type  TEXT    NOT NULL
);
-- Pack-led windowed reads: equality on pack + instrument, range on time.
CREATE INDEX IF NOT EXISTS idx_ms_pack_instrument_time
    ON metric_snapshots(pack_id, instrument, bucket_time);
-- Latest-per-pack: the MAX(bucket_time) subquery is pack-led.
CREATE INDEX IF NOT EXISTS idx_ms_pack_time
    ON metric_snapshots(pack_id, bucket_time DESC);
-- Prune: bucket_time-only range delete.
CREATE INDEX IF NOT EXISTS idx_ms_bucket_time
    ON metric_snapshots(bucket_time);
-- Replaced by the three above (see header note).
DROP INDEX IF EXISTS idx_ms_lookup;
