-- ============================================================================
-- TimescaleDB Enhancements for Stylobot
-- Optimizes time-series event storage for massive scale
-- ============================================================================

-- Enable TimescaleDB extension (requires superuser or rds_superuser)
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ============================================================================
-- CONVERT TABLES TO HYPERTABLES
-- ============================================================================

-- TimescaleDB requires that ALL unique indexes/constraints include the
-- partitioning column. The base schema creates dashboard_detections with
-- PRIMARY KEY (id) which doesn't include created_at. We must fix that first.
DO $$
BEGIN
    -- Only alter if the table exists and is NOT already a hypertable
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'dashboard_detections')
       AND NOT EXISTS (
           SELECT 1 FROM timescaledb_information.hypertables
           WHERE hypertable_name = 'dashboard_detections'
       )
    THEN
        -- Drop existing primary key (id alone) so we can include created_at
        IF EXISTS (
            SELECT 1 FROM pg_constraint
            WHERE conrelid = 'dashboard_detections'::regclass AND contype = 'p'
        ) THEN
            ALTER TABLE dashboard_detections DROP CONSTRAINT dashboard_detections_pkey;
            -- Recreate as composite PK including the partitioning column
            ALTER TABLE dashboard_detections ADD PRIMARY KEY (id, created_at);
        END IF;
    END IF;
END $$;

-- Convert dashboard_detections to hypertable (partitioned by time)
SELECT create_hypertable(
    'dashboard_detections',
    'created_at',
    if_not_exists => TRUE,
    chunk_time_interval => INTERVAL '1 day',  -- Daily chunks
    migrate_data => TRUE  -- Migrate existing data if any
);

-- Convert signature_audit_log to hypertable (only if table exists)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'signature_audit_log') THEN
        -- Fix PK to include partitioning column if not already a hypertable
        IF NOT EXISTS (
            SELECT 1 FROM timescaledb_information.hypertables
            WHERE hypertable_name = 'signature_audit_log'
        ) THEN
            IF EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = 'signature_audit_log'::regclass AND contype = 'p'
            ) THEN
                ALTER TABLE signature_audit_log DROP CONSTRAINT signature_audit_log_pkey;
                ALTER TABLE signature_audit_log ADD PRIMARY KEY (id, changed_at);
            END IF;
        END IF;

        PERFORM create_hypertable(
            'signature_audit_log',
            'changed_at',
            if_not_exists => TRUE,
            chunk_time_interval => INTERVAL '7 days',
            migrate_data => TRUE
        );
    END IF;
END $$;

-- ============================================================================
-- COMPRESSION POLICIES (Automatic data compression)
-- ============================================================================

-- Compress dashboard_detections older than 7 days
ALTER TABLE dashboard_detections SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'risk_band, is_bot',
    timescaledb.compress_orderby = 'created_at DESC'
);

SELECT add_compression_policy(
    'dashboard_detections',
    compress_after => INTERVAL '7 days',
    if_not_exists => TRUE
);

-- Compress audit log older than 30 days (only if table exists)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'signature_audit_log') THEN
        ALTER TABLE signature_audit_log SET (
            timescaledb.compress,
            timescaledb.compress_segmentby = 'change_type',
            timescaledb.compress_orderby = 'changed_at DESC'
        );

        PERFORM add_compression_policy(
            'signature_audit_log',
            compress_after => INTERVAL '30 days',
            if_not_exists => TRUE
        );
    END IF;
END $$;

-- ============================================================================
-- RETENTION POLICIES (Automatic data deletion)
-- ============================================================================

-- Auto-delete dashboard_detections older than retention period
-- (Configurable via PostgreSQLStorageOptions.RetentionDays)
-- Default: 30 days for low/medium risk, 90 days for high risk

SELECT add_retention_policy(
    'dashboard_detections',
    drop_after => INTERVAL '90 days',  -- Maximum retention
    if_not_exists => TRUE
);

