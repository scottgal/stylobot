-- StyloBotUserStore schema. ASP.NET Core Identity user table plus the
-- dashboard_api_keys table backing the X-SB-Api-Key tier-2 auth path.
-- Two unique indexes on dashboard_users let Identity's
-- FindByNameAsync / FindByEmailAsync paths use index scans rather than
-- full-table on every login.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS dashboard_users (
    id TEXT PRIMARY KEY,
    username TEXT NOT NULL,
    normalized_username TEXT NOT NULL,
    email TEXT NOT NULL,
    normalized_email TEXT NOT NULL,
    email_confirmed INTEGER NOT NULL DEFAULT 0,
    password_hash TEXT,
    security_stamp TEXT NOT NULL DEFAULT '',
    concurrency_stamp TEXT NOT NULL DEFAULT '',
    phone_number TEXT,
    phone_number_confirmed INTEGER NOT NULL DEFAULT 0,
    two_factor_enabled INTEGER NOT NULL DEFAULT 0,
    lockout_end TEXT,
    lockout_enabled INTEGER NOT NULL DEFAULT 0,
    access_failed_count INTEGER NOT NULL DEFAULT 0,
    authenticator_key TEXT,
    recovery_codes TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_users_norm_username ON dashboard_users(normalized_username);
CREATE UNIQUE INDEX IF NOT EXISTS idx_users_norm_email ON dashboard_users(normalized_email);

CREATE TABLE IF NOT EXISTS dashboard_api_keys (
    id TEXT PRIMARY KEY,
    key_hash TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    scope TEXT NOT NULL DEFAULT 'read',
    created_at TEXT NOT NULL,
    last_used_at TEXT,
    expires_at TEXT,
    created_by TEXT
);
