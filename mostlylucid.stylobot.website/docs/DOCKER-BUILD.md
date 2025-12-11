# Docker Build & Deployment Guide

This guide explains how to build and deploy the Stylobot website Docker image.

## Quick Start

### Windows (PowerShell)

```powershell
# Build image
.\build-docker.ps1

# Build and create compressed tarball for deployment
.\build-docker.ps1 -Compress
```

### Linux/Mac (Bash)

```bash
# Build image
./build-docker.sh

# Build and create compressed tarball for deployment
./build-docker.sh --compress
```

## Build Scripts

Two build scripts are provided for cross-platform compatibility:

- **build-docker.ps1** - PowerShell script (Windows, Linux, Mac with PowerShell Core)
- **build-docker.sh** - Bash script (Linux, Mac, WSL, Git Bash)

Both scripts provide the same functionality and options.

## Script Options

### PowerShell (build-docker.ps1)

```powershell
.\build-docker.ps1 [OPTIONS]

Options:
  -Tag <string>        Image tag (default: "latest")
  -ImageName <string>  Image name (default: "stylobot-website")
  -NoCache            Build without cache
  -SaveTarball        Save image as .tar file
  -Compress           Save and compress image as .tar.gz

Examples:
  .\build-docker.ps1
  .\build-docker.ps1 -Tag "v1.0.0"
  .\build-docker.ps1 -Compress
  .\build-docker.ps1 -Tag "v1.0.0" -Compress
```

### Bash (build-docker.sh)

```bash
./build-docker.sh [OPTIONS]

Options:
  -t, --tag TAG        Image tag (default: "latest")
  -n, --name NAME      Image name (default: "stylobot-website")
  --no-cache           Build without cache
  -s, --save           Save image as .tar file
  -c, --compress       Save and compress image as .tar.gz
  -h, --help           Show help message

Examples:
  ./build-docker.sh
  ./build-docker.sh -t v1.0.0
  ./build-docker.sh -c
  ./build-docker.sh -t v1.0.0 -c
```

## Common Build Scenarios

### 1. Local Development Build

Just build the image for local testing:

```bash
# PowerShell
.\build-docker.ps1

# Bash
./build-docker.sh
```

### 2. Production Build with Version Tag

Build with a specific version tag:

```bash
# PowerShell
.\build-docker.ps1 -Tag "v1.2.3"

# Bash
./build-docker.sh -t v1.2.3
```

### 3. Build for Remote Deployment (Compressed)

Build and create a compressed tarball for manual deployment:

```bash
# PowerShell
.\build-docker.ps1 -Tag "v1.2.3" -Compress

# Bash
./build-docker.sh -t v1.2.3 -c
```

This creates: `dist/stylobot-website-v1.2.3.tar.gz`

### 4. Rebuild Without Cache

Force a clean rebuild without using Docker cache:

```bash
# PowerShell
.\build-docker.ps1 -NoCache

# Bash
./build-docker.sh --no-cache
```

## Manual Deployment with Tarball

When you use the `-Compress` option, a `.tar.gz` file is created in the `dist/` directory.

### Step 1: Build Compressed Image

```bash
# PowerShell
.\build-docker.ps1 -Tag "v1.0.0" -Compress

# Bash
./build-docker.sh -t v1.0.0 -c
```

Output: `dist/stylobot-website-v1.0.0.tar.gz`

### Step 2: Copy to Server

```bash
scp dist/stylobot-website-v1.0.0.tar.gz user@your-server.com:/tmp/
```

### Step 3: Load on Server

SSH into your server and run:

```bash
# Decompress and load into Docker
gunzip -c /tmp/stylobot-website-v1.0.0.tar.gz | docker load

# Verify image loaded
docker images stylobot-website

# Clean up tarball
rm /tmp/stylobot-website-v1.0.0.tar.gz
```

### Step 4: Deploy with Docker Compose

Make sure your `docker-compose.yml` references the correct image:

```yaml
services:
  website:
    image: stylobot-website:v1.0.0  # Match your tag
    # ... rest of config
```

Then start the services:

```bash
docker-compose up -d
```

## Docker Compose Deployment

### Using Pre-built Local Image

After building the image locally:

```bash
docker-compose up -d
```

### Using Docker Hub (if pushing images)

