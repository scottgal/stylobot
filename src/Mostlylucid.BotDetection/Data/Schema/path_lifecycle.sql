-- SqlitePathLifecycleStore schema. Per-path counters tracking 2xx/4xx
-- transitions for the path-discovery heuristic.
--
-- Multi-domain: (host, path) is the natural key so the same path served by
-- different hosts is tracked independently; domain is the eTLD+1 rollup column.
-- Pre-existing single-column-PK databases are migrated forward by the
-- MultiDomainAdd migration; this fresh-init schema captures the target shape.

CREATE TABLE IF NOT EXISTS path_lifecycle (
    domain                  TEXT NOT NULL DEFAULT 'unknown',
    host                    TEXT NOT NULL DEFAULT 'unknown',
    path                    TEXT NOT NULL,
    first_seen_utc          TEXT NOT NULL,
    total_2xx               INTEGER NOT NULL DEFAULT 0,
    total_4xx               INTEGER NOT NULL DEFAULT 0,
    total_other             INTEGER NOT NULL DEFAULT 0,
    last_2xx_utc            TEXT,
    first_4xx_after_2xx_utc TEXT,
    last_seen_utc           TEXT NOT NULL,
    PRIMARY KEY (host, path)
);

CREATE INDEX IF NOT EXISTS idx_path_lifecycle_last_seen
    ON path_lifecycle(last_seen_utc);

CREATE INDEX IF NOT EXISTS idx_path_lifecycle_domain
    ON path_lifecycle(domain);