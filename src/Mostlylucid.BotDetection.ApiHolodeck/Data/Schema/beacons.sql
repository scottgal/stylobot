-- BeaconStore schema. One row per HMAC canary embedded in a holodeck-served
-- fake response; ix_beacons_expires powers the sweep, ix_beacons_fingerprint
-- powers the cross-rotation lookup when BeaconContributor matches an
-- incoming replay against its original fingerprint.

CREATE TABLE IF NOT EXISTS beacons (
    canary TEXT PRIMARY KEY,
    fingerprint TEXT NOT NULL,
    path TEXT NOT NULL,
    pack_id TEXT,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_beacons_expires ON beacons(expires_at);
CREATE INDEX IF NOT EXISTS ix_beacons_fingerprint ON beacons(fingerprint);
