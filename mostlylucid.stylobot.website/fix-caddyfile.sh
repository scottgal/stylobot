#!/bin/bash
# Quick fix for Caddyfile mount issue

DEPLOY_DIR="/opt/stylobot"

echo "Fixing Caddyfile mount issue..."

# Stop containers if running
echo "Stopping containers..."
cd "$DEPLOY_DIR" && docker-compose down 2>/dev/null

# Check if Caddyfile is a directory (incorrect)
if [ -d "$DEPLOY_DIR/Caddyfile" ]; then
    echo "❌ Caddyfile is a directory! Removing..."
    rm -rf "$DEPLOY_DIR/Caddyfile"
fi

# Ensure Caddyfile exists as a file
if [ ! -f "$DEPLOY_DIR/Caddyfile" ]; then
    echo "❌ Caddyfile doesn't exist!"
    echo "Please copy Caddyfile to $DEPLOY_DIR/"
    echo "  scp Caddyfile user@server:$DEPLOY_DIR/"
    exit 1
fi

# Verify it's a file
if [ -f "$DEPLOY_DIR/Caddyfile" ]; then
    echo "✓ Caddyfile exists and is a file"
    echo "File size: $(wc -c < "$DEPLOY_DIR/Caddyfile") bytes"

    # Show first few lines
    echo ""
    echo "First 5 lines of Caddyfile:"
    head -5 "$DEPLOY_DIR/Caddyfile"
else
    echo "❌ Caddyfile is not a regular file!"
    exit 1
fi

# Check data directories
echo ""
echo "Checking data directories..."
mkdir -p "$DEPLOY_DIR/data/caddy-data"
mkdir -p "$DEPLOY_DIR/data/caddy-config"
mkdir -p "$DEPLOY_DIR/data/bot-detection"
chmod -R 777 "$DEPLOY_DIR/data"
echo "✓ Data directories ready"

# Restart containers
echo ""
echo "Starting containers..."
cd "$DEPLOY_DIR" && docker-compose up -d

echo ""
echo "Done! Check logs with: docker-compose logs -f"
