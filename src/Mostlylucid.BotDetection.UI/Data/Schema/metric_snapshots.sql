-- SqliteMetricSnapshotStore schema. Per-pack telemetry samples keyed by
-- (bucket_time, pack_id, instrument); idx_ms_lookup covers the dashboard
-- query that aggregates snapshots for a pack's instruments in a time range.

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
CREATE INDEX IF NOT EXISTS idx_ms_lookup
    ON metric_snapshots(bucket_time, pack_id, instrument);