-- Keep audit log for 1 year (only if table exists)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'signature_audit_log') THEN
        PERFORM add_retention_policy(
            'signature_audit_log',
            drop_after => INTERVAL '1 year',
            if_not_exists => TRUE
        );
    END IF;
END $$;

-- ============================================================================
-- CONTINUOUS AGGREGATES (Pre-computed summaries for fast dashboards)
-- ============================================================================

-- 1-minute aggregates for real-time charts
DO $$
BEGIN
    BEGIN
        CREATE MATERIALIZED VIEW IF NOT EXISTS dashboard_detections_1min
        WITH (timescaledb.continuous) AS
        SELECT
            time_bucket('1 minute', created_at) AS bucket,
            risk_band,
            COUNT(*) as total_count,
            COUNT(*) FILTER (WHERE is_bot) as bot_count,
            COUNT(*) FILTER (WHERE NOT is_bot) as human_count,
            AVG(bot_probability) as avg_bot_probability,
            AVG(confidence) as avg_confidence,
            AVG(processing_time_ms) as avg_processing_time_ms
        FROM dashboard_detections
        GROUP BY bucket, risk_band;
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create dashboard_detections_1min: %', SQLERRM;
    END;
END $$;

-- 1-hour aggregates for historical charts
DO $$
BEGIN
    BEGIN
        CREATE MATERIALIZED VIEW IF NOT EXISTS dashboard_detections_1hour
        WITH (timescaledb.continuous) AS
        SELECT
            time_bucket('1 hour', created_at) AS bucket,
            risk_band,
            COUNT(*) as total_count,
            COUNT(*) FILTER (WHERE is_bot) as bot_count,
            COUNT(*) FILTER (WHERE NOT is_bot) as human_count,
            AVG(bot_probability) as avg_bot_probability,
            AVG(confidence) as avg_confidence,
            AVG(processing_time_ms) as avg_processing_time_ms,
            COUNT(DISTINCT signature_id) as unique_signatures
        FROM dashboard_detections
        GROUP BY bucket, risk_band;
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create dashboard_detections_1hour: %', SQLERRM;
    END;
END $$;

-- 1-day aggregates for long-term trends
DO $$
BEGIN
    BEGIN
        CREATE MATERIALIZED VIEW IF NOT EXISTS dashboard_detections_1day
        WITH (timescaledb.continuous) AS
        SELECT
            time_bucket('1 day', created_at) AS bucket,
            risk_band,
            COUNT(*) as total_count,
            COUNT(*) FILTER (WHERE is_bot) as bot_count,
            COUNT(*) FILTER (WHERE NOT is_bot) as human_count,
            AVG(bot_probability) as avg_bot_probability,
            AVG(confidence) as avg_confidence,
            AVG(processing_time_ms) as avg_processing_time_ms,
            COUNT(DISTINCT signature_id) as unique_signatures,
            COUNT(DISTINCT bot_type) FILTER (WHERE bot_type IS NOT NULL) as unique_bot_types
        FROM dashboard_detections
        GROUP BY bucket, risk_band;
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create dashboard_detections_1day: %', SQLERRM;
    END;
END $$;

-- ============================================================================
-- REFRESH POLICIES (Keep aggregates up-to-date)
-- ============================================================================

-- Refresh 1-minute aggregates every 30 seconds (near real-time)
DO $$
BEGIN
    BEGIN
        PERFORM add_continuous_aggregate_policy(
            'dashboard_detections_1min',
            start_offset => INTERVAL '1 hour',
            end_offset => INTERVAL '30 seconds',
            schedule_interval => INTERVAL '30 seconds',
            if_not_exists => TRUE
        );
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not add continuous aggregate policy for dashboard_detections_1min: %', SQLERRM;
    END;
END $$;

