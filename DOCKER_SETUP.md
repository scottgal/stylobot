# Docker Setup for Stylobot

> **Note**: Docker with PostgreSQL/TimescaleDB is **optional**! Stylobot works perfectly with:
> - **Default**: File/SQLite storage (no Docker required)
> - **Enhanced**: PostgreSQL (this guide)
> - **Optimized**: + TimescaleDB (this guide)
> - **ML-Powered**: + pgvector (this guide)
>
> Use this Docker setup only if you want enhanced performance and scale!

## Prerequisites

- Docker Engine 24+ and Docker Compose v2
- 2GB+ RAM (4GB recommended for full stack)
- For Ubuntu Server, see [Ubuntu Server Setup](#ubuntu-server-setup) below

## Quick Start

```bash
# 1. Create your environment file
cp .env.example .env

# 2. Set a strong password (required - compose will fail without it)
#    Edit .env and set POSTGRES_PASSWORD to a strong random value
nano .env

# 3. Start everything (TimescaleDB + Stylobot Dashboard)
docker compose up -d

# 4. View logs
docker compose logs -f

# 5. Access dashboard
#    http://localhost:5080/stylobot
```

## Deployment Options

### Option A: Demo Dashboard (`docker-compose.yml`)

Simple stack: TimescaleDB + Stylobot Demo app with dashboard.

```bash
docker compose up -d
# Dashboard: http://localhost:5080/stylobot
```

### Option B: Full Demo Stack (`docker-compose.demo.yml`)

Full stack: Caddy (TLS) + YARP Gateway + Website + TimescaleDB + Qdrant + Ollama + Redis.

```bash
docker compose -f docker-compose.demo.yml up -d
# Site: https://demo.stylobot.local (add to /etc/hosts)
# Dashboard: https://demo.stylobot.local/_stylobot
# Gateway health: https://demo.stylobot.local/admin/health
```

### Option C: YARP Gateway Only (Production)

Deploy the Stylobot Gateway as a reverse proxy in front of your existing app:

```bash
docker run -d \
  --name stylobot-gateway \
  -p 80:8080 \
  -e DEFAULT_UPSTREAM=http://your-backend:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e BotDetection__BotThreshold=0.7 \
  -e BotDetection__BlockDetectedBots=true \
  scottgal/stylobot-gateway:latest
```

See [YARP Gateway Integration](#yarp-gateway-integration) for details.

## Ubuntu Server Setup

Step-by-step deployment on a fresh Ubuntu Server 24.04 LTS:

### 1. Install Docker

```bash
# Update system
sudo apt update && sudo apt upgrade -y

# Install Docker prerequisites
sudo apt install -y ca-certificates curl gnupg

# Add Docker GPG key and repository
sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
sudo chmod a+r /etc/apt/keyrings/docker.gpg

echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
  $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
  sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Add your user to docker group (log out and back in after)
sudo usermod -aG docker $USER
```

### 2. Clone and Configure

```bash
# Clone the repository
git clone https://github.com/scottgal/mostlylucid.nugetpackages.git /opt/stylobot
cd /opt/stylobot

# Create environment file
cp .env.example .env

# Generate a strong random password and set it
POSTGRES_PW=$(openssl rand -base64 32)
sed -i "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=$POSTGRES_PW/" .env

# Also set ADMIN_SECRET if using the full demo stack
ADMIN_SECRET=$(openssl rand -base64 32)
sed -i "s/^ADMIN_SECRET=.*/ADMIN_SECRET=$ADMIN_SECRET/" .env

echo "Passwords generated. Review with: cat .env"
```

### 3. Start the Stack

```bash
# For the simple demo dashboard:
docker compose up -d

# Or for the full demo stack with YARP gateway:
docker compose -f docker-compose.demo.yml up -d

# Verify everything is running
docker compose ps
```

### 4. Configure Firewall

```bash
# Allow HTTP and HTTPS
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp

# If using the demo dashboard directly (without Caddy)
sudo ufw allow 5080/tcp

# Enable firewall if not already active
sudo ufw enable
```

### 5. Set Up as a systemd Service (Optional)

```bash
# Create systemd service for auto-start on boot
sudo tee /etc/systemd/system/stylobot.service > /dev/null << 'EOF'
[Unit]
Description=Stylobot Bot Detection
Requires=docker.service
After=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=/opt/stylobot
ExecStart=/usr/bin/docker compose up -d
ExecStop=/usr/bin/docker compose down
TimeoutStartSec=120

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable stylobot
```

## YARP Gateway Integration

The Stylobot Gateway (`Stylobot.Gateway`) is a Docker-first YARP reverse proxy that adds bot detection to any web application.

### Architecture

```
Client --> Caddy (TLS) --> Stylobot Gateway (bot detection) --> Your Backend
                              |
                              +--> TimescaleDB (event storage)
                              +--> Ollama (optional AI detection)
```

### Gateway Configuration

The gateway runs on port `8080` inside the container. Key environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `DEFAULT_UPSTREAM` | - | Backend URL to proxy to (required) |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Runtime environment |
| `BotDetection__BotThreshold` | `0.7` | Bot confidence threshold (0.0-1.0) |
| `BotDetection__BlockDetectedBots` | `false` | Block detected bots with HTTP 403 |
| `BotDetection__AiDetection__Provider` | `Heuristic` | AI provider: `Heuristic`, `Ollama`, `OpenAI` |
| `BotDetection__ResponseHeaders__Enabled` | `true` | Add `X-Bot-*` response headers |
| `ADMIN_SECRET` | - | Secret for admin endpoints |

### Gateway with Caddy (Production TLS)

For production with automatic HTTPS:

```yaml
# docker-compose.production.yml
services:
  caddy:
    image: caddy:2-alpine
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
    depends_on:
      gateway:
        condition: service_healthy

  gateway:
    image: scottgal/stylobot-gateway:latest
    environment:
      - DEFAULT_UPSTREAM=http://your-backend:8080
      - BotDetection__BotThreshold=0.7
      - BotDetection__BlockDetectedBots=true
    expose:
      - "8080"
    healthcheck:
      test: ["CMD", "wget", "-q", "-O", "/dev/null", "http://localhost:8080/admin/health"]
      interval: 30s
      timeout: 5s
      retries: 3

volumes:
  caddy-data:
```

Caddyfile:
```
yourdomain.com {
    reverse_proxy gateway:8080
}
```

### Gateway Health Check

```bash
# Check gateway health
curl http://localhost:8080/admin/health

# Check bot detection headers on a request
curl -v -H "User-Agent: curl/8.0" http://localhost:8080/
# Look for X-Bot-Detection, X-Bot-Confidence headers in response
```

## Services

### TimescaleDB
- **Image**: [`timescale/timescaledb:latest-pg16`](https://hub.docker.com/r/timescale/timescaledb)
- **Port**: `5432` (PostgreSQL)
- **Database**: `stylobot`
- **User**: `stylobot`
- **Password**: Set via `POSTGRES_PASSWORD` in `.env` (copy `.env.example` to `.env`)
- **Extensions**: Includes TimescaleDB + **pgvector** (for ML-based similarity search)

### Stylobot Demo
- **Port**: `5080` (HTTP)
- **Dashboard**: `http://localhost:5080/stylobot`
- **Storage**: Automatically uses TimescaleDB
- **Auto-initialization**: Schema creates on first run

### Stylobot Gateway
- **Port**: `8080` (internal)
- **Image**: `scottgal/stylobot-gateway:latest`
- **Health**: `http://localhost:8080/admin/health`
- **Config**: Environment variables or mounted `/app/config`

## Configuration

### Environment Variables

All secrets are managed via `.env` file (never committed to git):

```bash
cp .env.example .env
nano .env
```

Required variables:
- `POSTGRES_PASSWORD` - Database password (compose will refuse to start without this)
- `ADMIN_SECRET` - Gateway admin API secret (for full demo stack)

Optional variables - see `.env.example` for the full list.

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
- Command: `curl -f http://localhost:5080/health`
- Interval: 30s
- Retries: 3
- Start period: 10s

### Stylobot Gateway
- Command: `wget -q -O /dev/null http://localhost:8080/admin/health`
- Interval: 30s
- Retries: 3
- Start period: 15s

The demo app waits for TimescaleDB to be healthy before starting.

## Production Hardening

### 1. Tune for Your Traffic

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

### 2. Enable PostgreSQL SSL/TLS

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

### 3. Restrict Network Access

```yaml
# Don't expose PostgreSQL to the host in production
services:
  timescaledb:
    # Remove the ports mapping - only expose within Docker network
    # ports:
    #   - "5432:5432"
    networks:
      - stylobot-network
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

### Automated Backup (cron)

```bash
# Add to crontab: daily backup at 2 AM, keep 7 days
echo "0 2 * * * docker exec stylobot-timescale pg_dump -U stylobot stylobot | gzip > /opt/stylobot/backups/backup_\$(date +\%Y\%m\%d).sql.gz && find /opt/stylobot/backups -name '*.sql.gz' -mtime +7 -delete" | sudo tee -a /var/spool/cron/crontabs/root
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
docker compose ps
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
docker compose logs stylobot-demo | grep -i "schema\|timescale"
```

### Compose Fails with "Set POSTGRES_PASSWORD"

You must create a `.env` file with the required secrets:
```bash
cp .env.example .env
# Edit .env and set POSTGRES_PASSWORD to a strong value
nano .env
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
      - StyloBotDashboard__PostgreSQL__ConnectionString=Host=timescaledb;Database=stylobot;Username=stylobot;Password=${POSTGRES_PASSWORD};Maximum Pool Size=50
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
      - DB_PASSWORD=${POSTGRES_PASSWORD}
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
docker compose pull timescaledb

# Recreate container
docker compose up -d timescaledb

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
- [Gateway Demo Mode](Stylobot.Gateway/docs/DEMO_MODE.md)
- [TLS Fingerprinting Setup](Stylobot.Gateway/docs/TLS_FINGERPRINTING_SETUP.md)
- [YARP Integration](Mostlylucid.BotDetection/docs/yarp-integration.md)
