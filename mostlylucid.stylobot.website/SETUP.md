# Stylobot Website - Setup Guide

Deploy the Stylobot marketing website with Docker. Caddy handles HTTPS automatically.

## What you need

- A Linux server (Ubuntu, Debian, etc.) with Docker and Docker Compose installed
- A domain name (e.g. `stylobot.net`) with DNS A record pointing to your server IP
- Ports 80 and 443 open on your firewall

## Step 1: Build the Docker image (on your local machine)

```powershell
# Windows (PowerShell)
.\build-docker.ps1 -Compress
```

```bash
# Linux/Mac
./build-docker.sh -c
```

This creates `dist/stylobot-website-latest.tar.gz`.

## Step 2: Copy files to your server

```bash
# Copy the image and config files
scp dist/stylobot-website-latest.tar.gz user@your-server:/opt/stylobot/
scp docker-compose.yml user@your-server:/opt/stylobot/
scp Caddyfile user@your-server:/opt/stylobot/
scp .env.example user@your-server:/opt/stylobot/
```

## Step 3: Configure on the server

```bash
ssh user@your-server
cd /opt/stylobot

# Create your .env from the example
cp .env.example .env
```

Edit `.env` and set the two required secrets:

```bash
nano .env
```

**You must change these two lines:**

```
BOTDETECTION_SIGNATURE_HASH_KEY=<paste-a-random-string-here>
BOTDETECTION_CLIENTSIDE_TOKEN_SECRET=<paste-another-random-string-here>
```

Generate random strings with: `openssl rand -hex 32`

**Optional:** If you want the contact form to work, set your Web3Forms key:

```
Web3Forms__AccessKey=your-web3forms-key-here
```

Everything else has sane defaults. Leave it as-is unless you know what you're changing.

## Step 4: Edit the Caddyfile for your domain

```bash
nano Caddyfile
```

Replace `stylobot.net` with your actual domain on this line:

```
stylobot.net, www.stylobot.net {
```

Update the email address too (used for Let's Encrypt certificate notifications):

```
email your-email@example.com
```

## Step 5: Load the Docker image and start

```bash
# Load the image
gunzip -c stylobot-website-latest.tar.gz | docker load

# Start everything
docker compose up -d
```

That's it. Caddy will automatically get an HTTPS certificate from Let's Encrypt.

## Step 6: Verify it works

```bash
# Check all containers are running
docker compose ps

# Check the website responds
curl -I https://your-domain.com

# Check health endpoint
curl https://your-domain.com/health

# View logs if something is wrong
docker compose logs -f
```

---

## Architecture

```
Internet --> Caddy (ports 80/443, auto-HTTPS) --> Website (port 8080, internal)
                                                       |
                                                  Watchtower (auto-updates containers)
```

| Container | Purpose |
|-----------|---------|
| **caddy** | Reverse proxy, automatic HTTPS via Let's Encrypt, security headers |
| **website** | ASP.NET Core 10 app with bot detection |
| **watchtower** | Checks for new container images every 5 minutes, auto-restarts |

## Updating the site

### Option A: Manual update

```bash
# On your local machine: build new image
.\build-docker.ps1 -Compress

# Copy to server
scp dist/stylobot-website-latest.tar.gz user@your-server:/opt/stylobot/

# On server: load and restart
cd /opt/stylobot
gunzip -c stylobot-website-latest.tar.gz | docker load
docker compose up -d
```

### Option B: Automatic (via container registry)

If you push images to a registry (Docker Hub, GHCR), Watchtower will detect new versions and restart automatically. No manual steps needed.

## Adding PostgreSQL (optional)

For persistent storage of detection data, uncomment the PostgreSQL lines in `.env`:

```
STYLOBOT_POSTGRESQL_CONNECTION=Host=db;Database=stylobot;Username=stylobot;Password=your_password
STYLOBOT_ENABLE_TIMESCALEDB=true
STYLOBOT_AUTO_INIT_SCHEMA=true
```

Then add a PostgreSQL service to `docker-compose.yml`:

```yaml
  db:
    image: timescale/timescaledb:latest-pg16
    container_name: stylobot-db
    environment:
      POSTGRES_DB: stylobot
      POSTGRES_USER: stylobot
      POSTGRES_PASSWORD: your_password
    volumes:
      - postgres-data:/var/lib/postgresql/data
    networks:
      - stylobot-network
    restart: unless-stopped
```

And add `postgres-data` to the volumes section:

```yaml
volumes:
  bot-detection-data:
  caddy-data:
  caddy-config:
  postgres-data:
```

## Troubleshooting

### Site not loading

```bash
# Check containers
docker compose ps

# Check website logs
docker compose logs website

# Check caddy logs (SSL issues show here)
docker compose logs caddy
```

### SSL certificate not working

1. Check your DNS: `dig your-domain.com` should return your server IP
2. Check ports 80 and 443 are open: `sudo ufw status`
3. Check Caddy logs: `docker compose logs caddy`

### Restart everything

```bash
docker compose down
docker compose up -d
```

### Reset everything (nuclear option)

```bash
docker compose down -v   # -v removes volumes (data loss!)
docker compose up -d
```

## File layout on server

```
/opt/stylobot/
  docker-compose.yml          # Container orchestration
  Caddyfile                   # Reverse proxy config
  .env                        # Your secrets and settings
  stylobot-website-latest.tar.gz  # Docker image (can delete after loading)
```

## Local development

```bash
# Install frontend deps
npm install --prefix src/Stylobot.Website

# Build frontend assets
npm run build --prefix src/Stylobot.Website

# Run the .NET app
dotnet run --project src/Stylobot.Website/Stylobot.Website.csproj
```

The app runs at `https://localhost:7038`. The dashboard is at `https://localhost:7038/_stylobot`.
