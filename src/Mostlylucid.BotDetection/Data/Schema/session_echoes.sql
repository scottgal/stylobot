-- Session accounting log: one row per session that reaches TTL / pressure
-- eviction on Orchestration.Sessions.SessionStore. Written by SessionEchoAtom
-- (subscribes to the two-phase finalize signal). Always populated — this is
-- the "session S existed" trail. Higher-detail enrichment (Markov vector,
-- full request history) lives on the sessions table (opportunistic, priority-
-- gated) and cross-references by fingerprint + time window.

CREATE TABLE IF NOT EXISTS session_echoes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    fingerprint_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    started_at TEXT NOT NULL,           -- ISO-8601 UTC
    ended_at TEXT NOT NULL,             -- ISO-8601 UTC
    sample_count INTEGER NOT NULL,
    mean_bot_probability REAL NOT NULL,
    max_bot_probability REAL NOT NULL,
    latest_confidence REAL NOT NULL,
    honeypot_hits INTEGER NOT NULL,
    upstream_status_counts TEXT NOT NULL,  -- JSON blob: {"404": 3, "500": 1}
    dominant_client_type TEXT,             -- nullable — not always inferred
    retention_priority REAL NOT NULL,
    finalize_reason INTEGER NOT NULL,      -- SessionFinalizeReason enum (Ttl=0, MaxLifetime=1, Pressure=2, Explicit=3)
    echoed_at TEXT NOT NULL                -- ISO-8601 UTC when the echo was materialised
);

-- Drill-in from a fingerprint detail view: "what sessions did this fp have?".
CREATE INDEX IF NOT EXISTS ix_session_echoes_fp
    ON session_echoes(fingerprint_id, echoed_at DESC);

-- Recent-echoes page for the dashboard, per site.
CREATE INDEX IF NOT EXISTS ix_session_echoes_site_time
    ON session_echoes(site_id, echoed_at DESC);

-- Filter for pressure / honeypot slices when operators audit shed decisions.
CREATE INDEX IF NOT EXISTS ix_session_echoes_reason
    ON session_echoes(finalize_reason, echoed_at DESC);
