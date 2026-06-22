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
    root_source                 TEXT,
    -- Persistent trust state (gap analysis 2026-06-15, Gap #4).
    -- claim_status: 'unverified' | 'verified' | 'spoofed' | 'behaviourally-trusted'.
    -- The CLAIMED identity's verification state; the request-path verifier
    -- contributors read claim_status + verified_at to short-circuit
    -- re-verification when still within TrustOptions.TrustCacheTtl.
    -- verification_method records HOW the claim was verified
    -- ('ip_range', 'fcrdns', 'forward_dns', 'nodeinfo', 'behavioural-trust', ...);
    -- NULL when unverified.
    -- verified_at is the ISO-8601 UTC timestamp of first successful verification.
    -- trust_observations counts how many requests have matched the claimed
    -- identity's expected behaviour (incremented separately by Gap #5 work).
    claim_status                TEXT NOT NULL DEFAULT 'unverified',
    verification_method         TEXT,
    verified_at                 TEXT,
    trust_observations          INTEGER NOT NULL DEFAULT 0
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

-- Per-fingerprint display-name change history. Append-only snapshot table; one
-- row per name transition. Written under the single write gate
-- (SqliteFingerprintStore.UpdateDisplayNameAsync) so every rename — matcher
-- recompose, LLM rename, future operator rename — leaves a record. Read by the
-- signature timeline view on demand; NOT denormalised onto the Fingerprint
-- record (snapshots stay cold, the LFU dict owns only the current name).
CREATE TABLE IF NOT EXISTS fingerprint_name_history (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint_id  TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
    old_name        TEXT,
    new_name        TEXT NOT NULL,
    source          TEXT NOT NULL,  -- 'matcher' | 'llm' | 'operator'
    changed_at      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_fnh_fp_changedat
    ON fingerprint_name_history(fingerprint_id, changed_at DESC);

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
    archetype_id            TEXT PRIMARY KEY,
    name                    TEXT NOT NULL,
    description             TEXT,
    centroid                BLOB NOT NULL,
    dimension_mask          BLOB NOT NULL,
    archetype_kind          TEXT NOT NULL,
    descendant_count        INTEGER NOT NULL,
    last_refined_at         TEXT NOT NULL,
    -- Phase 3 umbrella shrinkage (2026-06-21). Multiplier on the matcher's
    -- per-dim variance: 1.0 = no narrowing, below 1.0 = catchment tightened
    -- by calibration. Floor at 0.05 enforced by the writer; the column NOT
    -- NULL DEFAULT 1.0 keeps a hand-inserted row matcher-compatible.
    variance_multiplier     REAL NOT NULL DEFAULT 1.0,
    -- 2026-06-22 -- discriminator letting this one table hold both classic
    -- identity archetypes ('identity') and per-mode centroids ('browser_mode')
    -- without forking the calibration / refinement / drainer infrastructure.
    -- See docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md
    -- and ModeCentroidCatalogue (T11b). Default 'identity' is correct for every
    -- pre-existing row -- they're all classic identity archetypes; mode-centroid
    -- inserts specify 'browser_mode' explicitly.
    catalogue_kind          TEXT NOT NULL DEFAULT 'identity'
);
CREATE INDEX IF NOT EXISTS ix_identity_archetypes_catalogue_kind
    ON identity_archetypes (catalogue_kind);

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

-- Per-archetype, per-observed-UA-family drift metrics. Written by
-- IdentityWeightCalibrationService.RunOnceAsync after centroid refinement.
-- A snapshot of "how close are this archetype's descendants to its centroid,
-- broken down by what UA the descendant claimed". See
-- docs/superpowers/specs/2026-06-21-adaptive-learning-loop-design.md §2.B.
--
-- Composite PK on (archetype_id, ua_family, calibrated_at) so back-to-back
-- calibrations write distinct rows -- the dashboard can show a drift curve.
-- matches_asserted_ua is INTEGER 0/1; SQLite has no native BOOLEAN.
CREATE TABLE IF NOT EXISTS archetype_drift_metrics (
    archetype_id            TEXT NOT NULL,
    ua_family               TEXT NOT NULL,
    matches_asserted_ua     INTEGER NOT NULL,
    descendant_count        INTEGER NOT NULL,
    mean_l2_to_centroid     REAL NOT NULL,
    variance_l2_to_centroid REAL NOT NULL,
    p90_l2_to_centroid      REAL NOT NULL,
    calibrated_at           TEXT NOT NULL,
    PRIMARY KEY (archetype_id, ua_family, calibrated_at)
);
CREATE INDEX IF NOT EXISTS ix_adm_calibrated_at
    ON archetype_drift_metrics(calibrated_at DESC);
CREATE INDEX IF NOT EXISTS ix_adm_archetype_id
    ON archetype_drift_metrics(archetype_id);
