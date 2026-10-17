-- Analytics indexes for SqliteDashboardEventStore. Split from
-- dashboard_events.sql because they depend on columns added by the
-- in-code ALTER TABLE migration loop (domain, referrer_host,
-- ua_device_class). Run after the migration step so the columns are
-- guaranteed to exist; idempotent via IF NOT EXISTS.

CREATE INDEX IF NOT EXISTS idx_det_domain_ts ON detections(domain, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_det_domain_path_ts ON detections(domain, path, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_det_domain_bot_name_ts ON detections(domain, bot_name, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_det_domain_country_ts ON detections(domain, country_code, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_det_domain_referrer_host_ts ON detections(domain, referrer_host, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_det_domain_ua_device_class_ts ON detections(domain, ua_device_class, timestamp DESC);

-- Compression-fold drain order: the fold scans the aged region in ascending
-- (importance_weight, timestamp) so the least-important rows fold first and the
-- LIMIT-batched pass terminates early. Depends on the importance_weight column
-- added by the in-code ALTER TABLE migration loop; idempotent via IF NOT EXISTS.
CREATE INDEX IF NOT EXISTS idx_det_compression ON detections(importance_weight, timestamp);
