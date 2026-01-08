# Docker Setup for Stylobot with TimescaleDB

> **Note**: Docker with PostgreSQL/TimescaleDB is **optional**! Stylobot works perfectly with:
> - **Default**: File/SQLite storage (no Docker required)
> - **Enhanced**: PostgreSQL (this guide)
> - **Optimized**: + TimescaleDB (this guide)
> - **ML-Powered**: + pgvector (this guide)
>
> Use this Docker setup only if you want enhanced performance and scale!

## Quick Start

```bash
# Start everything (TimescaleDB + Stylobot Dashboard)
docker-compose up -d

# View logs
docker-compose logs -f

# Access dashboard
# http://localhost:5000/stylobot
```

## Services

### TimescaleDB
- **Image**: [`timescale/timescaledb:latest-pg16`](https://hub.docker.com/r/timescale/timescaledb)
- **Port**: `5432` (PostgreSQL)
- **Database**: `stylobot`
- **User**: `stylobot`
- **Password**: `stylobot_secure_password_change_me` ⚠️ **CHANGE THIS!**
- **Extensions**: Includes TimescaleDB + **pgvector** (for ML-based similarity search)

### Stylobot Demo
- **Port**: `5000` (HTTP)
- **Dashboard**: `http://localhost:5000/stylobot`
- **Storage**: Automatically uses TimescaleDB
- **Auto-initialization**: Schema creates on first run

## Configuration

The docker-compose file includes:

### TimescaleDB Tuning
- `shared_buffers=512MB` - Memory for caching
- `effective_cache_size=2GB` - Query planner memory estimate
- `max_connections=200` - Concurrent connections
- `work_mem=32MB` - Per-operation memory
- `maintenance_work_mem=256MB` - Maintenance operations
- `timescaledb.max_background_workers=8` - Parallel workers

### Stylobot Dashboard Settings
- `EnableTimescaleDB=true` - Enables hypertables, compression, aggregates
- `AutoInitializeSchema=true` - Auto-creates tables on startup
- `RetentionDays=90` - Keep 90 days of data
- `CompressionAfterDays=7` - Compress data older than 7 days

## Persistent Storage

Docker volumes are created for:
- `timescale-data` - TimescaleDB data (PostgreSQL)
- `stylobot-db` - SQLite (for AOT console fallback)
- `stylobot-learning` - YARP learning data
- `stylobot-geoip` - GeoIP database

## Healthchecks

### TimescaleDB
- Command: `pg_isready -U stylobot -d stylobot`
- Interval: 10s
- Retries: 5
- Start period: 30s

### Stylobot Demo
- Command: `curl -f http://localhost:5000/health`
- Interval: 30s
- Retries: 3
- Start period: 10s

The demo app waits for TimescaleDB to be healthy before starting.

## Production Setup

For production, update these settings:

### 1. Change Passwords

```yaml
environment:
  - POSTGRES_PASSWORD=your_super_secure_password_here

  - StyloBotDashboard__PostgreSQL__ConnectionString=Host=timescaledb;Database=stylobot;Username=stylobot;Password=your_super_secure_password_here
```

### 2. Use Environment File

Create `.env`:
```env
POSTGRES_PASSWORD=your_secure_password
STYLOBOT_POSTGRES_PASSWORD=your_secure_password
```

Update docker-compose.yml:
```yaml
environment:
  - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
  - StyloBotDashboard__PostgreSQL__ConnectionString=Host=timescaledb;Database=stylobot;Username=stylobot;Password=${STYLOBOT_POSTGRES_PASSWORD}
```

### 3. Tune for Your Traffic

For **high traffic** (>10M events/day):
```yaml
command:
  - postgres
  - -c
  - shared_buffers=2GB
  - -c
  - effective_cache_size=8GB
  - -c
  - work_mem=64MB
  - -c
  - timescaledb.max_background_workers=16
```

For **low traffic** (<1M events/day):
```yaml
command:
  - postgres
  - -c
  - shared_buffers=256MB
  - -c
  - effective_cache_size=1GB
  - -c
  - work_mem=16MB
  - -c
  - timescaledb.max_background_workers=4
```

### 4. Enable SSL/TLS

```yaml
volumes:
  - ./certs/server.crt:/var/lib/postgresql/server.crt:ro
  - ./certs/server.key:/var/lib/postgresql/server.key:ro
command:
  - postgres
  - -c
  - ssl=on
  - -c
  - ssl_cert_file=/var/lib/postgresql/server.crt
  - -c
  - ssl_key_file=/var/lib/postgresql/server.key
```

## Management

### Access PostgreSQL Shell

```bash
docker exec -it stylobot-timescale psql -U stylobot -d stylobot
```

### Check TimescaleDB Status

```sql
SELECT * FROM timescaledb_information.hypertables;
SELECT * FROM timescaledb_information.continuous_aggregates;
SELECT * FROM timescaledb_information.compression_settings;
```

### Check Data Size

```sql
SELECT
    hypertable_name,
    pg_size_pretty(total_bytes) as total_size,
    pg_size_pretty(table_bytes) as table_size,
    pg_size_pretty(index_bytes) as index_size
FROM timescaledb_information.hypertable_detailed_size
ORDER BY total_bytes DESC;
```

### Manual Backup

```bash
# Backup
docker exec stylobot-timescale pg_dump -U stylobot stylobot > stylobot_backup.sql

# Restore
docker exec -i stylobot-timescale psql -U stylobot stylobot < stylobot_backup.sql
```

## Troubleshooting

### TimescaleDB Won't Start

Check logs:
```bash
docker logs stylobot-timescale
```

Common issues:
- Port 5432 already in use: Change to `5433:5432`
- Insufficient memory: Reduce `shared_buffers`
- Permission denied: Check volume permissions

### Dashboard Not Connecting

1. Check TimescaleDB is healthy:
```bash
docker-compose ps
```

2. Check connection string:
```bash
docker exec stylobot-demo env | grep PostgreSQL
```

3. Test connection manually:
```bash
docker exec stylobot-demo pg_isready -h timescaledb -U stylobot
```

### Schema Not Initializing

Enable debug logging:
```yaml
environment:
  - Logging__LogLevel__Default=Debug
  - Logging__LogLevel__Mostlylucid.BotDetection.UI.PostgreSQL=Debug
```

Check logs:
```bash
docker-compose logs stylobot-demo | grep -i "schema\|timescale"
```

## Monitoring

### Prometheus Metrics

TimescaleDB exposes metrics via `pg_stat_statements`:

```sql
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

SELECT
    query,
    calls,
    mean_exec_time,
    max_exec_time
FROM pg_stat_statements
ORDER BY mean_exec_time DESC
LIMIT 10;
```

### Container Stats

```bash
docker stats stylobot-timescale stylobot-demo
```

## Scaling

### Horizontal Scaling

For multi-instance deployments:

```yaml
services:
  stylobot-demo:
    deploy:
      replicas: 3
    environment:
      - StyloBotDashboard__PostgreSQL__ConnectionString=Host=timescaledb;Database=stylobot;Username=stylobot;Password=...;Maximum Pool Size=50
```

### Connection Pooling

Add PgBouncer for connection pooling:

```yaml
services:
  pgbouncer:
    image: edoburu/pgbouncer
    environment:
      - DB_HOST=timescaledb
      - DB_PORT=5432
      - DB_NAME=stylobot
      - DB_USER=stylobot
      - DB_PASSWORD=...
      - POOL_MODE=transaction
      - MAX_CLIENT_CONN=1000
      - DEFAULT_POOL_SIZE=50
```

Update connection string:
```yaml
- StyloBotDashboard__PostgreSQL__ConnectionString=Host=pgbouncer;Database=stylobot;...
```

## Updates

### Upgrade TimescaleDB

```bash
# Pull latest image
docker-compose pull timescaledb

# Recreate container
docker-compose up -d timescaledb

# Run upgrade script
docker exec stylobot-timescale psql -U stylobot -d stylobot -c "ALTER EXTENSION timescaledb UPDATE;"
```

### Backup Before Upgrade

```bash
docker exec stylobot-timescale pg_dump -U stylobot stylobot | gzip > backup_$(date +%Y%m%d).sql.gz
```

## pgvector Support

The TimescaleDB image includes **pgvector** extension for ML-based vector similarity search:

```sql
-- Enable pgvector (included in timescale/timescaledb image)
CREATE EXTENSION IF NOT EXISTS vector;

-- Now you can use vector columns and similarity search
SELECT * FROM bot_signatures
ORDER BY signature_embedding <=> '[...]'::vector
LIMIT 10;
```

See [PGVECTOR_GUIDE.md](Mostlylucid.BotDetection.UI.PostgreSQL/PGVECTOR_GUIDE.md) for full documentation on:
- ML-based bot detection with embeddings
- Signature similarity clustering
- Migration from Qdrant
- Performance tuning

## Further Reading

- [TimescaleDB Docker Image](https://hub.docker.com/r/timescale/timescaledb)
- [TimescaleDB Documentation](https://docs.timescale.com/)
- [pgvector Documentation](https://github.com/pgvector/pgvector)
- [PostgreSQL Tuning Guide](https://pgtune.leopard.in.ua/)
- [Stylobot PostgreSQL Plugin](Mostlylucid.BotDetection.UI.PostgreSQL/README.md)
- [pgvector Integration Guide](Mostlylucid.BotDetection.UI.PostgreSQL/PGVECTOR_GUIDE.md)
