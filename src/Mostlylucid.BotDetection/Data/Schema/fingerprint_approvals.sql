-- FingerprintApprovalStore schema. Operator-driven approval flow:
--   fingerprint_approvals  -- which signatures have been approved, with the
--                             locked dimensions (json) preserved at approval
--                             time + the revocation column for un-approve.
--   approval_tokens        -- single-use one-time tokens issued by the
--                             dashboard, consumed when the operator confirms
--                             the binding; idx_tokens_expires powers the
--                             sweep that drops stale unconsumed rows.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA cache_size=-2000;

CREATE TABLE IF NOT EXISTS fingerprint_approvals (
    signature TEXT PRIMARY KEY,
    locked_dimensions_json TEXT NOT NULL DEFAULT '{}',
    justification TEXT NOT NULL,
    approved_by TEXT NOT NULL,
    approved_at TEXT NOT NULL,
    expires_at TEXT,
    revoked_at TEXT,
    revoked_by TEXT
);

CREATE TABLE IF NOT EXISTS approval_tokens (
    token TEXT PRIMARY KEY,
    signature TEXT NOT NULL,
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    consumed INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_tokens_signature ON approval_tokens(signature);
CREATE INDEX IF NOT EXISTS idx_tokens_expires ON approval_tokens(expires_at);
