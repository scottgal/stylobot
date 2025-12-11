# Docker Troubleshooting Guide

## Network Creation Error: "No chain/target/match by that name"

### Error Message
```
failed to create network stylobot_stylobot-network: Error response from daemon:
Failed to Setup IP tables: Unable to enable SKIP DNAT rule: (iptables failed:
iptables --wait -t nat -I DOCKER -i br-9dece01c5388 -j RETURN: iptables:
No chain/target/match by that name. (exit status 1))
```

### Cause
Docker's iptables chains are out of sync or corrupted. This typically happens when:
- Docker service was restarted improperly
- Firewall rules were modified manually
- System was rebooted while Docker was running
- Docker and iptables got into an inconsistent state

### Solutions (Try in Order)

#### Solution 1: Restart Docker Service (Easiest)

**Linux:**
```bash
sudo systemctl restart docker
```

**Windows (Docker Desktop):**
- Right-click Docker Desktop icon in system tray
- Select "Restart Docker Desktop"

**Windows (WSL2):**
```bash
# In WSL2 terminal
sudo service docker restart
```

Then try again:
```bash
docker-compose up -d
```

---

#### Solution 2: Clean Docker Network State

```bash
# Stop all containers
docker-compose down

# Remove all networks (be careful if you have other projects!)
docker network prune -f

# Restart Docker
sudo systemctl restart docker  # Linux
# or restart Docker Desktop

# Try again
docker-compose up -d
```

---

#### Solution 3: Rebuild iptables Chains

**Linux only:**

```bash
# Stop Docker
sudo systemctl stop docker

# Clean iptables Docker chains
sudo iptables -t nat -F DOCKER
sudo iptables -t filter -F DOCKER
sudo iptables -t nat -X DOCKER
sudo iptables -t filter -X DOCKER

# Restart Docker (this recreates chains)
sudo systemctl start docker

# Verify Docker is running
sudo systemctl status docker

# Try again
docker-compose up -d
```

---

#### Solution 4: Full Docker Reset (Nuclear Option)

**WARNING: This will remove ALL Docker data (containers, volumes, images, networks)**

```bash
# Stop Docker
sudo systemctl stop docker

# Remove Docker data
sudo rm -rf /var/lib/docker/network/

# Clean iptables
sudo iptables -t nat -F
sudo iptables -t nat -X
sudo iptables -t filter -F
sudo iptables -t filter -X

# Restart Docker
sudo systemctl start docker

# Reload your images
gunzip -c /tmp/stylobot-website-v1.0.0.tar.gz | docker load

# Try deployment
docker-compose up -d
```

---

#### Solution 5: Use Host Network Mode (Temporary Workaround)

If you need to get running quickly, modify docker-compose.yml:

```yaml
services:
  website:
    image: stylobot-website:latest
    network_mode: "host"  # Add this line
    # Remove the 'networks' section
    # Remove 'expose' and use 'ports' instead
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
```

**Note**: This is not ideal for production but can get you running.

---

### Platform-Specific Solutions

#### Windows (Docker Desktop)

1. **Reset Docker Desktop:**
   - Open Docker Desktop
   - Click Settings (gear icon)
   - Go to "Troubleshoot"
   - Click "Reset to factory defaults"
   - Click "Reset" to confirm
   - Restart Docker Desktop

2. **Reinstall Docker Desktop:**
   - Uninstall Docker Desktop
   - Reboot Windows
   - Download latest version from docker.com
   - Install and restart

#### Linux (Ubuntu/Debian)

```bash
# Reinstall iptables
sudo apt-get install --reinstall iptables

# Restart Docker
sudo systemctl restart docker

# Check Docker daemon
sudo systemctl status docker
```

#### WSL2 (Windows)

```bash
# In PowerShell (as Administrator)
wsl --shutdown

# Restart WSL2
wsl

# In WSL2, restart Docker
sudo service docker restart
```

---

### Verification Steps

After applying a fix:

```bash
# 1. Check Docker is running
docker info

# 2. Check iptables chains exist
sudo iptables -t nat -L DOCKER

# 3. Test network creation
docker network create test-network
docker network rm test-network

# 4. Deploy your stack
docker-compose up -d

# 5. Verify containers are running
docker-compose ps
```

---

### Prevention

To avoid this issue in the future:

1. **Always stop containers properly:**
   ```bash
   docker-compose down
   ```

2. **Restart Docker cleanly:**
   ```bash
   sudo systemctl restart docker
   ```

3. **Don't modify iptables manually** while Docker is running

4. **Use Docker Desktop restart** rather than killing processes

---

## Other Common Docker Issues

### Port Already in Use

**Error:** `Bind for 0.0.0.0:8080 failed: port is already allocated`

**Solution:**
```bash
# Find what's using the port
sudo netstat -tlnp | grep :8080
# or
sudo lsof -i :8080

# Kill the process
sudo kill -9 <PID>

# Or change the port in docker-compose.yml
```

### Permission Denied

**Error:** `Got permission denied while trying to connect to Docker daemon`

**Solution:**
```bash
# Add user to docker group
sudo usermod -aG docker $USER

# Log out and back in, or:
newgrp docker

# Verify
docker ps
```

### Out of Disk Space

**Error:** `no space left on device`

**Solution:**
```bash
# Check disk usage
docker system df

# Clean up
docker system prune -a -f

# Remove unused volumes
docker volume prune -f

# Check again
df -h
```

### Image Load Failed

**Error:** `Error processing tar file`

**Solution:**
```bash
# Verify tarball integrity
gunzip -t stylobot-website-v1.0.0.tar.gz

# If corrupted, re-download/re-create

# Try loading with verbose output
gunzip -c stylobot-website-v1.0.0.tar.gz | docker load -q
```

---

## Getting More Help

If issues persist:

1. **Check Docker logs:**
   ```bash
   # Linux
   sudo journalctl -u docker -n 100

   # Docker Desktop
   # Settings → Troubleshoot → View logs
   ```

2. **Check system logs:**
   ```bash
   dmesg | tail -50
   ```

3. **Verify Docker version:**
   ```bash
   docker --version
   docker-compose --version
   ```

4. **Test Docker installation:**
   ```bash
   docker run hello-world
   ```

5. **Check for known issues:**
   - https://github.com/docker/for-linux/issues
   - https://github.com/docker/for-win/issues
   - https://github.com/docker/for-mac/issues