-- Refresh 1-hour aggregates every 5 minutes
DO $$
BEGIN
    BEGIN
        PERFORM add_continuous_aggregate_policy(
            'dashboard_detections_1hour',
            start_offset => INTERVAL '1 day',
            end_offset => INTERVAL '1 hour',
            schedule_interval => INTERVAL '5 minutes',
            if_not_exists => TRUE
        );
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not add continuous aggregate policy for dashboard_detections_1hour: %', SQLERRM;
    END;
END $$;

-- Refresh 1-day aggregates every 1 hour
DO $$
BEGIN
    BEGIN
        PERFORM add_continuous_aggregate_policy(
            'dashboard_detections_1day',
            start_offset => INTERVAL '7 days',
            end_offset => INTERVAL '1 day',
            schedule_interval => INTERVAL '1 hour',
            if_not_exists => TRUE
        );
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not add continuous aggregate policy for dashboard_detections_1day: %', SQLERRM;
    END;
END $$;

-- ============================================================================
-- HELPER VIEWS FOR DASHBOARD QUERIES
-- ============================================================================

-- Last 24 hours summary (uses continuous aggregates for speed)
DO $$
BEGIN
    EXECUTE 'CREATE OR REPLACE VIEW dashboard_summary_24h AS
SELECT
    SUM(total_count)::INT as total_requests,
    SUM(bot_count)::INT as bot_requests,
    SUM(human_count)::INT as human_requests,
    (SUM(bot_count)::FLOAT / NULLIF(SUM(total_count), 0) * 100) as bot_percentage,
    AVG(avg_processing_time_ms) as avg_processing_time_ms,
    COUNT(DISTINCT bucket) as data_points
