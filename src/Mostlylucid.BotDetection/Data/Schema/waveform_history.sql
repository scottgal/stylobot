-- WaveformHistoryStore schema. Per-signature observation timeline used by
-- the BehavioralWaveformContributor for sequence-class analysis. PII guard:
-- the contributor hashes UA via xxHash64 before inserting -- ua_hash here
-- is already the hash, not the raw UA.

PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS waveform_history (
    signature TEXT NOT NULL,
    ts_ticks INTEGER NOT NULL,
    path TEXT NOT NULL,
    method TEXT NOT NULL,
    status_code INTEGER NOT NULL,
    user_agent_hash TEXT NOT NULL,
    referer_hash TEXT NOT NULL,
    content_class INTEGER NOT NULL,
    PRIMARY KEY (signature, ts_ticks)
);

CREATE INDEX IF NOT EXISTS idx_waveform_history_signature_ts
    ON waveform_history(signature, ts_ticks);
