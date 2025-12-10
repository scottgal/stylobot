# Server Directory Setup & Permissions Guide

## Quick Setup (Automated)

**On your server:**
```bash
# Copy setup script
scp setup-server.sh user@server:/tmp/

# SSH to server
ssh user@server

# Run setup script
sudo bash /tmp/setup-server.sh
```

## Manual Setup

If you prefer to set up directories manually:

### 1. Create Directory Structure

```bash
# Create main deployment directory
sudo mkdir -p /opt/stylobot

# Create data directories
sudo mkdir -p /opt/stylobot/data/caddy-data
sudo mkdir -p /opt/stylobot/data/caddy-config
sudo mkdir -p /opt/stylobot/data/bot-detection
sudo mkdir -p /opt/stylobot/logs
```

### 2. Set Ownership

Replace `youruser` with your actual username:

```bash
# Set ownership
sudo chown -R youruser:youruser /opt/stylobot

# Or if using a different group
sudo chown -R youruser:docker /opt/stylobot
```

### 3. Set Permissions

```bash
# Main directory
sudo chmod -R 755 /opt/stylobot

# Data directories (Docker needs write access)
sudo chmod -R 777 /opt/stylobot/data

# Logs directory
sudo chmod 777 /opt/stylobot/logs
```

## Directory Structure

```
/opt/stylobot/
├── docker-compose.yml          # 644 (rw-r--r--)
├── Caddyfile                   # 644 (rw-r--r--)
├── .env                        # 600 (rw-------)  IMPORTANT: Secure this!
├── stylobot-website-latest.tar.gz
├── data/                       # 777 (rwxrwxrwx)
│   ├── caddy-data/            # 777 (Caddy SSL certs)
│   ├── caddy-config/          # 777 (Caddy config)
│   └── bot-detection/         # 777 (Bot detection data)
└── logs/                      # 777 (rwxrwxrwx)
```

## Permission Explanation

### Why 755 for /opt/stylobot?
- Owner: Read, Write, Execute
- Group: Read, Execute
- Others: Read, Execute
- Allows your user to manage files while others can read

### Why 777 for data directories?
- Docker containers run with different UIDs
- Need write access to store:
  - SSL certificates (Caddy)
  - Bot detection patterns
  - Configuration files
  - Logs

### Why 600 for .env?
- Contains sensitive configuration
- Only owner can read/write
- Nobody else can access

## Secure .env File

**After copying .env to the server:**

```bash
# Set restrictive permissions
chmod 600 /opt/stylobot/.env

# Verify
ls -la /opt/stylobot/.env
# Should show: -rw------- 1 youruser youruser
```

## Docker Socket Permissions

Watchtower needs access to Docker socket:

```bash
# Check if your user is in docker group
groups

# If not, add user to docker group
sudo usermod -aG docker $USER

# Log out and back in for changes to take effect
exit
ssh user@server

# Verify
docker ps
```

## File Transfer with Correct Permissions

```bash
# Transfer files maintaining permissions
scp -p docker-compose.yml user@server:/opt/stylobot/
scp -p Caddyfile user@server:/opt/stylobot/
scp -p .env user@server:/opt/stylobot/
scp -p stylobot-website-latest.tar.gz user@server:/opt/stylobot/

# Then secure .env
ssh user@server "chmod 600 /opt/stylobot/.env"
```

## Verification Commands

```bash
# Check directory structure
tree /opt/stylobot

# Check permissions
ls -la /opt/stylobot/
ls -la /opt/stylobot/data/

# Check ownership
ls -lh /opt/stylobot/ | grep -E '^d'

# Check .env is secure
ls -la /opt/stylobot/.env
# Should show: -rw------- (600)
```

## Common Issues

### Issue: Permission denied when starting Docker

**Solution:**
```bash
# Add user to docker group
sudo usermod -aG docker $USER

# Or run docker-compose with sudo
sudo docker-compose up -d
```

### Issue: Caddy can't write certificates

**Solution:**
```bash
# Ensure data directory is writable
sudo chmod -R 777 /opt/stylobot/data

# Check ownership
sudo chown -R $USER:$USER /opt/stylobot/data
```

### Issue: Bot detection data not persisting

**Solution:**
```bash
# Create and set permissions for bot-detection directory
sudo mkdir -p /opt/stylobot/data/bot-detection
sudo chmod 777 /opt/stylobot/data/bot-detection
```

### Issue: Can't read .env file

**Solution:**
```bash
# Check .env permissions
ls -la /opt/stylobot/.env

# Fix if needed
chmod 600 /opt/stylobot/.env
chown $USER:$USER /opt/stylobot/.env
```

## Security Best Practices

1. **Limit .env access:** Only owner should read/write
   ```bash
   chmod 600 /opt/stylobot/.env
   ```

2. **Use specific user:** Don't run as root
   ```bash
   # Run docker-compose as your user
   docker-compose up -d
   ```

3. **Firewall rules:** Only allow ports 80 and 443
   ```bash
   sudo ufw allow 80/tcp
   sudo ufw allow 443/tcp
   sudo ufw enable
   ```

4. **Regular updates:** Watchtower handles this automatically

5. **Backup data:** Regular backups of /opt/stylobot/data/

## Backup Commands

```bash
# Backup all data
sudo tar -czf /backup/stylobot-data-$(date +%Y%m%d).tar.gz /opt/stylobot/data/

# Backup configuration only
sudo tar -czf /backup/stylobot-config-$(date +%Y%m%d).tar.gz /opt/stylobot/*.yml /opt/stylobot/Caddyfile /opt/stylobot/.env

# Restore data
sudo tar -xzf /backup/stylobot-data-20250101.tar.gz -C /
```
