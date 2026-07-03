#!/bin/bash
# StyloBot Community Edition Installer
# Usage: curl -fsSL https://stylo.bot/install.sh | bash
#
# This script downloads the latest StyloBot console gateway binary
# for your platform and installs it to /usr/local/bin/stylobot

set -euo pipefail

VERSION="${STYLOBOT_VERSION:-latest}"
INSTALL_DIR="${STYLOBOT_INSTALL_DIR:-/usr/local/bin}"
REPO="scottgal/stylobot"

# Detect platform
OS=$(uname -s | tr '[:upper:]' '[:lower:]')
ARCH=$(uname -m)

case "$OS" in
    linux)  PLATFORM="linux" ;;
    darwin) PLATFORM="osx" ;;
    *)      echo "Unsupported OS: $OS"; exit 1 ;;
esac

case "$ARCH" in
    x86_64|amd64)   ARCH_SUFFIX="x64" ;;
    aarch64|arm64)   ARCH_SUFFIX="arm64" ;;
    *)               echo "Unsupported architecture: $ARCH"; exit 1 ;;
esac

ASSET_NAME="stylobot-${PLATFORM}-${ARCH_SUFFIX}.tar.gz"

echo "========================================"
echo "  StyloBot · Free Forever"
echo "========================================"
echo ""
echo "Platform: ${PLATFORM}-${ARCH_SUFFIX}"

# Get latest version if not specified
if [ "$VERSION" = "latest" ]; then
    echo "Finding latest release..."
    VERSION=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" | grep '"tag_name"' | sed 's/.*"tag_name": "console-v\(.*\)".*/\1/' || echo "")
    if [ -z "$VERSION" ]; then
        # Fallback: try release list
        VERSION=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases" | grep '"tag_name": "console-v' | head -1 | sed 's/.*"console-v\(.*\)".*/\1/' || echo "5.6.3")
    fi
fi

echo "Version: ${VERSION}"
DOWNLOAD_URL="https://github.com/${REPO}/releases/download/console-v${VERSION}/${ASSET_NAME}"
echo "URL: ${DOWNLOAD_URL}"
echo ""

# Download
TEMP_DIR=$(mktemp -d)
echo "Downloading..."
curl -fsSL -o "${TEMP_DIR}/${ASSET_NAME}" "$DOWNLOAD_URL"

# Extract
echo "Extracting..."
tar xzf "${TEMP_DIR}/${ASSET_NAME}" -C "${TEMP_DIR}"

# Install
echo "Installing to ${INSTALL_DIR}/stylobot..."
if [ -w "$INSTALL_DIR" ]; then
    cp "${TEMP_DIR}/stylobot" "${INSTALL_DIR}/stylobot"
    chmod +x "${INSTALL_DIR}/stylobot"
else
    sudo cp "${TEMP_DIR}/stylobot" "${INSTALL_DIR}/stylobot"
    sudo chmod +x "${INSTALL_DIR}/stylobot"
fi

# Copy config if present
if [ -f "${TEMP_DIR}/appsettings.json" ]; then
    mkdir -p "${HOME}/.config/stylobot"
    cp "${TEMP_DIR}/appsettings.json" "${HOME}/.config/stylobot/appsettings.json"
fi

# Cleanup
rm -rf "${TEMP_DIR}"

echo ""
echo "StyloBot v${VERSION} installed successfully!"
echo ""
echo "Quick start:"
echo "  stylobot                                    # Demo mode"
echo "  DEFAULT_UPSTREAM=http://localhost:3000 stylobot --mode production"
echo ""
echo "Dashboard:  http://localhost:5080/_stylobot"
echo "Docs:       https://github.com/scottgal/stylobot"
echo "Commercial: https://stylo.bot/pricing"
echo ""
echo "Install StyloBot Community in under 60 seconds. Upgrade only when you need scale."
