#!/usr/bin/env bash
# WordPress honeypot — real bot traffic via Cloudflare quick tunnel.
# Runs WordPress in Docker, StyloBot Gateway locally, cloudflared tunnel.
# Learning mode enabled: heuristic weights update from live traffic.
#
# Usage: ./start-wp-honeypot.sh
# Stop:  ./start-wp-honeypot.sh stop

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATEWAY_DIR="$SCRIPT_DIR/src/Stylobot.Gateway"

WP_PORT=8081
GW_PORT=8080
ADMIN_SECRET="${ADMIN_SECRET:-devsecret-$(openssl rand -hex 8)}"

# ── stop ──────────────────────────────────────────────────────────────────────
if [[ "${1:-}" == "stop" ]]; then
    echo "Stopping..."
    pkill -f "Stylobot.Gateway.dll" 2>/dev/null && echo "  Gateway stopped" || true
    pkill -f "cloudflared tunnel" 2>/dev/null && echo "  Tunnel stopped" || true
    docker rm -f wordpress wp-db 2>/dev/null && echo "  WordPress stopped" || true
    docker network rm wp-net 2>/dev/null || true
    echo "Done."
    exit 0
fi

echo "=== StyloBot WordPress Honeypot ==="
echo ""

# ── WordPress in Docker ───────────────────────────────────────────────────────
echo "Starting WordPress..."
docker network create wp-net 2>/dev/null || true
docker rm -f wp-db wordpress 2>/dev/null || true

docker run -d --name wp-db --network wp-net \
    -e MYSQL_DATABASE=wordpress \
    -e MYSQL_USER=wp -e MYSQL_PASSWORD=secret \
    -e MYSQL_RANDOM_ROOT_PASSWORD=1 \
    mysql:8.0 > /dev/null

docker run -d --name wordpress --network wp-net \
    -p ${WP_PORT}:80 \
    -e WORDPRESS_DB_HOST=wp-db \
    -e WORDPRESS_DB_USER=wp -e WORDPRESS_DB_PASSWORD=secret \
    -e WORDPRESS_DB_NAME=wordpress \
    -e WORDPRESS_CONFIG_EXTRA="define('WP_HOME','http://localhost:${WP_PORT}'); define('WP_SITEURL','http://localhost:${WP_PORT}');" \
    wordpress:latest > /dev/null

echo "  WordPress: http://localhost:${WP_PORT}"

# Wait for MySQL to be ready, then complete the 5-minute install
echo -n "  Waiting for WordPress DB..."
for i in $(seq 1 30); do
    if curl -sf "http://localhost:${WP_PORT}/wp-admin/install.php" 2>/dev/null | grep -q "Information needed"; then
        echo " ready!"
        break
    fi
    sleep 2; echo -n "."
done
curl -s -X POST "http://localhost:${WP_PORT}/wp-admin/install.php?step=2" \
    --data-urlencode "weblog_title=Tech Solutions Blog" \
    --data-urlencode "user_name=admin" \
    --data-urlencode "admin_password=Adm!nS3cur3" \
    --data-urlencode "admin_password2=Adm!nS3cur3" \
    --data-urlencode "pw_weak=on" \
    --data-urlencode "admin_email=admin@techsolutions.local" \
    --data-urlencode "blog_public=1" \
    --data-urlencode "language=" > /dev/null
echo "  WordPress installed (user: admin / Adm!nS3cur3)"

# ── StyloBot Gateway ─────────────────────────────────────────────────────────
echo "Building and starting StyloBot Gateway (learning mode)..."

# learning mode: EnableWeightLearning + demo policy (all detectors)
ADMIN_SECRET="$ADMIN_SECRET" \
BotDetection__AiDetection__Heuristic__EnableWeightLearning=true \
BotDetection__AiDetection__Heuristic__LoadLearnedWeights=true \
BotDetection__LogAllRequests=true \
dotnet run --project "$GATEWAY_DIR" \
    --configuration Release \
    --no-launch-profile \
    -- --urls "http://localhost:${GW_PORT}" \
    > /tmp/stylobot-gateway.log 2>&1 &

GW_PID=$!
echo "  Gateway PID: $GW_PID (logs: /tmp/stylobot-gateway.log)"

# Wait for gateway to be ready
echo -n "  Waiting for gateway..."
for i in $(seq 1 30); do
    if curl -sf "http://localhost:${GW_PORT}/admin/alive" > /dev/null 2>&1; then
        echo " ready!"
        break
    fi
    sleep 1
    echo -n "."
done

# ── Cloudflare quick tunnel ───────────────────────────────────────────────────
echo "Starting Cloudflare tunnel..."
cloudflared tunnel --no-autoupdate --url "http://localhost:${GW_PORT}" \
    > /tmp/cloudflared.log 2>&1 &

CF_PID=$!
echo "  Tunnel PID: $CF_PID (logs: /tmp/cloudflared.log)"

# Extract tunnel URL (retry up to 15s)
echo -n "  Waiting for tunnel URL..."
TUNNEL_URL=""
for i in $(seq 1 15); do
    TUNNEL_URL=$(grep -o 'https://[a-z0-9-]*\.trycloudflare\.com' /tmp/cloudflared.log 2>/dev/null | head -1 || true)
    [[ -n "$TUNNEL_URL" ]] && break
    sleep 1
    echo -n "."
done
echo ""

echo ""
echo "=== Live ==="
echo ""
echo "  Public URL (bait):  ${TUNNEL_URL:-check /tmp/cloudflared.log}"
echo "  Dashboard:          http://localhost:${GW_PORT}/_stylobot"
echo "  WordPress admin:    http://localhost:${WP_PORT}/wp-admin"
echo "  Gateway logs:       tail -f /tmp/stylobot-gateway.log"
echo "  Tunnel logs:        tail -f /tmp/cloudflared.log"
echo ""
echo "  Stop everything:    ./start-wp-honeypot.sh stop"
echo ""
echo "Bots typically probe /wp-login.php and /xmlrpc.php within minutes."
echo "The wordpress-5.9 simulation pack serves realistic fake responses."
echo "Heuristic weights are being updated from every detection."
