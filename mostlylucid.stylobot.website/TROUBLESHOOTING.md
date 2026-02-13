# Troubleshooting Guide

## Common Deployment Issues

### Issue: Caddy Caddyfile Mount Error

**Error Message:**
```
Error response from daemon: failed to create task for container:
error mounting "/root/stylobot/Caddyfile" to rootfs at "/etc/caddy/Caddyfile":
Check if the specified host path exists and is the expected type
```

**Cause:** The Caddyfile doesn't exist on the host or there's a directory/file mismatch.

**Solution:**

1. **Ensure Caddyfile exists on the server:**
   ```bash
   ls -la /opt/stylobot/Caddyfile
   ```

2. **If it doesn't exist, check if it's a directory:**
   ```bash
   # Remove if it's a directory
   rm -rf /opt/stylobot/Caddyfile

   # Create the file
   touch /opt/stylobot/Caddyfile
   ```

3. **Copy the Caddyfile:**
   ```bash
   # From your local machine
   scp Caddyfile user@server:/opt/stylobot/
   ```

4. **Verify the file:**
   ```bash
   # Should be a file, not a directory
   file /opt/stylobot/Caddyfile
   # Output: /opt/stylobot/Caddyfile: ASCII text

   # Check contents
   cat /opt/stylobot/Caddyfile
   ```

5. **Restart services:**
   ```bash
   cd /opt/stylobot
   docker-compose down
   docker-compose up -d
   ```

### Issue: Image Not Found

**Error Message:**
```
Error: No such image: stylobot-website:latest
```

**Solution:**

1. **Load the Docker image:**
   ```bash
   cd /opt/stylobot
   docker load < stylobot-website-latest.tar.gz
   ```

2. **Verify image is loaded:**
   ```bash
   docker images | grep stylobot-website
   ```

3. **Start services:**
   ```bash
   docker-compose up -d
   ```

### Issue: Gateway Image Not Found

**Error Message:**
```
Error: pull access denied for scottgal/mostlylucid.yarpgateway
```

**Cause:** The YARP Gateway image doesn't exist in Docker Hub yet.

**Temporary Solution:** Comment out the gateway in docker-compose.yml and update Caddyfile to point directly to website:

**docker-compose.yml:**
```yaml
# Comment out gateway and update caddy to point directly to website
# gateway:
#   image: scottgal/mostlylucid.yarpgateway:latest
#   ...

caddy:
  image: caddy:latest
  # Update depends_on
  depends_on:
    - website  # Changed from gateway
```

**Caddyfile:**
```caddyfile
stylobot.net, www.stylobot.net {
    # Point directly to website instead of gateway
    reverse_proxy website:8080 {
        # ... rest of config
    }
}
```

### Issue: Port Already in Use

**Error Message:**
```
Error: port is already allocated
```

**Solution:**

1. **Check what's using the port:**
   ```bash
   sudo lsof -i :80
   sudo lsof -i :443
   ```

2. **Stop conflicting service:**
   ```bash
   # If Apache
   sudo systemctl stop apache2

   # If Nginx
   sudo systemctl stop nginx
   ```

3. **Or change ports in docker-compose.yml:**
   ```yaml
   caddy:
     ports:
       - "8080:80"
       - "8443:443"
   ```

### Issue: Permission Denied for Data Directories

**Error Message:**
```
Permission denied: '/data/caddy'
```

**Solution:**

```bash
# Fix permissions
sudo chmod -R 777 /opt/stylobot/data
```

### Issue: Containers Keep Restarting

**Check logs:**
```bash
# All services
docker-compose logs

# Specific service
docker-compose logs website
docker-compose logs caddy
docker-compose logs gateway
```

**Common causes:**

1. **Website crashes on startup:**
   ```bash
   docker logs stylobot-website
   # Look for errors
   ```

2. **Caddy configuration error:**
   ```bash
   docker logs stylobot-caddy
   # Check Caddyfile syntax
   ```

3. **Network issues:**
   ```bash
   docker network inspect stylobot_stylobot-network
   ```

### Issue: SSL Certificate Not Working

**Error:** Unable to get SSL certificate from Let's Encrypt

**Solutions:**

1. **Check domain DNS:**
   ```bash
   nslookup stylobot.net
   dig stylobot.net
   ```

2. **Verify ports are accessible:**
   ```bash
   # From another machine
   nc -zv your-server-ip 80
   nc -zv your-server-ip 443
   ```

3. **Check Caddy logs:**
   ```bash
   docker logs stylobot-caddy | grep -i acme
   ```

4. **Test with staging:**
   Update Caddyfile:
   ```caddyfile
   {
       acme_ca https://acme-staging-v02.api.letsencrypt.org/directory
   }
   ```

### Issue: Can't Access Website

**Troubleshooting steps:**

1. **Check if containers are running:**
   ```bash
   docker ps
   ```

2. **Check logs:**
   ```bash
   docker-compose logs -f
   ```

3. **Test website directly:**
   ```bash
   # From server
   curl http://localhost:8080
   ```

4. **Test Caddy:**
   ```bash
   curl http://localhost
   curl -k https://localhost
   ```

5. **Check firewall:**
   ```bash
   sudo ufw status
   sudo ufw allow 80/tcp
   sudo ufw allow 443/tcp
   ```

## Clean Restart

If everything is broken, do a clean restart:

```bash
cd /opt/stylobot

# Stop and remove everything
docker-compose down -v

# Remove all containers
docker rm -f $(docker ps -aq) 2>/dev/null || true

# Start fresh
docker-compose up -d

# Watch logs
docker-compose logs -f
```

## Verify Deployment Checklist

- [ ] All files copied to `/opt/stylobot/`
- [ ] Docker image loaded: `docker images | grep stylobot-website`
- [ ] `.env` file exists and has correct values
- [ ] `.env` permissions set to 600: `ls -la .env`
- [ ] Caddyfile exists as a file (not directory)
- [ ] Data directories exist with 777 permissions
- [ ] DNS points to server IP
- [ ] Ports 80 and 443 are open in firewall
- [ ] User is in docker group or using sudo

## Getting Help

**Collect diagnostics:**
```bash
# System info
uname -a
docker --version
docker-compose --version

# Container status
docker ps -a

# Logs
docker-compose logs > /tmp/stylobot-logs.txt

# Network
docker network ls
docker network inspect stylobot_stylobot-network

# Images
docker images | grep stylobot
```

**Then create an issue with the output at:**
https://github.com/scottgal/stylobot/issues

