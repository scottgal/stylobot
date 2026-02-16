-- ============================================================================
-- Stylobot PostgreSQL Comprehensive Storage Schema
-- DDD-aligned schema with GIN indexes for high-performance signature matching
-- ============================================================================

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS pg_trgm;      -- Trigram fuzzy search
CREATE EXTENSION IF NOT EXISTS btree_gin;    -- GIN indexes for btree types
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";  -- UUID generation
CREATE EXTENSION IF NOT EXISTS vector;       -- pgvector for embedding similarity search

-- ============================================================================
-- CORE SIGNATURE TABLES (Multifactor signatures with reputation)
-- ============================================================================

-- Primary multifactor signature table with GIN indexes
CREATE TABLE IF NOT EXISTS bot_signatures (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    primary_signature VARCHAR(128) NOT NULL UNIQUE,  -- HMAC(IP+UA)
    ip_signature VARCHAR(128),                       -- HMAC(IP)
    ua_signature VARCHAR(128),                       -- HMAC(UA)
    client_side_signature VARCHAR(128),              -- Browser fingerprint
    plugin_signature VARCHAR(128),                    -- Plugin/extension signature
    ip_subnet_signature VARCHAR(128),                -- IP /24 subnet signature
    factor_count INT NOT NULL DEFAULT 0,

    -- Reputation fields
    first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    total_requests BIGINT NOT NULL DEFAULT 1,
    bot_requests BIGINT NOT NULL DEFAULT 0,
    human_requests BIGINT NOT NULL DEFAULT 0,
    bot_probability DOUBLE PRECISION,
    reputation_score DOUBLE PRECISION DEFAULT 0.5,

    -- Classification
    is_known_bot BOOLEAN DEFAULT FALSE,
    bot_type VARCHAR(50),
    bot_name VARCHAR(100),
    risk_band VARCHAR(20),

    -- Extensibility
    metadata JSONB DEFAULT '{}'::jsonb,

    -- Vector embeddings for ML-based similarity search (future feature)
    -- Dimension: 384 (all-MiniLM-L6-v2), 768 (OpenAI ada-002), 1536 (text-embedding-3-small)
    signature_embedding vector(384),  -- Embedding of signature features
    behavior_embedding vector(384),   -- Embedding of request behavior patterns

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT chk_factor_count CHECK (factor_count >= 0 AND factor_count <= 6),
    CONSTRAINT chk_reputation CHECK (reputation_score >= 0 AND reputation_score <= 1)
);

