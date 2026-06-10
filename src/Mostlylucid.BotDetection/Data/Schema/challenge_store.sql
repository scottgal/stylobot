-- ChallengeStore schema. Loaded by ChallengeStore at first-use via
-- SchemaLoader. Two tables:
--   challenges     -- outstanding micro-puzzle bundles per signature, with
--                     TTL via expires_at; consumed=1 once verified.
--   verifications  -- per-signature timing record of successful solves;
--                     used by the challenge-as-signal feedback loop to
--                     score worker-count claims and timing jitter.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA cache_size=-2000;

CREATE TABLE IF NOT EXISTS challenges (
    id TEXT PRIMARY KEY,
    signature TEXT NOT NULL,
    puzzle_count INTEGER NOT NULL,
    required_zeros INTEGER NOT NULL,
    puzzles_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    consumed INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS verifications (
    signature TEXT PRIMARY KEY,
    total_solve_duration_ms REAL NOT NULL,
    reported_worker_count INTEGER NOT NULL,
    puzzle_count INTEGER NOT NULL,
    puzzle_timings_json TEXT NOT NULL,
    timing_jitter REAL NOT NULL,
    verified_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_challenges_expires ON challenges(expires_at);
CREATE INDEX IF NOT EXISTS idx_challenges_signature ON challenges(signature);
CREATE INDEX IF NOT EXISTS idx_verifications_verified ON verifications(verified_at);
