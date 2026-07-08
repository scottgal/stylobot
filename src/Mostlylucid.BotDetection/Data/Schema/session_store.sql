-- SqliteSessionStore schema. Loaded by the store at InitializeAsync via
-- SchemaLoader. The schema covers four concerns:
--   * Sessions + per-signature aggregates (sessions, signatures, buckets).
--   * Anonymous entity resolution (entities + entity_edges with merge/split
--     audit trail).
--   * Per-request log (requests) that gets atomized into sessions.
--   * Centroid persistence for the HNSW replacement path (signature_centroids,
--     session_centroids, intent_centroids) plus the destructive-merge
--     forwarding table (signature_merges).
--
-- Column-add migrations live in C# (MigrateAddColumnAsync) because they need
-- the "catch duplicate column" pattern. Net-new tables and indexes belong here.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA cache_size=-8000;

CREATE TABLE IF NOT EXISTS sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    signature TEXT NOT NULL,
    started_at TEXT NOT NULL,
    ended_at TEXT NOT NULL,
    request_count INTEGER NOT NULL,
    vector BLOB,
    maturity REAL NOT NULL,
    dominant_state TEXT NOT NULL,
    is_bot INTEGER NOT NULL,
    avg_bot_probability REAL NOT NULL,
    avg_confidence REAL NOT NULL,
    risk_band TEXT NOT NULL,
    action TEXT,
    bot_name TEXT,
    bot_type TEXT,
    country_code TEXT,
    top_reasons_json TEXT,
    transition_counts_json TEXT,
    paths_json TEXT,
    avg_processing_time_ms REAL,
    error_count INTEGER DEFAULT 0,
    timing_entropy REAL DEFAULT 0,
    narrative TEXT,
    header_hashes_json TEXT,
    user_agent_raw TEXT,
    frequency_fingerprint BLOB,
    drift_vector BLOB
);

