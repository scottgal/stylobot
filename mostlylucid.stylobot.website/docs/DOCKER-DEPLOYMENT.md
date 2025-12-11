# Docker Deployment with GitHub Actions

This repository is configured to automatically build and push Docker images to Docker Hub using GitHub Actions.

## Setup

### 1. Docker Hub Credentials

You need to configure two GitHub secrets for Docker Hub authentication:

#### Required Secrets

1. **DOCKER_HUB_USERNAME**
   - Your Docker Hub username
   - Go to: Repository Settings → Secrets and variables → Actions → New repository secret
   - Name: `DOCKER_HUB_USERNAME`
   - Value: Your Docker Hub username (e.g., `scottgal`)

2. **DOCKER_HUB_TOKEN**
   - Your Docker Hub API token (NOT your password)
   - Create a token at: https://hub.docker.com/settings/security → New Access Token
   - Token description: "GitHub Actions - Stylobot Site"
   - Permissions: Read & Write (or Read, Write, Delete)
   - Go to: Repository Settings → Secrets and variables → Actions → New repository secret
   - Name: `DOCKER_HUB_TOKEN`
   - Value: The API token you just created

### 2. Trigger the Workflow

The workflow automatically triggers on:
- **Push to main branch**: Builds and pushes with `latest` tag
- **Git tags starting with 'v'**: Builds and pushes with version tags (e.g., `v1.0.0` → `1.0.0`, `1.0`, `1`)
- **Manual trigger**: Via GitHub Actions UI

#### Manual Trigger
1. Go to Actions tab in GitHub
2. Select "Build and Push Docker Image" workflow
3. Click "Run workflow"

#### Tag-based Release
```bash
# Create and push a version tag
git tag v1.0.0
git push origin v1.0.0
```

This will create images tagged as:
- `scottgal/stylobot-site:1.0.0`
- `scottgal/stylobot-site:1.0`
- `scottgal/stylobot-site:1`
- `scottgal/stylobot-site:latest` (if on main branch)

## Docker Image Repository

Images are pushed to: **scottgal/stylobot-site**

View at: https://hub.docker.com/r/scottgal/stylobot-site

## Running the Image

### Pull and Run
```bash
# Pull latest
docker pull scottgal/stylobot-site:latest

# Run the container
docker run -d \
  -p 8080:8080 \
  --name stylobot-site \
  scottgal/stylobot-site:latest
```

### With Docker Compose
```yaml
version: '3.8'
services:
  web:
    image: scottgal/stylobot-site:latest
    ports:
      - "8080:8080"
    restart: unless-stopped
```

## Build Cache

The workflow uses GitHub Actions cache to speed up builds:
- Cache is automatically managed by Docker Buildx
- Subsequent builds reuse layers from previous builds
- This significantly reduces build time

## Troubleshooting

### Build Fails
1. Check the Actions tab for detailed logs
2. Verify the Dockerfile is valid
3. Ensure all dependencies are properly specified

### Push Fails
1. Verify `DOCKER_HUB_USERNAME` and `DOCKER_HUB_TOKEN` secrets are set correctly
2. Check that the Docker Hub token has Write permissions
3. Verify the token hasn't expired

### Image Not Found on Docker Hub
1. Confirm the workflow completed successfully
2. Check that you're using the correct image name: `scottgal/stylobot-site`
3. Verify the tag you're pulling exists

## Workflow File

The workflow configuration is located at: `.github/workflows/docker-build-push.yml`