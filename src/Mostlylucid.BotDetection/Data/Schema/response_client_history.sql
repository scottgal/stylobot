-- SqliteResponseHistoryStore schema. Cross-restart durability for
-- ResponseCoordinator's per-client response-behaviour aggregates (exclusive-404
-- scan detection, honeypot-hit counting, auth-struggle, fail2ban escalation --
-- see ResponseBehaviorAtom). Before this table existed the hot-tier
-- ClientResponseTrackingAtom (bounded, TTL-evicted, in-process memory only) was
-- the sole source of this state -- a pod restart mid-scan silently reset a
-- scanning client's history to zero. This table is the write-behind durable
-- tier: counts only, not the exact set of distinct 404 paths (the detection
-- consumers only ever read the count; see unique_404_paths).

CREATE TABLE IF NOT EXISTS response_client_history (
    client_id               TEXT PRIMARY KEY,
    first_seen_utc           TEXT NOT NULL,
    last_seen_utc            TEXT NOT NULL,
    total_count              INTEGER NOT NULL DEFAULT 0,
    count_2xx                INTEGER NOT NULL DEFAULT 0,
    count_3xx                INTEGER NOT NULL DEFAULT 0,
    count_4xx                INTEGER NOT NULL DEFAULT 0,
    count_5xx                INTEGER NOT NULL DEFAULT 0,
    count_404                INTEGER NOT NULL DEFAULT 0,
    unique_404_paths         INTEGER NOT NULL DEFAULT 0,
    auth_failures            INTEGER NOT NULL DEFAULT 0,
    honeypot_hits            INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_response_client_history_last_seen
    ON response_client_history(last_seen_utc);