CREATE INDEX IF NOT EXISTS idx_sessions_signature ON sessions(signature, ended_at DESC);
CREATE INDEX IF NOT EXISTS idx_sessions_ended ON sessions(ended_at DESC);
CREATE INDEX IF NOT EXISTS idx_sessions_is_bot ON sessions(is_bot, ended_at DESC);
CREATE INDEX IF NOT EXISTS idx_sessions_country ON sessions(country_code);
-- Multi-domain composite indexes. On fresh installs the sessions.domain/host
-- columns don't exist until MigrateAddColumnAsync in SqliteSessionStore runs
-- first (see the C# init path). SQLite CREATE INDEX will fail with
-- "no such column" if the columns are missing, so these are re-issued in C#
-- (via CREATE INDEX IF NOT EXISTS) AFTER the ADD COLUMN migrations. They live
-- here too so the schema file remains the single source of truth for the
-- target shape; the C# path is the runtime idempotent applicator.

CREATE TABLE IF NOT EXISTS signatures (
    signature_id TEXT PRIMARY KEY,
    session_count INTEGER NOT NULL DEFAULT 0,
    total_request_count INTEGER NOT NULL DEFAULT 0,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    is_bot INTEGER NOT NULL DEFAULT 0,
    bot_probability REAL NOT NULL DEFAULT 0,
    confidence REAL NOT NULL DEFAULT 0,
    risk_band TEXT NOT NULL DEFAULT 'Low',
    bot_name TEXT,
    bot_type TEXT,
    action TEXT,
    country_code TEXT,
    root_vector BLOB,
    root_vector_maturity REAL DEFAULT 0,
    narrative TEXT,
    top_reasons_json TEXT,
    last_updated_utc TEXT
);

CREATE INDEX IF NOT EXISTS idx_signatures_bot ON signatures(is_bot, session_count DESC);
CREATE INDEX IF NOT EXISTS idx_signatures_last_seen ON signatures(last_seen DESC);

CREATE TABLE IF NOT EXISTS buckets (
    bucket_time TEXT NOT NULL PRIMARY KEY,
    total_count INTEGER NOT NULL DEFAULT 0,
    bot_count INTEGER NOT NULL DEFAULT 0,
    human_count INTEGER NOT NULL DEFAULT 0,
    unique_signatures INTEGER NOT NULL DEFAULT 0,
    sessions_started INTEGER NOT NULL DEFAULT 0,
    avg_processing_time_ms REAL NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_buckets_time ON buckets(bucket_time DESC);

-- Entity Resolution: resolved actor identities.
-- An entity is our best guess at "this is one actor."
-- Multiple PrimarySignatures can map to one entity (merge).
-- One signature can fork into multiple entities (split).
CREATE TABLE IF NOT EXISTS entities (
    entity_id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    confidence_level INTEGER NOT NULL DEFAULT 0,
    factor_count INTEGER NOT NULL DEFAULT 1,
    is_bot INTEGER NOT NULL DEFAULT 0,
    bot_probability REAL NOT NULL DEFAULT 0,
    reputation_score REAL NOT NULL DEFAULT 0,
    stable_anchors_json TEXT,
    rotation_cadence_seconds REAL,
    velocity_variance REAL,
    metadata_json TEXT
);

CREATE INDEX IF NOT EXISTS idx_entities_updated ON entities(updated_at DESC);
CREATE INDEX IF NOT EXISTS idx_entities_bot ON entities(is_bot, reputation_score DESC);

-- Edges link PrimarySignatures to entities with audit trail.
CREATE TABLE IF NOT EXISTS entity_edges (
    edge_id TEXT PRIMARY KEY,
    entity_id TEXT NOT NULL,
    signature TEXT NOT NULL,
    edge_type TEXT NOT NULL,
    confidence REAL NOT NULL,
    created_at TEXT NOT NULL,
    reason TEXT,
    reverted_at TEXT,
    FOREIGN KEY (entity_id) REFERENCES entities(entity_id)
);

CREATE INDEX IF NOT EXISTS idx_edges_signature ON entity_edges(signature, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_edges_entity ON entity_edges(entity_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_edges_active ON entity_edges(signature) WHERE reverted_at IS NULL;

CREATE TABLE IF NOT EXISTS requests (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    signature       TEXT    NOT NULL,
    timestamp       TEXT    NOT NULL,
    path            TEXT    NOT NULL,
    markov_state    TEXT    NOT NULL,
    status_code     INTEGER NOT NULL,
    bot_probability REAL    NOT NULL,
    confidence      REAL    NOT NULL,
    risk_band       TEXT    NOT NULL,
    processing_ms   REAL    NOT NULL DEFAULT 0,
    session_id      INTEGER REFERENCES sessions(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_requests_sig_time  ON requests(signature, timestamp ASC);
CREATE INDEX IF NOT EXISTS idx_requests_unatomized ON requests(signature, timestamp ASC) WHERE session_id IS NULL;
CREATE INDEX IF NOT EXISTS idx_requests_ts_desc   ON requests(timestamp DESC);

-- Centroid tables: bounded per-signature persistence for HNSW replacement (Task 2).

CREATE TABLE IF NOT EXISTS signature_centroids (
    signature_id TEXT PRIMARY KEY,
    vector       BLOB    NOT NULL,
    was_bot      INTEGER NOT NULL DEFAULT 0,
    confidence   REAL    NOT NULL DEFAULT 0.5,
    updated_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sigc_updated ON signature_centroids(updated_at);

CREATE TABLE IF NOT EXISTS session_centroids (
    signature_id      TEXT PRIMARY KEY,
    vector            BLOB    NOT NULL,
    velocity_vector   BLOB,
    variance_vector   BLOB,
    freq_fingerprint  BLOB,
    cluster_id        TEXT,
    compression_level INTEGER NOT NULL DEFAULT 0,
    is_bot            INTEGER NOT NULL DEFAULT 0,
    bot_probability   REAL    NOT NULL DEFAULT 0.0,
    priority          REAL    NOT NULL DEFAULT 0.5,
    updated_at        INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sesc_updated ON session_centroids(updated_at);
CREATE INDEX IF NOT EXISTS idx_sesc_cluster  ON session_centroids(cluster_id);

CREATE TABLE IF NOT EXISTS intent_centroids (
    signature_id    TEXT PRIMARY KEY,
    vector          BLOB    NOT NULL,
    threat_score    REAL    NOT NULL DEFAULT 0.0,
    intent_category TEXT    NOT NULL DEFAULT 'unknown',
    updated_at      INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_intc_updated ON intent_centroids(updated_at);

-- Merge-history forwarding table. Each row says "old_signature_id has been
-- merged into new_signature_id". Read path (ResolveSignatureAsync) follows
-- the chain so dashboard URLs survive a signature being absorbed. The FOSS
-- upstream doesn't currently emit destructive merges; the table exists so
-- commercial labelling flows + future manual-merge dashboard actions can
-- populate it and the FOSS UI 301-redirects without any other change.
CREATE TABLE IF NOT EXISTS signature_merges (
    old_signature_id TEXT PRIMARY KEY,
    new_signature_id TEXT NOT NULL,
    reason           TEXT NOT NULL DEFAULT 'unknown',
    merged_at        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sigm_new ON signature_merges(new_signature_id);
