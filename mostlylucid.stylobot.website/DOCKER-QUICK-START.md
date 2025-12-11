# Docker Quick Start

## Build Commands

### Windows (PowerShell)
```powershell
# Basic build
.\build-docker.ps1

# Build for deployment (creates .tar.gz)
.\build-docker.ps1 -Compress

# Build specific version
.\build-docker.ps1 -Tag "v1.0.0" -Compress
```

### Linux/Mac/WSL (Bash)
```bash
# Basic build
./build-docker.sh

# Build for deployment (creates .tar.gz)
./build-docker.sh -c

# Build specific version
./build-docker.sh -t v1.0.0 -c
```

## Output

Compressed image will be saved to: `dist/stylobot-website-{tag}.tar.gz`

## Deploy to Server

```bash
# 1. Copy to server
scp dist/stylobot-website-v1.0.0.tar.gz user@server:/tmp/

# 2. On server: Load image
gunzip -c /tmp/stylobot-website-v1.0.0.tar.gz | docker load

# 3. Start services
docker-compose up -d
```

## Local Testing

```bash
# Run standalone
docker run -d -p 8080:8080 --name stylobot stylobot-website:latest

# Or with full stack
docker-compose up -d
```

See [DOCKER-BUILD.md](docs/DOCKER-BUILD.md) for complete documentation.
