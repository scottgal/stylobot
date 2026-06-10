-- SqliteStickyDenyStore schema. Loaded by the store at InitializeAsync via
-- SchemaLoader. Per-key violation counter + block_until_ticks; one row per
-- rate-limit key.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS sticky_deny (
    key TEXT PRIMARY KEY,
    first_violation_ticks INTEGER NOT NULL,
    last_violation_ticks INTEGER NOT NULL,
    violation_count INTEGER NOT NULL,
    blocked_until_ticks INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_sticky_deny_blocked_until
    ON sticky_deny(blocked_until_ticks);
