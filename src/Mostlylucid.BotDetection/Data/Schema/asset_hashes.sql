-- AssetHashStore schema. Tracks the content fingerprint of static assets so
-- the centroid-sequence layer can mark a path "stale" the moment a deploy
-- changes its bytes. Avoids false-positive divergence alarms during the
-- post-deploy window where every visitor sees a new hash for the same path.

CREATE TABLE IF NOT EXISTS asset_hashes (
    path         TEXT PRIMARY KEY,
    hash         TEXT NOT NULL,
    changed_at   TEXT,
    last_seen    TEXT NOT NULL
);
