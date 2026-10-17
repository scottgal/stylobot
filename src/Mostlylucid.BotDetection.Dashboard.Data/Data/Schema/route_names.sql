-- SqliteRouteNameStore schema. Operator-supplied friendly names for routes:
-- the dashboard renders "GET /api/v1/users" as e.g. "List users" once a
-- name is set. The route_key is the canonical "{METHOD} {path}" form.

CREATE TABLE IF NOT EXISTS route_names (
    route_key     TEXT PRIMARY KEY,
    friendly_name TEXT NOT NULL,
    notes         TEXT,
    updated_utc   TEXT NOT NULL,
    updated_by    TEXT
);
