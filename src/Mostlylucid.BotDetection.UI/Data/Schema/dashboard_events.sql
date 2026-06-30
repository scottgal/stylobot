-- SqliteDashboardEventStore primary schema. Loaded at startup. Statements
-- are split on the SQL terminator and run one at a time so a per-statement
-- DDL failure surfaces with the actual offending statement instead of a
-- wall of SQL. Column migrations (ALTER TABLE ADD COLUMN) and the dependent
-- analytics indexes live in C# / dashboard_events_analytics_indexes.sql
-- because they need the new columns to exist first.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA cache_size=-4000;

CREATE TABLE IF NOT EXISTS detections (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    signature TEXT NOT NULL,
    method TEXT,
    path TEXT,
    is_bot INTEGER NOT NULL,
    bot_probability REAL NOT NULL,
    confidence REAL NOT NULL,
    risk_band TEXT,
    bot_name TEXT,
    bot_type TEXT,
    action TEXT,
    country_code TEXT,
    processing_time_ms REAL,
    threat_score REAL DEFAULT 0,
    threat_band TEXT,
    status_code INTEGER DEFAULT 0,
    user_agent_raw TEXT,
    response_bytes INTEGER,
    risk_justification TEXT
);

CREATE TABLE IF NOT EXISTS signatures (
    signature TEXT PRIMARY KEY,
    bot_name TEXT,
    bot_type TEXT,
    is_bot INTEGER NOT NULL DEFAULT 0,
    bot_probability REAL NOT NULL DEFAULT 0,
    confidence REAL NOT NULL DEFAULT 0,
    risk_band TEXT,
    action TEXT,
    country_code TEXT,
    hit_count INTEGER NOT NULL DEFAULT 1,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    processing_time_ms REAL DEFAULT 0,
    threat_score REAL DEFAULT 0,
    threat_band TEXT,
    narrative TEXT,
    metadata_json TEXT,
    risk_justification TEXT
);

CREATE INDEX IF NOT EXISTS idx_det_timestamp ON detections(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_det_signature ON detections(signature);
CREATE INDEX IF NOT EXISTS idx_det_is_bot ON detections(is_bot);
CREATE INDEX IF NOT EXISTS idx_det_country ON detections(country_code);
CREATE INDEX IF NOT EXISTS idx_det_path ON detections(path);
CREATE INDEX IF NOT EXISTS idx_sig_last_seen ON signatures(last_seen DESC);
CREATE INDEX IF NOT EXISTS idx_sig_is_bot ON signatures(is_bot);
CREATE INDEX IF NOT EXISTS idx_det_threat ON detections(threat_score DESC, timestamp DESC);

CREATE TABLE IF NOT EXISTS user_agent_stats (
    ua_family TEXT NOT NULL,
    ua_version TEXT NOT NULL DEFAULT '',
    ua_os TEXT NOT NULL DEFAULT '',
    is_bot INTEGER NOT NULL DEFAULT 0,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    hit_count INTEGER NOT NULL DEFAULT 1,
    unique_signatures INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY (ua_family, ua_version, ua_os)
);
CREATE INDEX IF NOT EXISTS idx_ua_family ON user_agent_stats(ua_family, hit_count DESC);

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

-- Site-health degradation snapshots -- one row per ScheduleCoordinator Tick10s,
-- written by DegradationStoreSampler. Replaces the deleted in-memory
-- DegradationHistoryAtom ring per [[feedback_no_inmemory_stores]]: the ring
-- lost the entire window on restart; this table persists across reboot so the
-- Traffic page's site-health chartlet keeps reading real values after a
-- gateway restart. Append-only; pruned alongside detections by the retention
-- sweep. Numeric columns mirror DegradationSnapshot 1:1 so the read path is a
-- direct projection without per-call shape translation.
CREATE TABLE IF NOT EXISTS degradation_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp       TEXT    NOT NULL,
    latency_p50_ms  REAL    NOT NULL DEFAULT 0,
    latency_p95_ms  REAL    NOT NULL DEFAULT 0,
    rate_5xx        REAL    NOT NULL DEFAULT 0,
    rate_4xx        REAL    NOT NULL DEFAULT 0,
    rate_429        REAL    NOT NULL DEFAULT 0,
    rate_404        REAL    NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_degradation_timestamp
    ON degradation_history(timestamp DESC);
