-- SqliteLicenseGraceStore schema. Single-row table holding the grace-period
-- start timestamp; updated_at lets the warn-never-lock licensing logic make
-- decisions based on absolute time without trusting the wall clock alone.
-- The seed row ensures GetGraceStartedAtAsync never has to handle the
-- "table is empty" branch separately from "grace_started_at is NULL".

CREATE TABLE IF NOT EXISTS license_state (
    id               INTEGER PRIMARY KEY DEFAULT 1,
    grace_started_at INTEGER,
    updated_at       INTEGER NOT NULL
);

INSERT OR IGNORE INTO license_state (id, updated_at) VALUES (1, 0);
