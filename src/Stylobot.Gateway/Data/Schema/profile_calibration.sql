-- ProfileCalibrationStore schema. Gateway-local sink for the profile-analysis
-- worker: each row is one snapshot's verdict (probability + bot-type + name
-- + risk band), keyed by signature hash and path. Drives the calibration
-- view that shows operators how thresholds map to real distributions before
-- a config change is promoted.

CREATE TABLE IF NOT EXISTS profile_calibration (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    signature_hash TEXT    NOT NULL,
    bot_probability REAL   NOT NULL,
    risk_band      TEXT    NOT NULL,
    bot_type       TEXT,
    bot_name       TEXT,
    top_detector   TEXT,
    path_pattern   TEXT    NOT NULL,
    analyzed_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS idx_pc_probability ON profile_calibration(bot_probability);
CREATE INDEX IF NOT EXISTS idx_pc_analyzed_at ON profile_calibration(analyzed_at);
