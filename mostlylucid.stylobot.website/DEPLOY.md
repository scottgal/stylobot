# Stylobot Website Deployment Guide

## Prerequisites

- Docker and Docker Compose installed on your server
- Domain pointing to your server's IP address
- Ports 80 and 443 available

## Deployment Steps

### 1. Transfer Files to Server

```bash
# SCP the Docker image
scp stylobot-website-latest.tar.gz user@your-server:/opt/stylobot/

# SCP the deployment files
scp docker-compose.yml user@your-server:/opt/stylobot/
scp Caddyfile user@your-server:/opt/stylobot/
scp .env.example user@your-server:/opt/stylobot/
```

### 2. Set Up Environment

```bash
# SSH into your server
ssh user@your-server

# Navigate to the deployment directory
cd /opt/stylobot

# Create .env file from example
cp .env.example .env

# Edit the .env file with your values
nano .env
```

**Important:** Update these values in `.env`:
- `DOMAIN` - Your actual domain (e.g., stylobot.net)
- `ADMIN_EMAIL` - Your email for Let's Encrypt notifications
- `WEB3FORMS_ACCESS_KEY` - Your Web3Forms API key (if changed)

### 3. Update Caddyfile

Edit the Caddyfile to use your domain:

```bash
nano Caddyfile
```

Replace `stylobot.com` with `stylobot.net` (or your actual domain).

### 4. Load Docker Image

```bash
# Load the Docker image
docker load < stylobot-website-latest.tar.gz

# Verify the image is loaded
docker images | grep stylobot-website
```

### 5. Start Services

```bash
# Start all services (Caddy, Gateway, Website, Watchtower)
docker-compose up -d

# Check logs
docker-compose logs -f

# Check status
docker-compose ps
```

### 6. Verify Deployment

1. **Check HTTP** (should redirect to HTTPS):
   ```bash
   curl -I http://your-domain.com
   ```

2. **Check HTTPS**:
   ```bash
   curl -I https://your-domain.com
   ```

3. **View logs**:
   ```bash
   # All services
   docker-compose logs

   # Specific service
   docker-compose logs website
   docker-compose logs caddy
   docker-compose logs gateway
   ```

## Architecture

```
Internet → Caddy (SSL/HTTPS) → YARP Gateway (Bot Detection) → Website (ASP.NET Core)
                                      ↓
                                  Watchtower (Auto-updates)
```

## Service Descriptions

### Caddy
- Handles SSL/TLS with automatic Let's Encrypt certificates
- Reverse proxy to YARP Gateway
- Port 80 (HTTP) and 443 (HTTPS)

### YARP Gateway
- Bot detection layer using StyloBot
- Routes traffic to the website backend
- Learns bot patterns automatically

### Website
- ASP.NET Core 10 application
- Serves the Stylobot marketing site
- Internal port 8080

### Watchtower
- Automatically updates containers
- Checks every 5 minutes (configurable)
- Only updates labeled containers

## Updating the Application

### Manual Update

```bash
# Pull new image from registry (when available)
docker pull scottgal/stylobot-website:latest

# Restart services
docker-compose up -d
```

### Automatic Updates

Watchtower will automatically:
1. Check for new images every 5 minutes
2. Pull new images when available
3. Restart affected containers
4. Clean up old images

## Troubleshooting

### Logs

```bash
# View all logs
docker-compose logs -f

# View specific service
docker-compose logs -f website
docker-compose logs -f caddy
docker-compose logs -f gateway
```

### Restart Services

```bash
# Restart all services
docker-compose restart

# Restart specific service
docker-compose restart website
```

### SSL Issues

If Let's Encrypt fails:
1. Check your domain DNS points to the correct IP
2. Ensure ports 80 and 443 are open
3. Check Caddy logs: `docker-compose logs caddy`
4. Verify email in .env is correct

### Health Checks

```bash
# Check website health
curl http://localhost:8080/health

# Check through Caddy
curl https://your-domain.com/health
```

## Stopping Services

```bash
# Stop all services
docker-compose down

# Stop and remove volumes
docker-compose down -v
```

## File Structure

```
/opt/stylobot/
├── docker-compose.yml      # Service orchestration
├── Caddyfile              # Caddy reverse proxy config
├── .env                   # Environment variables
├── stylobot-website-latest.tar.gz  # Docker image
└── data/                  # Persistent data (created automatically)
    ├── caddy/            # Caddy SSL certificates
    └── bot-detection/    # Bot detection patterns
```

## Security Notes

1. **SSL/TLS**: Caddy handles automatic HTTPS with Let's Encrypt
2. **Bot Protection**: YARP Gateway provides automatic bot detection
3. **Privacy**: No PII data is stored
4. **Updates**: Watchtower keeps containers up to date
5. **Firewall**: Only expose ports 80 and 443

## Support

- GitHub: https://github.com/scottgal/mostlylucid.nugetpackages
- Documentation: https://github.com/scottgal/mostlylucid.nugetpackages/blob/main/Mostlylucid.BotDetection/README.md
- Issues: https://github.com/scottgal/mostlylucid.nugetpackages/issues