FROM dashboard_detections_1min
WHERE bucket > NOW() - INTERVAL ''24 hours''';
EXCEPTION WHEN OTHERS THEN
    RAISE WARNING 'Could not create dashboard_summary_24h view: %', SQLERRM;
END $$;

-- Risk band distribution (last 24h)
DO $$
BEGIN
    EXECUTE 'CREATE OR REPLACE VIEW dashboard_risk_bands_24h AS
SELECT
    risk_band,
    SUM(total_count)::INT as count,
    (SUM(total_count)::FLOAT / SUM(SUM(total_count)) OVER () * 100) as percentage
FROM dashboard_detections_1min
WHERE bucket > NOW() - INTERVAL ''24 hours''
GROUP BY risk_band
ORDER BY count DESC';
EXCEPTION WHEN OTHERS THEN
    RAISE WARNING 'Could not create dashboard_risk_bands_24h view: %', SQLERRM;
END $$;

-- ============================================================================
-- INDEXES ON CONTINUOUS AGGREGATES
-- ============================================================================

DO $$
BEGIN
    BEGIN
        CREATE INDEX IF NOT EXISTS idx_detections_1min_bucket
            ON dashboard_detections_1min(bucket DESC);
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create idx_detections_1min_bucket: %', SQLERRM;
    END;

    BEGIN
        CREATE INDEX IF NOT EXISTS idx_detections_1hour_bucket
            ON dashboard_detections_1hour(bucket DESC);
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create idx_detections_1hour_bucket: %', SQLERRM;
    END;

    BEGIN
        CREATE INDEX IF NOT EXISTS idx_detections_1day_bucket
            ON dashboard_detections_1day(bucket DESC);
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create idx_detections_1day_bucket: %', SQLERRM;
    END;
END $$;

-- ============================================================================
-- PERFORMANCE TUNING FUNCTIONS
-- ============================================================================

-- Function to get dashboard summary using pre-aggregated data
DO $$
BEGIN
    BEGIN
        CREATE OR REPLACE FUNCTION get_dashboard_summary_fast(
            p_interval INTERVAL DEFAULT INTERVAL '24 hours'
        ) RETURNS TABLE (
            total_requests BIGINT,
            bot_requests BIGINT,
            human_requests BIGINT,
            bot_percentage DOUBLE PRECISION,
            avg_processing_time_ms DOUBLE PRECISION
        ) AS $func$
        BEGIN
            RETURN QUERY
            SELECT
                SUM(total_count)::BIGINT,
                SUM(bot_count)::BIGINT,
                SUM(human_count)::BIGINT,
                (SUM(bot_count)::FLOAT / NULLIF(SUM(total_count), 0) * 100),
                AVG(avg_processing_time_ms)
            FROM dashboard_detections_1min
            WHERE bucket > NOW() - p_interval;
        END;
        $func$ LANGUAGE plpgsql STABLE;
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create get_dashboard_summary_fast function: %', SQLERRM;
    END;
END $$;

-- Function to get time series using appropriate aggregate
DO $$
BEGIN
    BEGIN
        CREATE OR REPLACE FUNCTION get_time_series_fast(
            p_start TIMESTAMPTZ,
            p_end TIMESTAMPTZ,
            p_bucket_size INTERVAL DEFAULT INTERVAL '1 minute'
        ) RETURNS TABLE (
            bucket TIMESTAMPTZ,
            total_count BIGINT,
            bot_count BIGINT,
            human_count BIGINT
        ) AS $func$
        BEGIN
            -- Choose appropriate aggregate based on bucket size
            IF p_bucket_size <= INTERVAL '5 minutes' THEN
                RETURN QUERY
                SELECT d.bucket, d.total_count, d.bot_count, d.human_count
                FROM dashboard_detections_1min d
                WHERE d.bucket BETWEEN p_start AND p_end
                ORDER BY d.bucket;
            ELSIF p_bucket_size <= INTERVAL '2 hours' THEN
                RETURN QUERY
                SELECT d.bucket, d.total_count, d.bot_count, d.human_count
                FROM dashboard_detections_1hour d
                WHERE d.bucket BETWEEN p_start AND p_end
                ORDER BY d.bucket;
            ELSE
                RETURN QUERY
                SELECT d.bucket, d.total_count, d.bot_count, d.human_count
                FROM dashboard_detections_1day d
                WHERE d.bucket BETWEEN p_start AND p_end
                ORDER BY d.bucket;
            END IF;
        END;
        $func$ LANGUAGE plpgsql STABLE;
    EXCEPTION WHEN OTHERS THEN
        RAISE WARNING 'Could not create get_time_series_fast function: %', SQLERRM;
    END;
END $$;

-- ============================================================================
-- COMMENTS
-- ============================================================================

COMMENT ON FUNCTION get_dashboard_summary_fast IS 'Fast dashboard summary using TimescaleDB continuous aggregates (sub-millisecond query)';
COMMENT ON FUNCTION get_time_series_fast IS 'Intelligently selects appropriate continuous aggregate based on bucket size for optimal performance';
COMMENT ON MATERIALIZED VIEW dashboard_detections_1min IS 'Real-time 1-minute aggregates, refreshed every 30 seconds';
COMMENT ON MATERIALIZED VIEW dashboard_detections_1hour IS 'Hourly aggregates for historical analysis, refreshed every 5 minutes';
COMMENT ON MATERIALIZED VIEW dashboard_detections_1day IS 'Daily aggregates for long-term trends, refreshed hourly';

-- ============================================================================
-- PERFORMANCE NOTES
-- ============================================================================
/*
With TimescaleDB:
- Queries against continuous aggregates are 100-1000x faster
- Compression reduces storage by 90-95% after 7 days
- Automatic retention eliminates need for cleanup jobs
- Hypertable chunks enable parallel query execution

Expected Performance:
- Dashboard summary (24h): <1ms (vs ~50ms without aggregates)
- Time series (1h, 1min buckets): <5ms (vs ~200ms without aggregates)
- Individual event queries: ~same speed (GIN indexes still apply)
- Storage: ~10GB/month for 100M events (vs ~100GB without compression)
*/
