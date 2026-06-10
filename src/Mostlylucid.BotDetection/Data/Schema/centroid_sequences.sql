-- CentroidSequenceStore schema. One row per cluster centroid: the expected
-- content-loading chain for that behavioural class (json), the sample size
-- the chain was computed from, and when it was last refreshed. Read at
-- startup into memory; rebuilt by RebuildAsync after each clustering pass.

CREATE TABLE IF NOT EXISTS centroid_sequences (
    centroid_id   TEXT PRIMARY KEY,
    centroid_type INTEGER NOT NULL,
    sequence_json TEXT NOT NULL,
    sample_size   INTEGER NOT NULL,
    computed_at   TEXT NOT NULL
);
