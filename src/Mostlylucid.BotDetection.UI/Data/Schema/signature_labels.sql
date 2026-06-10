-- SqliteSignatureLabelStore schema. Operator labels on signatures (good /
-- bad / spam etc.) with author + confidence; PRIMARY KEY (signature,
-- labeled_by) lets multiple operators label the same signature and the
-- consensus path aggregate across them.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS labels (
    signature TEXT NOT NULL,
    kind INTEGER NOT NULL,
    confidence REAL NOT NULL DEFAULT 1.0,
    labeled_by TEXT NOT NULL,
    labeled_at TEXT NOT NULL,
    note TEXT,
    PRIMARY KEY (signature, labeled_by)
);

CREATE INDEX IF NOT EXISTS idx_labels_signature ON labels(signature);
CREATE INDEX IF NOT EXISTS idx_labels_at ON labels(labeled_at DESC);
