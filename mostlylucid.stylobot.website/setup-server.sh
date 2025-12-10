#!/bin/bash
# Stylobot Server Setup Script
# This script creates the necessary directories and sets permissions

set -e  # Exit on error

echo "========================================="
echo "Stylobot Server Setup"
echo "========================================="

# Configuration
DEPLOY_DIR="/opt/stylobot"
DATA_DIR="${DEPLOY_DIR}/data"
CADDY_DATA_DIR="${DATA_DIR}/caddy-data"
CADDY_CONFIG_DIR="${DATA_DIR}/caddy-config"
BOT_DETECTION_DIR="${DATA_DIR}/bot-detection"

# Check if running as root or with sudo
if [ "$EUID" -ne 0 ]; then
    echo "Please run with sudo or as root"
    exit 1
fi

echo ""
echo "Creating deployment directories..."
echo "-----------------------------------"

# Create main deployment directory
mkdir -p "${DEPLOY_DIR}"
echo "✓ Created ${DEPLOY_DIR}"

# Create data directories
mkdir -p "${DATA_DIR}"
echo "✓ Created ${DATA_DIR}"

mkdir -p "${CADDY_DATA_DIR}"
echo "✓ Created ${CADDY_DATA_DIR}"

mkdir -p "${CADDY_CONFIG_DIR}"
echo "✓ Created ${CADDY_CONFIG_DIR}"

mkdir -p "${BOT_DETECTION_DIR}"
echo "✓ Created ${BOT_DETECTION_DIR}"

echo ""
echo "Setting permissions..."
echo "-----------------------------------"

# Set ownership to current user (the one who ran sudo)
ACTUAL_USER="${SUDO_USER:-$USER}"
ACTUAL_GROUP=$(id -gn "${ACTUAL_USER}")

chown -R "${ACTUAL_USER}:${ACTUAL_GROUP}" "${DEPLOY_DIR}"
echo "✓ Set ownership to ${ACTUAL_USER}:${ACTUAL_GROUP}"

# Set directory permissions
chmod -R 755 "${DEPLOY_DIR}"
echo "✓ Set directory permissions to 755"

# Set data directory permissions (needs to be writable by Docker containers)
chmod -R 777 "${DATA_DIR}"
echo "✓ Set data directory permissions to 777 (Docker writable)"

# Create logs directory
mkdir -p "${DEPLOY_DIR}/logs"
chmod 777 "${DEPLOY_DIR}/logs"
echo "✓ Created logs directory with write permissions"

echo ""
echo "Directory structure created:"
echo "-----------------------------------"
tree -L 3 "${DEPLOY_DIR}" 2>/dev/null || find "${DEPLOY_DIR}" -type d -print

echo ""
echo "========================================="
echo "Setup Complete!"
echo "========================================="
echo ""
echo "Next steps:"
echo "1. Copy deployment files to ${DEPLOY_DIR}/"
echo "   - docker-compose.yml"
echo "   - Caddyfile"
echo "   - .env"
echo ""
echo "2. Load Docker image:"
echo "   cd ${DEPLOY_DIR}"
echo "   docker load < stylobot-website-latest.tar.gz"
echo ""
echo "3. Start services:"
echo "   docker-compose up -d"
echo ""
