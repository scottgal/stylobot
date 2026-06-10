-- SqlitePinnedEndpointStore schema. Single-table store for operator-pinned
-- endpoints: paths that are either always-on honeypots or always-exempt.
-- Method "ANY" is the wildcard; the unique index lets the upsert path
-- treat (method, path) as a natural key without race conditions.

CREATE TABLE IF NOT EXISTS pinned_endpoints (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    method     TEXT NOT NULL DEFAULT 'ANY',
    path       TEXT NOT NULL,
    is_honeypot INTEGER NOT NULL DEFAULT 0,
    note       TEXT,
    created_at INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_pinned_endpoints_method_path
    ON pinned_endpoints (method, path);
