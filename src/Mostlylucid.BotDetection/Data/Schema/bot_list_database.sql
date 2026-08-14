-- BotListDatabase schema. Loaded by BotListDatabase via SchemaLoader at every
-- read/write operation so the "process-local _initialized flag is set but the
-- table isn't actually there" failure mode (observed in production on .15)
-- self-heals on the next call. All CREATE statements are idempotent.

CREATE TABLE IF NOT EXISTS bot_patterns (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    pattern TEXT NOT NULL,
    category TEXT NOT NULL,
    url TEXT,
    is_verified INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    UNIQUE(pattern)
);

-- idx_bot_patterns_pattern duplicates the UNIQUE(pattern) constraint's
-- implicit index (dbreview- 2026-08-14).
DROP INDEX IF EXISTS idx_bot_patterns_pattern;
CREATE INDEX IF NOT EXISTS idx_bot_patterns_category ON bot_patterns(category);

CREATE TABLE IF NOT EXISTS datacenter_ips (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ip_range TEXT NOT NULL UNIQUE,
    provider TEXT,
    region TEXT,
    created_at TEXT NOT NULL
);

-- idx_datacenter_ips_range duplicates the UNIQUE(ip_range) constraint's
-- implicit index (dbreview- 2026-08-14).
DROP INDEX IF EXISTS idx_datacenter_ips_range;

CREATE TABLE IF NOT EXISTS list_updates (
    list_type TEXT PRIMARY KEY,
    last_update TEXT NOT NULL,
    record_count INTEGER NOT NULL
);
