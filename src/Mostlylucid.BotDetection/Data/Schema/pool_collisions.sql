-- SqlitePoolCollisionStore schema. One row per (shape_hash, context_key)
-- with last_seen_ticks. Cold-load reads the last N hours per shape_hash.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS pool_collisions (
    shape_hash TEXT NOT NULL,
    context_key TEXT NOT NULL,
    last_seen_ticks INTEGER NOT NULL,
    PRIMARY KEY (shape_hash, context_key)
);

CREATE INDEX IF NOT EXISTS idx_pool_collisions_shape_ts
    ON pool_collisions(shape_hash, last_seen_ticks);
