-- SqlitePathLifecycleStore schema. Per-path counters tracking 2xx/4xx
-- transitions for the path-discovery heuristic.

CREATE TABLE IF NOT EXISTS path_lifecycle (
    path                    TEXT PRIMARY KEY,
    first_seen_utc          TEXT NOT NULL,
    total_2xx               INTEGER NOT NULL DEFAULT 0,
    total_4xx               INTEGER NOT NULL DEFAULT 0,
    total_other             INTEGER NOT NULL DEFAULT 0,
    last_2xx_utc            TEXT,
    first_4xx_after_2xx_utc TEXT,
    last_seen_utc           TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_path_lifecycle_last_seen
    ON path_lifecycle(last_seen_utc);
