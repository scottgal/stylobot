# TimescaleDB Integration Guide

> **Note**: TimescaleDB is an **optional enhancement** for the PostgreSQL storage provider. The bot detection system has multiple deployment options:
> - **Base**: File/SQLite storage (works out of the box, no database required)
> - **Enhanced**: PostgreSQL storage (better performance, GIN indexes, relational queries)
> - **Fully Enhanced**: PostgreSQL + TimescaleDB (time-series optimizations) ← You are here
> - **Maximum Performance**: PostgreSQL + TimescaleDB + pgvector (add ML similarity search)
>
> You can use Stylobot without any external database - PostgreSQL and TimescaleDB are performance enhancements!

## Why TimescaleDB?

**TimescaleDB** is a PostgreSQL extension optimized for time-series data. For Stylobot's event-heavy workload, it provides:

### Performance Benefits
- **100-1000x faster** dashboard queries via continuous aggregates
- **90-95% storage reduction** via automatic compression
- **Automatic data retention** with built-in policies
- **Parallel chunk processing** for faster queries

### Real-World Impact

| Scenario | Without TimescaleDB | With TimescaleDB |
|----------|---------------------|------------------|
| Dashboard summary (24h, 1M events) | ~50-200ms | <1ms |
| Time series (1h, 1-min buckets) | ~100-500ms | ~2-5ms |
| Storage (100M events/month) | ~100GB | ~10GB |
| Retention cleanup | Manual SQL job | Automatic |

## Installation

### Option 1: Docker (Recommended for Development)

```bash
docker run -d --name stylobot-timescale \
  -p 5432:5432 \
  -e POSTGRES_PASSWORD=yourpassword \
  -e POSTGRES_DB=stylobot \
  timescale/timescaledb:latest-pg16
```

### Option 2: Self-Hosted PostgreSQL

```bash
# Add TimescaleDB repository (Ubuntu/Debian)
sudo sh -c "echo 'deb https://packagecloud.io/timescale/timescaledb/ubuntu/ $(lsb_release -c -s) main' > /etc/apt/sources.list.d/timescaledb.list"
wget --quiet -O - https://packagecloud.io/timescale/timescaledb/gpgkey | sudo apt-key add -
sudo apt update

# Install TimescaleDB
sudo apt install timescaledb-2-postgresql-16

# Tune PostgreSQL for TimescaleDB
sudo timescaledb-tune

# Enable extension in database
psql -U postgres -d stylobot -c "CREATE EXTENSION IF NOT EXISTS timescaledb;"
```

### Option 3: Cloud Services

#### AWS RDS
TimescaleDB is available as an extension on RDS PostgreSQL 12+.
```sql
CREATE EXTENSION IF NOT EXISTS timescaledb;
```

#### Azure Database for PostgreSQL
TimescaleDB extension is pre-installed on Flexible Server.

#### Timescale Cloud (Managed)
Fully managed TimescaleDB: https://www.timescale.com/cloud

## Configuration

### Enable in Code

```csharp
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.PostgreSQL.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddStyloBotDashboard(...);

builder.Services.AddStyloBotPostgreSQL(
    "Host=localhost;Database=stylobot;Username=postgres;Password=pass",
    opts =>
    {
        // Enable TimescaleDB optimizations
        opts.EnableTimescaleDB = true;

        // Chunk interval (partition size)
        opts.TimescaleChunkInterval = TimeSpan.FromDays(1);  // Daily chunks

        // Compression threshold
        opts.CompressionAfter = TimeSpan.FromDays(7);  // Compress after 7 days

        // Aggregate refresh rate
        opts.AggregateRefreshInterval = TimeSpan.FromSeconds(30);  // Real-time

        // Retention
        opts.RetentionDays = 90;  // Keep 90 days max
    });
```

### What Gets Created

When `EnableTimescaleDB = true`:

1. **Hypertables**
   - `dashboard_detections` → Partitioned by `created_at`
   - `signature_audit_log` → Partitioned by `changed_at`

2. **Compression Policies**
   - Compress data older than 7 days
   - Reduces storage by 90-95%

3. **Continuous Aggregates**
   - 1-minute aggregates (refreshed every 30s)
   - 1-hour aggregates (refreshed every 5min)
   - 1-day aggregates (refreshed hourly)

4. **Retention Policies**
   - Automatic deletion of old data
   - Configurable via `RetentionDays`

5. **Helper Functions**
   - `get_dashboard_summary_fast()` - Sub-millisecond summary
   - `get_time_series_fast()` - Intelligent aggregate selection

6. **Materialized Views**
   - `dashboard_summary_24h` - Last 24h summary
   - `dashboard_risk_bands_24h` - Risk distribution

## Using Continuous Aggregates

### Fast Dashboard Summary

```csharp
// Uses pre-aggregated data (sub-millisecond query)
var summary = await connection.ExecuteAsync(
    "SELECT * FROM get_dashboard_summary_fast(@Interval)",
    new { Interval = TimeSpan.FromHours(24) });
```

### Intelligent Time Series

```csharp
// Automatically selects 1-min, 1-hour, or 1-day aggregate based on bucket size
var timeSeries = await connection.ExecuteAsync(@"
    SELECT * FROM get_time_series_fast(
        @Start, @End, @BucketSize
    )",
    new {
        Start = DateTime.UtcNow.AddHours(-24),
        End = DateTime.UtcNow,
        BucketSize = TimeSpan.FromMinutes(5)
    });
```

## Monitoring

### Check Hypertable Status

```sql
SELECT * FROM timescaledb_information.hypertables;
```

### Check Compression Stats