```bash
# Tag for registry
docker tag stylobot-website:latest your-registry/stylobot-website:latest

# Push to registry
docker push your-registry/stylobot-website:latest

# Update docker-compose.yml to reference registry image
# Then on server:
docker-compose pull
docker-compose up -d
```

## What Gets Built

The Docker build process includes:

1. **Backend**: ASP.NET Core 10 application
2. **Frontend Assets**: Vite build output (CSS + JS)
3. **Node.js**: Required for building frontend assets during build
4. **Runtime**: Minimal ASP.NET runtime image (no Node.js in final image)

### Multi-stage Build

The Dockerfile uses multi-stage builds:

- **Build stage**: Includes .NET SDK + Node.js for building
- **Final stage**: Only includes .NET runtime + compiled application
- **Result**: Small, optimized production image

## Build Output

After building, you'll see:

```
========================================
Build Successful!
========================================

Image: stylobot-website:latest
Size: 234MB

To run locally:
  docker run -d -p 8080:8080 --name stylobot stylobot-website:latest

To run with docker-compose:
  docker-compose up -d
```

If using `-Compress`:

```
========================================
Compressed Image Ready for Deployment!
========================================

File: /path/to/dist/stylobot-website-latest.tar.gz

To deploy on remote server:
  1. Copy to server: scp dist/stylobot-website-latest.tar.gz user@server:/tmp/
  2. On server: gunzip -c /tmp/stylobot-website-latest.tar.gz | docker load
  3. Run: docker-compose up -d
```

## Troubleshooting

### Build Fails with NPM Errors

**Problem**: `npm install` or `npm run build` fails during Docker build

**Solution**:
- Ensure `package.json` and `package-lock.json` are in sync
- Try building with `--no-cache` to force fresh npm install
- Check Node.js version in Dockerfile (currently v22.x)

### Image Size Too Large

**Problem**: Docker image is larger than expected

**Solution**:
- The multi-stage build should keep image size reasonable
- Check that `.dockerignore` file exists and excludes unnecessary files
- Verify final image doesn't include Node.js or build artifacts

### Compression Fails (PowerShell)

**Problem**: Gzip compression fails on Windows

**Solution**:
- Install gzip for Windows: `choco install gzip` or `scoop install gzip`
- Or use WSL: `wsl ./build-docker.sh -c`
- The script will fall back to .NET compression if gzip is not found

### Cannot Connect to Docker Daemon

**Problem**: Error about Docker daemon not running

**Solution**:
- Start Docker Desktop (Windows/Mac)
- Or start Docker service (Linux): `sudo systemctl start docker`
- Verify with: `docker ps`

## File Locations

- **Dockerfile**: `./Dockerfile`
- **Docker Compose**: `./docker-compose.yml`
- **Build Scripts**: `./build-docker.ps1`, `./build-docker.sh`
- **Output Directory**: `./dist/`
- **Caddyfile**: `./Caddyfile` (for reverse proxy)

## Stack Architecture

When deployed with docker-compose, the full stack includes:

```
Internet
    ↓
Caddy (SSL/TLS, ports 80/443)
    ↓
YARP Gateway (Bot Detection)
    ↓
Stylobot Website (ASP.NET Core)
```

All managed by Watchtower for automatic updates.

## CI/CD Integration

### GitHub Actions Example

```yaml
name: Build and Deploy

on:
  push:
    tags:
      - 'v*'

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Build Docker Image
        run: |
          ./build-docker.sh -t ${{ github.ref_name }} -c

      - name: Upload Artifact
        uses: actions/upload-artifact@v3
        with:
          name: docker-image
          path: dist/*.tar.gz
```

## Environment Variables

The following environment variables are used in production (docker-compose):

- `ASPNETCORE_ENVIRONMENT=Production`
- `ASPNETCORE_URLS=http://+:8080`
- `DEFAULT_UPSTREAM=http://website:8080` (Gateway)

## Health Checks

The Docker image includes a health check:

```dockerfile
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1
```

This ensures the container is healthy before routing traffic to it.

## Support

For issues with Docker builds or deployment:
1. Check this documentation
2. Review Dockerfile and docker-compose.yml
3. Check Docker logs: `docker logs stylobot-website`
4. Verify image built correctly: `docker images stylobot-website`