-- GIN indexes for ultra-fast signature matching (supports fuzzy search)
CREATE INDEX IF NOT EXISTS idx_signatures_primary_gin
    ON bot_signatures USING GIN(primary_signature gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_signatures_ip_gin
    ON bot_signatures USING GIN(ip_signature gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_signatures_ua_gin
    ON bot_signatures USING GIN(ua_signature gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_signatures_client_gin
    ON bot_signatures USING GIN(client_side_signature gin_trgm_ops);

-- Regular indexes for exact lookups
CREATE INDEX IF NOT EXISTS idx_signatures_last_seen ON bot_signatures(last_seen DESC);
CREATE INDEX IF NOT EXISTS idx_signatures_reputation ON bot_signatures(reputation_score);
CREATE INDEX IF NOT EXISTS idx_signatures_is_known_bot ON bot_signatures(is_known_bot);
CREATE INDEX IF NOT EXISTS idx_signatures_risk_band ON bot_signatures(risk_band);

-- GIN index on metadata for flexible querying
CREATE INDEX IF NOT EXISTS idx_signatures_metadata_gin ON bot_signatures USING GIN(metadata);

-- pgvector HNSW indexes for fast similarity search (cosine distance)
CREATE INDEX IF NOT EXISTS idx_signatures_signature_embedding_hnsw
    ON bot_signatures USING hnsw (signature_embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);  -- Performance tuning params

CREATE INDEX IF NOT EXISTS idx_signatures_behavior_embedding_hnsw
    ON bot_signatures USING hnsw (behavior_embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

-- ============================================================================
-- WEIGHTS & REPUTATION TABLES
-- ============================================================================

-- Pattern reputation weights (dirty pattern tracking)
CREATE TABLE IF NOT EXISTS pattern_reputations (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pattern_signature VARCHAR(128) NOT NULL UNIQUE,
    pattern_type VARCHAR(50) NOT NULL, -- 'ip', 'ua', 'client_side', etc.

    -- Reputation tracking
    first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    occurrences BIGINT NOT NULL DEFAULT 1,
    bot_occurrences BIGINT NOT NULL DEFAULT 0,
    dirty_score DOUBLE PRECISION DEFAULT 0.0,

    -- Decay parameters
    decay_rate DOUBLE PRECISION DEFAULT 0.05,
    half_life_days INT DEFAULT 30,

    -- Classification
    is_dirty BOOLEAN DEFAULT FALSE,
    confidence DOUBLE PRECISION DEFAULT 0.5,

    -- Extensibility
    metadata JSONB DEFAULT '{}'::jsonb,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT chk_dirty_score CHECK (dirty_score >= 0 AND dirty_score <= 1),
    CONSTRAINT chk_confidence CHECK (confidence >= 0 AND confidence <= 1)
);

-- GIN index for pattern matching
CREATE INDEX IF NOT EXISTS idx_pattern_reps_signature_gin
    ON pattern_reputations USING GIN(pattern_signature gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_pattern_reps_type ON pattern_reputations(pattern_type);
CREATE INDEX IF NOT EXISTS idx_pattern_reps_dirty ON pattern_reputations(is_dirty);
CREATE INDEX IF NOT EXISTS idx_pattern_reps_score ON pattern_reputations(dirty_score DESC);

-- Detector weights (dynamic weight adjustments)
CREATE TABLE IF NOT EXISTS detector_weights (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    detector_name VARCHAR(100) NOT NULL UNIQUE,
    category VARCHAR(50) NOT NULL,
    base_weight DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    current_weight DOUBLE PRECISION NOT NULL DEFAULT 1.0,

    -- Performance metrics
    true_positives BIGINT DEFAULT 0,
    false_positives BIGINT DEFAULT 0,
    true_negatives BIGINT DEFAULT 0,
    false_negatives BIGINT DEFAULT 0,
    precision DOUBLE PRECISION,
    recall DOUBLE PRECISION,
    f1_score DOUBLE PRECISION,

    -- Adjustment parameters
    auto_adjust BOOLEAN DEFAULT TRUE,
    last_adjusted TIMESTAMPTZ,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_detector_weights_name ON detector_weights(detector_name);
CREATE INDEX IF NOT EXISTS idx_detector_weights_category ON detector_weights(category);

-- ============================================================================
-- DASHBOARD TABLES (Real-time monitoring)
-- ============================================================================

-- Detection events (for dashboard display)
CREATE TABLE IF NOT EXISTS dashboard_detections (
    id BIGSERIAL PRIMARY KEY,
    request_id VARCHAR(50) NOT NULL,
    signature_id UUID REFERENCES bot_signatures(id),

    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_bot BOOLEAN NOT NULL,
    bot_probability DOUBLE PRECISION NOT NULL,
    confidence DOUBLE PRECISION NOT NULL,
    risk_band VARCHAR(20) NOT NULL,

    -- Request details
    method VARCHAR(10) NOT NULL,
    path TEXT NOT NULL,
    status_code INT,
    processing_time_ms DOUBLE PRECISION,

    -- Client info (anonymized/hashed for GDPR)
    ip_address_hash VARCHAR(64),  -- Hashed IP
    user_agent_hash VARCHAR(64),  -- Hashed UA

    -- Classification
    bot_type VARCHAR(50),
    bot_name VARCHAR(100),
    action VARCHAR(20),
    policy_name VARCHAR(100),

    -- Evidence
    top_reasons JSONB DEFAULT '[]'::jsonb,
    detector_contributions JSONB DEFAULT '[]'::jsonb,
    important_signals JSONB,

    -- Enrichment
    primary_signature VARCHAR(100),
    country_code VARCHAR(10),
    description TEXT,
    narrative TEXT,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for fast dashboard queries
CREATE INDEX IF NOT EXISTS idx_dash_detections_timestamp ON dashboard_detections(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_dash_detections_is_bot ON dashboard_detections(is_bot);
CREATE INDEX IF NOT EXISTS idx_dash_detections_risk_band ON dashboard_detections(risk_band);
CREATE INDEX IF NOT EXISTS idx_dash_detections_signature ON dashboard_detections(signature_id);
CREATE INDEX IF NOT EXISTS idx_dash_detections_path_gin ON dashboard_detections USING GIN(path gin_trgm_ops);

-- GIN indexes for JSONB columns
CREATE INDEX IF NOT EXISTS idx_dash_detections_reasons_gin
    ON dashboard_detections USING GIN(top_reasons);
CREATE INDEX IF NOT EXISTS idx_dash_detections_contributions_gin
    ON dashboard_detections USING GIN(detector_contributions);

-- Dashboard signature observations (lightweight, for scrolling feed)
CREATE TABLE IF NOT EXISTS dashboard_signatures (
    id BIGSERIAL PRIMARY KEY,
    signature_id UUID NOT NULL REFERENCES bot_signatures(id),
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    risk_band VARCHAR(20),
    hit_count INT DEFAULT 1,
    is_known_bot BOOLEAN DEFAULT FALSE,
    bot_name VARCHAR(100)
);

CREATE INDEX IF NOT EXISTS idx_dash_signatures_timestamp ON dashboard_signatures(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_dash_signatures_signature ON dashboard_signatures(signature_id);

-- ============================================================================
-- BOT CATALOG (Known bots database)
-- ============================================================================

-- Known bot patterns
CREATE TABLE IF NOT EXISTS bot_patterns (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    pattern_type VARCHAR(50) NOT NULL,  -- 'ua_regex', 'ip_range', 'asn', etc.
    pattern_value TEXT NOT NULL,

    -- Classification
    bot_type VARCHAR(50) NOT NULL,
    bot_name VARCHAR(100),
    is_good_bot BOOLEAN DEFAULT FALSE,
    vendor VARCHAR(100),

    -- Source tracking
    source VARCHAR(100),  -- 'crawler-user-agents', 'matomo', 'custom'
    last_updated TIMESTAMPTZ,

    -- Extensibility
    metadata JSONB DEFAULT '{}'::jsonb,

    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_bot_pattern UNIQUE (pattern_type, pattern_value)
);

CREATE INDEX IF NOT EXISTS idx_bot_patterns_type ON bot_patterns(pattern_type);
CREATE INDEX IF NOT EXISTS idx_bot_patterns_bot_type ON bot_patterns(bot_type);
CREATE INDEX IF NOT EXISTS idx_bot_patterns_good_bot ON bot_patterns(is_good_bot);

-- GIN index for pattern value fuzzy matching
CREATE INDEX IF NOT EXISTS idx_bot_patterns_value_gin
    ON bot_patterns USING GIN(pattern_value gin_trgm_ops);

-- ============================================================================
-- AUDIT & LOGGING TABLES
-- ============================================================================

-- Signature change audit log
CREATE TABLE IF NOT EXISTS signature_audit_log (
    id BIGSERIAL PRIMARY KEY,
    signature_id UUID NOT NULL REFERENCES bot_signatures(id),
    change_type VARCHAR(50) NOT NULL,  -- 'created', 'updated', 'reputation_change'
    old_values JSONB,
    new_values JSONB,
    changed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    changed_by VARCHAR(100)  -- System component that made the change
);

CREATE INDEX IF NOT EXISTS idx_signature_audit_signature ON signature_audit_log(signature_id);
CREATE INDEX IF NOT EXISTS idx_signature_audit_timestamp ON signature_audit_log(changed_at DESC);

-- ============================================================================
-- FUNCTIONS & TRIGGERS
-- ============================================================================

-- Auto-update updated_at timestamp
CREATE OR REPLACE FUNCTION update_timestamp()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Apply triggers
DROP TRIGGER IF EXISTS trigger_update_bot_signatures ON bot_signatures;
CREATE TRIGGER trigger_update_bot_signatures
    BEFORE UPDATE ON bot_signatures
    FOR EACH ROW EXECUTE FUNCTION update_timestamp();

DROP TRIGGER IF EXISTS trigger_update_pattern_reps ON pattern_reputations;
CREATE TRIGGER trigger_update_pattern_reps
    BEFORE UPDATE ON pattern_reputations
    FOR EACH ROW EXECUTE FUNCTION update_timestamp();

DROP TRIGGER IF EXISTS trigger_update_detector_weights ON detector_weights;
CREATE TRIGGER trigger_update_detector_weights
    BEFORE UPDATE ON detector_weights
    FOR EACH ROW EXECUTE FUNCTION update_timestamp();

-- Function to calculate reputation score
CREATE OR REPLACE FUNCTION calculate_reputation_score(
    p_bot_requests BIGINT,
    p_total_requests BIGINT
) RETURNS DOUBLE PRECISION AS $$
BEGIN
    IF p_total_requests = 0 THEN
        RETURN 0.5;  -- Neutral score for new signatures
    END IF;
    RETURN p_bot_requests::DOUBLE PRECISION / p_total_requests::DOUBLE PRECISION;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- ============================================================================
-- VECTOR SIMILARITY SEARCH FUNCTIONS (pgvector)
-- ============================================================================

-- Find similar signatures by embedding (cosine similarity)
CREATE OR REPLACE FUNCTION find_similar_signatures(
    p_embedding vector(384),
    p_limit INT DEFAULT 10,
    p_min_similarity DOUBLE PRECISION DEFAULT 0.8
) RETURNS TABLE (
    signature_id UUID,
    primary_signature VARCHAR(128),
    similarity DOUBLE PRECISION,
    reputation_score DOUBLE PRECISION,
    is_known_bot BOOLEAN,
    bot_name VARCHAR(100)
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        id,
        s.primary_signature,
        1 - (signature_embedding <=> p_embedding) AS similarity,
        s.reputation_score,
        s.is_known_bot,
        s.bot_name
    FROM bot_signatures s
    WHERE signature_embedding IS NOT NULL
        AND (1 - (signature_embedding <=> p_embedding)) >= p_min_similarity
    ORDER BY signature_embedding <=> p_embedding
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql STABLE;

-- Find similar behavior patterns
CREATE OR REPLACE FUNCTION find_similar_behaviors(
    p_embedding vector(384),
    p_limit INT DEFAULT 10,
    p_min_similarity DOUBLE PRECISION DEFAULT 0.8
) RETURNS TABLE (
    signature_id UUID,
    primary_signature VARCHAR(128),
    similarity DOUBLE PRECISION,
    total_requests BIGINT,
    bot_probability DOUBLE PRECISION
) AS $$
BEGIN
    RETURN QUERY
    SELECT
        id,
        s.primary_signature,
        1 - (behavior_embedding <=> p_embedding) AS similarity,
        s.total_requests,
        s.bot_probability
    FROM bot_signatures s
    WHERE behavior_embedding IS NOT NULL
        AND (1 - (behavior_embedding <=> p_embedding)) >= p_min_similarity
    ORDER BY behavior_embedding <=> p_embedding
    LIMIT p_limit;
END;
$$ LANGUAGE plpgsql STABLE;

-- Batch update embeddings (for ML model integration)
CREATE OR REPLACE FUNCTION update_signature_embeddings(
    p_signature_id UUID,
    p_signature_embedding vector(384),
    p_behavior_embedding vector(384)
) RETURNS VOID AS $$
BEGIN
    UPDATE bot_signatures
    SET
        signature_embedding = p_signature_embedding,
        behavior_embedding = p_behavior_embedding,
        updated_at = NOW()
    WHERE id = p_signature_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- MAINTENANCE FUNCTIONS
-- ============================================================================

-- Function to clean up old dashboard detections
CREATE OR REPLACE FUNCTION cleanup_old_detections(p_retention_days INT)
RETURNS BIGINT AS $$
DECLARE
    deleted_count BIGINT;
BEGIN
    DELETE FROM dashboard_detections
    WHERE created_at < NOW() - (p_retention_days || ' days')::INTERVAL;

    GET DIAGNOSTICS deleted_count = ROW_COUNT;
    RETURN deleted_count;
END;
$$ LANGUAGE plpgsql;

-- Function to update signature reputation
CREATE OR REPLACE FUNCTION update_signature_reputation(
    p_signature_id UUID,
    p_is_bot BOOLEAN
) RETURNS VOID AS $$
BEGIN
    UPDATE bot_signatures
    SET
        total_requests = total_requests + 1,
        bot_requests = bot_requests + CASE WHEN p_is_bot THEN 1 ELSE 0 END,
        human_requests = human_requests + CASE WHEN NOT p_is_bot THEN 1 ELSE 0 END,
        last_seen = NOW(),
        reputation_score = calculate_reputation_score(
            bot_requests + CASE WHEN p_is_bot THEN 1 ELSE 0 END,
            total_requests + 1
        )
    WHERE id = p_signature_id;
END;
$$ LANGUAGE plpgsql;

-- ============================================================================
-- COMMENTS (Documentation)
-- ============================================================================

COMMENT ON TABLE bot_signatures IS 'Core multifactor signature storage with reputation tracking and GIN indexes for fast fuzzy matching';
COMMENT ON TABLE pattern_reputations IS 'Individual pattern reputation tracking with decay for dirty pattern detection';
COMMENT ON TABLE detector_weights IS 'Dynamic detector weight adjustments based on performance metrics';
COMMENT ON TABLE dashboard_detections IS 'Real-time detection events for dashboard monitoring (with retention policy)';
COMMENT ON TABLE dashboard_signatures IS 'Lightweight signature observations for dashboard scrolling feed';
COMMENT ON TABLE bot_patterns IS 'Known bot patterns catalog from multiple sources';
COMMENT ON TABLE signature_audit_log IS 'Audit trail for signature changes and reputation updates';

COMMENT ON INDEX idx_signatures_primary_gin IS 'Trigram GIN index enables fast fuzzy matching of primary signatures';
COMMENT ON INDEX idx_signatures_metadata_gin IS 'JSONB GIN index for flexible metadata querying';
COMMENT ON INDEX idx_signatures_signature_embedding_hnsw IS 'HNSW vector index for fast cosine similarity search on signature embeddings (replaces Qdrant)';
COMMENT ON INDEX idx_signatures_behavior_embedding_hnsw IS 'HNSW vector index for fast cosine similarity search on behavior pattern embeddings';
COMMENT ON FUNCTION calculate_reputation_score IS 'Calculates bot reputation score as ratio of bot requests to total requests';
COMMENT ON FUNCTION update_signature_reputation IS 'Atomically updates signature reputation based on new detection';
COMMENT ON FUNCTION find_similar_signatures IS 'Finds similar bot signatures using pgvector cosine similarity (ML-based matching)';
COMMENT ON FUNCTION find_similar_behaviors IS 'Finds similar behavior patterns using pgvector cosine similarity';
COMMENT ON FUNCTION update_signature_embeddings IS 'Updates signature and behavior embeddings for ML model integration';