```sql
SELECT
    hypertable_name,
    chunk_name,
    compression_status,
    before_compression_total_bytes,
    after_compression_total_bytes,
    (1 - after_compression_total_bytes::FLOAT / before_compression_total_bytes) * 100 as compression_ratio
FROM timescaledb_information.chunks
WHERE compression_status = 'Compressed'
ORDER BY chunk_name DESC
LIMIT 10;
```

### Check Continuous Aggregate Lag

```sql
SELECT
    view_name,
    materialized_only,
    last_run_started_at,
    last_run_duration,
    total_runs,
    total_failures
FROM timescaledb_information.continuous_aggregate_stats;
```

## Performance Tuning

### Recommended Settings

```sql
-- postgresql.conf
shared_buffers = 2GB
effective_cache_size = 8GB
work_mem = 32MB
maintenance_work_mem = 512MB

# TimescaleDB specific
timescaledb.max_background_workers = 8
max_parallel_workers_per_gather = 4
```

### Chunk Size Guidelines

| Event Rate | Recommended Chunk Interval |
|------------|----------------------------|
| <1M/day | 7 days |
| 1-10M/day | 1 day (default) |
| 10-100M/day | 12 hours |
| >100M/day | 6 hours |

### Compression Trade-offs

| Compression After | CPU Overhead | Storage Savings | Query Speed |
|-------------------|--------------|-----------------|-------------|
| 1 day | High | Very High (~95%) | Fast (most recent uncompressed) |
| 7 days | Medium | High (~90%) | Fast (week of hot data) |
| 30 days | Low | Medium (~80%) | Slower (less hot data) |

## Troubleshooting

### Extension Not Found

```
ERROR: could not open extension control file ".../timescaledb.control"
```

**Solution**: Install TimescaleDB extension
```bash
sudo apt install timescaledb-2-postgresql-16
```

### Permission Denied

```
ERROR: permission denied to create extension "timescaledb"
```

**Solution**: Use superuser or rds_superuser role
```sql
GRANT rds_superuser TO your_user;  -- AWS RDS
```

### Continuous Aggregate Not Refreshing

```sql
-- Check refresh policy
SELECT * FROM timescaledb_information.jobs
WHERE job_type = 'Refresh Continuous Aggregate';

-- Manually refresh
CALL refresh_continuous_aggregate('dashboard_detections_1min', NULL, NULL);
```

## Migration from Standard PostgreSQL

If you already have data in standard PostgreSQL:

```sql
-- 1. Install TimescaleDB
CREATE EXTENSION timescaledb;

-- 2. Convert table to hypertable (migrates existing data)
SELECT create_hypertable(
    'dashboard_detections',
    'created_at',
    migrate_data => TRUE
);

-- 3. Apply compression
ALTER TABLE dashboard_detections SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'risk_band, is_bot'
);

SELECT add_compression_policy('dashboard_detections', INTERVAL '7 days');
```

## Cost Comparison

### Storage Costs (AWS RDS, 100M events/month)

| Scenario | Storage Needed | Monthly Cost (gp3) |
|----------|---------------|-------------------|
| Standard PostgreSQL | ~100GB | ~$11.50 |
| PostgreSQL + Basic Retention | ~50GB | ~$5.75 |
| **TimescaleDB + Compression** | **~10GB** | **~$1.15** |

### Compute Savings

Faster queries = smaller instances:
- Without TimescaleDB: db.r6g.xlarge (~$200/mo)
- **With TimescaleDB**: db.r6g.large (~$100/mo)

**Total savings**: ~$110/month for 100M events

## Best Practices

1. **Enable Compression** for data older than 7 days
2. **Use Continuous Aggregates** for dashboard queries
3. **Set Retention Policies** to automatically drop old data
4. **Monitor Chunk Count** (keep under 1000 chunks)
5. **Tune Chunk Interval** based on event rate
6. **Use Helper Functions** (`get_dashboard_summary_fast`, etc.)
7. **Regular Maintenance**: `VACUUM` and `ANALYZE` still apply

## pgvector Integration

The TimescaleDB Docker image also includes **pgvector** for ML-based similarity search:

```sql
-- Enable pgvector (included by default)
CREATE EXTENSION IF NOT EXISTS vector;

-- Add vector columns to existing tables
ALTER TABLE bot_signatures
ADD COLUMN signature_embedding vector(384);

-- Create HNSW index for fast similarity search
CREATE INDEX idx_signatures_embedding
ON bot_signatures USING hnsw (signature_embedding vector_cosine_ops);

-- Find similar signatures
SELECT * FROM bot_signatures
ORDER BY signature_embedding <=> '[...]'::vector
LIMIT 10;
```

### Combined Benefits

TimescaleDB + pgvector provides best-in-class storage for bot detection:

| Feature | Benefit |
|---------|---------|
| **Hypertables** | Efficient time-series event storage |
| **Continuous Aggregates** | Fast dashboard queries |
| **Compression** | 90% storage reduction |
| **pgvector** | ML-based signature similarity |
| **Single Database** | No separate vector store needed |

See [PGVECTOR_GUIDE.md](PGVECTOR_GUIDE.md) for complete pgvector documentation.

## Further Reading

- [TimescaleDB Documentation](https://docs.timescale.com/)
- [Best Practices Guide](https://docs.timescale.com/timescaledb/latest/best-practices/)
- [Compression Guide](https://docs.timescale.com/timescaledb/latest/how-to-guides/compression/)
- [Continuous Aggregates](https://docs.timescale.com/timescaledb/latest/how-to-guides/continuous-aggregates/)
- [pgvector Documentation](https://github.com/pgvector/pgvector)
- [Stylobot pgvector Guide](PGVECTOR_GUIDE.md)
