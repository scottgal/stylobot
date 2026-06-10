-- SqliteClusterStore schema. Per-cluster aggregates from the Leiden community
-- detection pass: size, intra-cluster similarity, temporal density, dominant
-- intent + threat score, and the member-signature list (json). Read by the
-- dashboard cluster view + the intent classifier's cluster-mapping path.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS clusters (
    cluster_id              TEXT PRIMARY KEY,
    cluster_type            INTEGER NOT NULL,
    member_count            INTEGER NOT NULL,
    average_bot_probability REAL NOT NULL,
    average_similarity      REAL NOT NULL,
    connectedness           REAL NOT NULL,
    temporal_density        REAL NOT NULL,
    dominant_country        TEXT,
    dominant_asn            TEXT,
    label                   TEXT,
    description             TEXT,
    first_seen              TEXT NOT NULL,
    last_seen               TEXT NOT NULL,
    dominant_intent         TEXT,
    average_threat_score    REAL NOT NULL,
    member_signatures_json  TEXT NOT NULL,
    updated_at              TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_clusters_last_seen ON clusters(last_seen DESC);
