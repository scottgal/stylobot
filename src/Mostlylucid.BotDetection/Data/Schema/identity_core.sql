-- Metastable fingerprint match system core tables. Seven tables + their
-- indexes; idempotent (every CREATE has IF NOT EXISTS). The vec0 virtual
-- indexes used by SqliteVecIdentityAnchorIndex stay inline in C# because
-- they take a runtime `dimension` parameter; forward-only ALTER TABLE
-- migrations + data backfills stay in IdentitySchema.cs because they have
-- intertwined exception-handling logic (SQLite's ALTER TABLE has no
-- IF NOT EXISTS for columns, so each ADD COLUMN is in a try/catch).
--
-- See docs/architecture/fingerprint-match.md.

CREATE TABLE IF NOT EXISTS fingerprints (
    fingerprint_id              TEXT PRIMARY KEY,
    centroid                    BLOB NOT NULL,
    centroid_maturity           INTEGER NOT NULL,
    weights                     BLOB NOT NULL,
    member_count                INTEGER NOT NULL,
    observation_count           INTEGER NOT NULL,
    correction_count            INTEGER NOT NULL,
    first_seen                  TEXT NOT NULL,
    last_seen                   TEXT NOT NULL,
    quality                     REAL NOT NULL,
    archetype_origin            TEXT,
    inferred_client_type        TEXT NOT NULL,
    inferred_type_confidence    REAL NOT NULL,
    inferred_type_changed_at    TEXT NOT NULL,
    cached_bot_probability      REAL NOT NULL DEFAULT 0,
    cached_risk_band            TEXT,
    cached_score_updated_at     TEXT,
    ambiguity_persistence       REAL NOT NULL DEFAULT 0,
    display_name                TEXT NOT NULL DEFAULT '',
    display_name_updated_at     TEXT NOT NULL DEFAULT '',
    root_centroid               BLOB,
    root_centroid_at            TEXT,
    root_source                 TEXT
);

CREATE TABLE IF NOT EXISTS fingerprint_root_history (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
    root_centroid       BLOB NOT NULL,
    root_source         TEXT NOT NULL,
    member_count        INTEGER NOT NULL DEFAULT 1,
    set_at              TEXT NOT NULL,
    superseded_at       TEXT
);
CREATE INDEX IF NOT EXISTS ix_frh_fp_setat
    ON fingerprint_root_history(fingerprint_id, set_at DESC);

CREATE TABLE IF NOT EXISTS fingerprint_keys (
    primary_signature   TEXT PRIMARY KEY,
    fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
    first_seen          TEXT NOT NULL,
    last_seen           TEXT NOT NULL,
    hit_count           INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_fpk_fp ON fingerprint_keys(fingerprint_id);

CREATE TABLE IF NOT EXISTS fingerprint_observations (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
    vector              BLOB NOT NULL,
    observed_at         TEXT NOT NULL,
    absorbed_at         TEXT
);
CREATE INDEX IF NOT EXISTS ix_fpo_active
    ON fingerprint_observations(fingerprint_id) WHERE absorbed_at IS NULL;

CREATE TABLE IF NOT EXISTS fingerprint_corrections (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id          TEXT NOT NULL,
    primary_signature   TEXT NOT NULL,
    pass1_fingerprint   TEXT,
    pass2_fingerprint   TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
    differentiator      BLOB NOT NULL,
    observed_at         TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS identity_dimension_weights (
    id                  INTEGER PRIMARY KEY CHECK (id = 1),
    weights             BLOB NOT NULL,
    samples_used        INTEGER NOT NULL,
    clusters_used       INTEGER NOT NULL,
    archetypes_used     INTEGER NOT NULL,
    last_computed_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS identity_archetypes (
    archetype_id        TEXT PRIMARY KEY,
    name                TEXT NOT NULL,
    description         TEXT,
    centroid            BLOB NOT NULL,
    dimension_mask      BLOB NOT NULL,
    archetype_kind      TEXT NOT NULL,
    descendant_count    INTEGER NOT NULL,
    last_refined_at     TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS identity_vector_layout (
    id                  INTEGER PRIMARY KEY CHECK (id = 1),
    version             INTEGER NOT NULL,
    dimension           INTEGER NOT NULL,
    layout_json         TEXT NOT NULL,
    installed_at        TEXT NOT NULL
);

-- Per-fingerprint browser-mode rows. A fingerprint can hold several mode
-- centroids: one per request shape the same identity plays during a
-- session (navigation / xhr / sub-resource / signalr-negotiate / etc.).
-- Each row reuses the existing centroid + weights blob layout; the
-- parent fingerprints.centroid stays as the rollup (weighted mean of
-- children, recomputed on a schedule tick). See
-- docs/architecture/composite-browser-mode-fingerprints.md.
CREATE TABLE IF NOT EXISTS fingerprint_modes (
    fingerprint_id        TEXT NOT NULL REFERENCES fingerprints(fingerprint_id) ON DELETE CASCADE,
    mode_id               TEXT NOT NULL,
    centroid              BLOB NOT NULL,
    centroid_maturity     INTEGER NOT NULL,
    weights               BLOB NOT NULL,
    observation_count     INTEGER NOT NULL DEFAULT 0,
    first_seen            TEXT NOT NULL,
    last_seen             TEXT NOT NULL,
    inferred_archetype    TEXT,
    inferred_confidence   REAL,
    PRIMARY KEY (fingerprint_id, mode_id)
);
CREATE INDEX IF NOT EXISTS ix_fm_last_seen ON fingerprint_modes(last_seen DESC);

-- Append-only per-mode observation log. Mirrors fingerprint_observations
-- shape: matcher inserts a row per request (no read, no merge); the
-- FingerprintModeAbsorptionService drains unabsorbed rows on a tick,
-- computes the batched EWMA against the cached mode centroid, and
-- writes one UPSERT per (fingerprint_id, mode_id) tuple per tick.
CREATE TABLE IF NOT EXISTS fingerprint_mode_observations (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id) ON DELETE CASCADE,
    mode_id             TEXT NOT NULL,
    vector              BLOB NOT NULL,
    observed_at         TEXT NOT NULL,
    absorbed_at         TEXT
);
CREATE INDEX IF NOT EXISTS ix_fmo_active
    ON fingerprint_mode_observations(fingerprint_id, mode_id) WHERE absorbed_at IS NULL;
