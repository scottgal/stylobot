# Complete Deployment Workflow

This guide walks through the complete process of building and deploying Stylobot to a production server.

## Overview

```
Development Machine → Build → Compress → Transfer → Server → Load → Deploy
```

## Prerequisites

### On Development Machine
- Docker installed and running
- Git repository cloned
- Build scripts: `build-docker.ps1` or `build-docker.sh`

### On Production Server
- Docker and Docker Compose installed
- SSH access
- `docker-compose.yml` and `Caddyfile` configured
- Domain DNS pointing to server
- Ports 80/443 open in firewall

## Step-by-Step Deployment

### Step 1: Build on Development Machine

#### Option A: Windows (PowerShell)
```powershell
# Navigate to project root
cd D:\Source\mostlylucid.stylobot\mostlylucid.stylobot.website

# Build with version tag and compression
.\build-docker.ps1 -Tag "v1.0.0" -Compress
```

#### Option B: Linux/Mac (Bash)
```bash
# Navigate to project root
cd /path/to/mostlylucid.stylobot.website

# Build with version tag and compression
./build-docker.sh -t v1.0.0 -c
```

**Output**: `dist/stylobot-website-v1.0.0.tar.gz`

### Step 2: Verify Build

```bash
# Check file exists
ls -lh dist/stylobot-website-v1.0.0.tar.gz

# Expected: ~200-300 MB compressed
```

### Step 3: Transfer to Server

```bash
# Copy compressed image to server
scp dist/stylobot-website-v1.0.0.tar.gz user@stylobot.net:/tmp/

# Copy docker-compose.yml if not already there
scp docker-compose.yml user@stylobot.net:/opt/stylobot/

# Copy Caddyfile if not already there
scp Caddyfile user@stylobot.net:/opt/stylobot/
```

### Step 4: SSH into Server

```bash
ssh user@stylobot.net
```

### Step 5: Load Docker Image

```bash
# Navigate to app directory
cd /opt/stylobot

# Load the compressed image
gunzip -c /tmp/stylobot-website-v1.0.0.tar.gz | docker load

# Verify image loaded
docker images | grep stylobot-website

# Expected output:
# stylobot-website   v1.0.0   abc123def456   2 minutes ago   234MB
```

### Step 6: Update docker-compose.yml

Edit the image tag to match your version:

```bash
nano docker-compose.yml
```

Find the website service and update the image:

```yaml
services:
  website:
    image: stylobot-website:v1.0.0  # Update this line
    # ... rest of config
```

### Step 7: Deploy Services

```bash
# Stop existing services (if running)
docker-compose down

# Pull any external images (gateway, caddy, watchtower)
docker-compose pull

# Start all services
docker-compose up -d

# Watch logs
docker-compose logs -f
```

### Step 8: Verify Deployment

```bash
# Check all containers are running
docker-compose ps

# Expected output:
# NAME                    STATUS
# stylobot-caddy         Up (healthy)
# stylobot-gateway       Up (healthy)
# stylobot-website       Up (healthy)
# stylobot-watchtower    Up

# Check website health endpoint
curl http://localhost:8080/health

# Expected: HTTP 200 OK

# Test from external URL
curl https://stylobot.net

# Should return your website HTML
```

### Step 9: Cleanup

```bash
# Remove temporary tarball
rm /tmp/stylobot-website-v1.0.0.tar.gz

# Remove old unused images (optional)
docker image prune -f
```

## Rollback Procedure

If something goes wrong, rollback to previous version:

```bash
# Stop current deployment
docker-compose down

# Edit docker-compose.yml to use previous tag
nano docker-compose.yml
# Change: image: stylobot-website:v1.0.0
# To:     image: stylobot-website:v0.9.0

# Restart with previous version
docker-compose up -d

# Verify rollback
docker-compose ps
curl https://stylobot.net
```

## Zero-Downtime Deployment (Advanced)

For zero-downtime deployments, use this approach:

```bash
# 1. Load new image
gunzip -c /tmp/stylobot-website-v1.0.0.tar.gz | docker load

# 2. Update docker-compose.yml to new version

# 3. Scale up with new version
docker-compose up -d --no-deps --scale website=2 website

# 4. Wait for new container to be healthy
docker-compose ps

# 5. Remove old container
docker-compose up -d --no-deps --scale website=1 website

# 6. Verify only new version is running
docker-compose ps
```

## Automated Deployment Script

Create a deployment script on the server:

