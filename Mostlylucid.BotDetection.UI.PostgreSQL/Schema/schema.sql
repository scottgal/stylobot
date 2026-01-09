-- Stylobot PostgreSQL Storage Schema
-- Comprehensive storage for bot detection, signatures, reputation, and weights
-- GIN-indexed tables for fast signature lookups and fuzzy matching

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS pg_trgm;  -- Trigram fuzzy search
CREATE EXTENSION IF NOT EXISTS btree_gin;  -- GIN indexes for btree types

-- ============================================================================
-- DASHBOARD TABLES (Real-time monitoring)
-- ============================================================================

-- Detection events table
CREATE TABLE IF NOT EXISTS dashboard_detections (
    id BIGSERIAL PRIMARY KEY,
    request_id VARCHAR(50) NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_bot BOOLEAN NOT NULL,
    bot_probability DOUBLE PRECISION NOT NULL,
    confidence DOUBLE PRECISION NOT NULL,
    risk_band VARCHAR(20) NOT NULL,
    bot_type VARCHAR(50),
    bot_name VARCHAR(100),
    action VARCHAR(20),
    policy_name VARCHAR(100),
    method VARCHAR(10) NOT NULL,
    path TEXT NOT NULL,
    status_code INTEGER,
    processing_time_ms DOUBLE PRECISION,
    ip_address INET,
    user_agent TEXT,
    top_reasons JSONB,
    primary_signature VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for fast queries
CREATE INDEX IF NOT EXISTS idx_detections_timestamp ON dashboard_detections(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_detections_is_bot ON dashboard_detections(is_bot);
CREATE INDEX IF NOT EXISTS idx_detections_risk_band ON dashboard_detections(risk_band);
CREATE INDEX IF NOT EXISTS idx_detections_request_id ON dashboard_detections(request_id);
CREATE INDEX IF NOT EXISTS idx_detections_primary_signature ON dashboard_detections(primary_signature);

-- GIN index for JSONB top_reasons (fast searches within reasons)
CREATE INDEX IF NOT EXISTS idx_detections_top_reasons_gin ON dashboard_detections USING GIN(top_reasons);

-- Signature observations table with GIN indexes
CREATE TABLE IF NOT EXISTS dashboard_signatures (
    id BIGSERIAL PRIMARY KEY,
    signature_id VARCHAR(50) NOT NULL,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    primary_signature VARCHAR(100) NOT NULL,
    ip_signature VARCHAR(100),
    ua_signature VARCHAR(100),
    client_side_signature VARCHAR(100),
    factor_count INTEGER NOT NULL DEFAULT 0,
    risk_band VARCHAR(20) NOT NULL,
    hit_count INTEGER NOT NULL DEFAULT 1,
    is_known_bot BOOLEAN NOT NULL DEFAULT FALSE,
    bot_name VARCHAR(100),
    metadata JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Unique index on primary_signature (update hit_count on conflict)
CREATE UNIQUE INDEX IF NOT EXISTS idx_signatures_primary_unique
    ON dashboard_signatures(primary_signature);

-- GIN index for full-text signature search
CREATE INDEX IF NOT EXISTS idx_signatures_primary_gin
    ON dashboard_signatures USING GIN(primary_signature gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_signatures_ip_gin
    ON dashboard_signatures USING GIN(ip_signature gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_signatures_ua_gin
    ON dashboard_signatures USING GIN(ua_signature gin_trgm_ops);

-- GIN index for metadata JSONB
CREATE INDEX IF NOT EXISTS idx_signatures_metadata_gin
    ON dashboard_signatures USING GIN(metadata);

-- Regular indexes
CREATE INDEX IF NOT EXISTS idx_signatures_timestamp ON dashboard_signatures(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_signatures_risk_band ON dashboard_signatures(risk_band);
CREATE INDEX IF NOT EXISTS idx_signatures_is_known_bot ON dashboard_signatures(is_known_bot);
CREATE INDEX IF NOT EXISTS idx_signatures_hit_count ON dashboard_signatures(hit_count DESC);

-- Function to update updated_at timestamp
CREATE OR REPLACE FUNCTION update_signature_timestamp()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger to auto-update timestamp
DROP TRIGGER IF EXISTS trigger_update_signature_timestamp ON dashboard_signatures;
CREATE TRIGGER trigger_update_signature_timestamp
    BEFORE UPDATE ON dashboard_signatures
    FOR EACH ROW
    EXECUTE FUNCTION update_signature_timestamp();

-- Enable pg_trgm extension for trigram GIN indexes (fuzzy search)
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Cleanup function for retention policy
CREATE OR REPLACE FUNCTION cleanup_old_detections(retention_days INTEGER)
RETURNS INTEGER AS $$
DECLARE
    deleted_count INTEGER;
BEGIN
    WITH deleted AS (
        DELETE FROM dashboard_detections
        WHERE created_at < NOW() - (retention_days || ' days')::INTERVAL
        RETURNING 1
    )
    SELECT COUNT(*) INTO deleted_count FROM deleted;

    RETURN deleted_count;
END;
$$ LANGUAGE plpgsql;

-- Comments for documentation
COMMENT ON TABLE dashboard_detections IS 'Real-time bot detection events for dashboard display';
COMMENT ON TABLE dashboard_signatures IS 'Unique bot signatures with GIN-indexed fast lookup';
COMMENT ON COLUMN dashboard_signatures.primary_signature IS 'HMAC signature of IP+UA, GIN-indexed for fast fuzzy search';
COMMENT ON COLUMN dashboard_signatures.metadata IS 'JSONB metadata for extensibility, GIN-indexed';
COMMENT ON INDEX idx_signatures_primary_gin IS 'Trigram GIN index for fast partial signature matching';
COMMENT ON FUNCTION cleanup_old_detections IS 'Deletes detection events older than retention_days, returns count of deleted rows';
