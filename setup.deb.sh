#!/bin/sh
# StyloBot apt repository setup (GitHub Pages static repo).
#
#   curl -fsSL https://pages.stylo.bot/setup.deb.sh | sudo sh
#
# Adds the StyloBot apt repo + signing key, then you can:
#   sudo apt-get update && sudo apt-get install stylobot
set -e

REPO_URL="https://pages.stylo.bot"
KEYRING="/usr/share/keyrings/stylobot.gpg"
LIST="/etc/apt/sources.list.d/stylobot.list"

# Need curl or wget for the key + a working sudo/root.
if command -v curl >/dev/null 2>&1; then
  FETCH="curl -fsSL"
elif command -v wget >/dev/null 2>&1; then
  FETCH="wget -qO-"
else
  echo "stylobot: need curl or wget to fetch the signing key" >&2
  exit 1
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi

# The published key is a binary GPG keyring, referenced directly by signed-by.
$FETCH "${REPO_URL}/stylobot.gpg" | $SUDO tee "$KEYRING" >/dev/null

echo "deb [signed-by=${KEYRING}] ${REPO_URL} stable main" | $SUDO tee "$LIST" >/dev/null

echo "StyloBot apt repo configured at ${REPO_URL}."
echo "Install with: ${SUDO} apt-get update && ${SUDO} apt-get install stylobot"