```bash
#!/bin/bash
# deploy.sh - Automated deployment script

set -e

VERSION=$1
TARBALL="/tmp/stylobot-website-${VERSION}.tar.gz"
APP_DIR="/opt/stylobot"

if [ -z "$VERSION" ]; then
    echo "Usage: ./deploy.sh v1.0.0"
    exit 1
fi

echo "Deploying Stylobot Website ${VERSION}..."

# Load image
echo "Loading Docker image..."
gunzip -c "$TARBALL" | docker load

# Update docker-compose
echo "Updating docker-compose.yml..."
cd "$APP_DIR"
sed -i "s/stylobot-website:[^ ]*/stylobot-website:${VERSION}/" docker-compose.yml

# Deploy
echo "Deploying services..."
docker-compose up -d

# Wait for health check
echo "Waiting for health check..."
sleep 10

# Verify
if docker-compose ps | grep -q "Up (healthy)"; then
    echo "Deployment successful!"
    rm "$TARBALL"
else
    echo "Deployment may have issues. Check logs:"
    docker-compose logs --tail=50
fi
```

Usage:
```bash
chmod +x deploy.sh
./deploy.sh v1.0.0
```

## Monitoring After Deployment

### Check Logs
```bash
# All services
docker-compose logs -f

# Specific service
docker-compose logs -f website

# Last 100 lines
docker-compose logs --tail=100 website
```

### Check Resource Usage
```bash
# Container stats
docker stats

# Disk usage
docker system df

# Specific container
docker stats stylobot-website
```

### Check Health
```bash
# Health endpoint
curl http://localhost:8080/health

# Response time
curl -o /dev/null -s -w "Time: %{time_total}s\n" https://stylobot.net
```

### Check SSL Certificate (Caddy)
```bash
# Certificate info
docker exec stylobot-caddy caddy trust

# List certificates
docker exec stylobot-caddy ls -la /data/caddy/certificates/
```

## Troubleshooting Common Issues

### Container Won't Start
```bash
# Check logs
docker logs stylobot-website

# Check health
docker inspect stylobot-website | grep -A 5 Health
```

### Port Already in Use
```bash
# Find what's using the port
sudo netstat -tlnp | grep :8080

# Kill the process
sudo kill -9 <PID>
```

### Out of Disk Space
```bash
# Check disk usage
df -h

# Clean up Docker
docker system prune -a -f

# Remove unused volumes
docker volume prune -f
```

### Caddy SSL Issues
```bash
# Check Caddy logs
docker logs stylobot-caddy

# Verify DNS points to server
dig stylobot.net

# Test Caddy config
docker exec stylobot-caddy caddy validate --config /etc/caddy/Caddyfile
```

## Production Checklist

Before deploying to production:

- [ ] Build tested locally with `docker-compose up`
- [ ] Version tagged appropriately (semantic versioning)
- [ ] Compressed tarball created and verified
- [ ] Transferred to server successfully
- [ ] Server has sufficient disk space (check with `df -h`)
- [ ] Backup of previous version available
- [ ] docker-compose.yml reviewed and updated
- [ ] Environment variables set correctly
- [ ] DNS records verified
- [ ] Firewall rules configured (ports 80, 443)
- [ ] SSL certificate will auto-provision (Caddy)
- [ ] Health checks configured and working
- [ ] Monitoring/logging setup complete
- [ ] Rollback plan documented and tested

## Continuous Deployment with Watchtower

The docker-compose.yml includes Watchtower for automatic updates. To use:

1. Push images to Docker registry:
   ```bash
   docker tag stylobot-website:v1.0.0 your-registry/stylobot-website:latest
   docker push your-registry/stylobot-website:latest
   ```

2. Update docker-compose.yml to use registry image:
   ```yaml
   website:
     image: your-registry/stylobot-website:latest
   ```

3. Watchtower will automatically pull and deploy updates every 5 minutes

## Security Notes

- Never commit secrets to git
- Use environment variables for sensitive config
- Keep Docker and dependencies updated
- Regular security audits: `docker scan stylobot-website:v1.0.0`
- Limit SSH access, use key-based auth
- Configure firewall (ufw/iptables)
- Monitor logs for suspicious activity
- Keep backups of configuration and data

## Support

For deployment issues:
1. Check logs: `docker-compose logs`
2. Verify image: `docker images`
3. Test health: `curl http://localhost:8080/health`
4. Review this guide
5. Check Docker and system resources
