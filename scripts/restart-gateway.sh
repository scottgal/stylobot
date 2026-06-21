#!/usr/bin/env bash
# Restart the gateway via SSH-as-babysitter. The SSH session itself keeps the
# Windows-side cmd alive, which keeps the gateway alive. This sidesteps
# Windows daemon-detachment complexity (CREATE_NEW_PROCESS_GROUP / Services /
# Task Scheduler) which would require P/Invoke or admin.
#
# The Node.js upstream is launched the same way in a separate SSH session.
#
# Usage: restart-gateway.sh [PROFILE] [MODE]
#   PROFILE: balanced (default) | api | site | highrisk | economy
#   MODE:    demo (default) | production
#
# Env opts:
#   WORKSTATION_GC=1  set DOTNET_GCServer=0 (smaller heap, better for Pi-class)
set -euo pipefail

PROFILE=${1:-balanced}
MODE=${2:-demo}
HOST=${HOST:-192.168.0.15}
SSH_USER=${SSH_USER:-claude}
SSH_PASS=${SSH_PASS:-Cl4ude2026!}
PORT=${PORT:-5080}
UPSTREAM_PORT=${UPSTREAM_PORT:-9999}
WORKSTATION_GC=${WORKSTATION_GC:-0}

PID_DIR="${HOME}/.config/stylobot-perf"
mkdir -p "$PID_DIR"
GW_PIDFILE="$PID_DIR/gateway-ssh.pid"
UPSTREAM_PIDFILE="$PID_DIR/upstream-ssh.pid"

SSH_OPTS="-o StrictHostKeyChecking=no -o NumberOfPasswordPrompts=1 -o PreferredAuthentications=password -o PubkeyAuthentication=no -o ServerAliveInterval=30 -o ServerAliveCountMax=600"
SSH="sshpass -p $SSH_PASS ssh $SSH_OPTS"

stop_pidfile() {
    local pidfile=$1
    local label=$2
    if [ -f "$pidfile" ]; then
        local pid
        pid=$(cat "$pidfile")
        if kill -0 "$pid" 2>/dev/null; then
            echo "  Stopping $label (Mac SSH PID $pid)..."
            kill "$pid" 2>/dev/null || true
            sleep 1
            kill -9 "$pid" 2>/dev/null || true
        fi
        rm -f "$pidfile"
    fi
}

echo "=== Stop prior gateway + upstream ==="
stop_pidfile "$GW_PIDFILE" "gateway"
stop_pidfile "$UPSTREAM_PIDFILE" "upstream"

echo "=== Force-kill any stray Windows-side processes ==="
$SSH $SSH_USER@$HOST 'cmd /c "taskkill /F /IM stylobot.exe /T 2>nul & taskkill /F /IM node.exe /T 2>nul & taskkill /F /IM node20.exe /T 2>nul"' 2>&1 | tail -3 || true
sleep 2

echo "=== Wipe SQLite for clean baseline ==="
$SSH $SSH_USER@$HOST 'cmd /c "cd C:\build\stylobot-aot-run && del /Q *.db 2>nul & del /Q *.db-wal 2>nul & del /Q *.db-shm 2>nul"' 2>&1 || true

echo "=== Start Node upstream on :$UPSTREAM_PORT (SSH-babysat) ==="
$SSH $SSH_USER@$HOST \
    "cmd /c \"set PORT=$UPSTREAM_PORT && node C:\\build\\stylobot-aot-run\\upstream-stub.js > C:\\build\\stylobot-aot-run\\upstream.log 2>&1\"" 2>&1 &
echo $! > "$UPSTREAM_PIDFILE"
echo "  Mac SSH PID $(cat "$UPSTREAM_PIDFILE")"
sleep 3

GC_ENV=""
if [ "$WORKSTATION_GC" = "1" ]; then
    GC_ENV="set DOTNET_GCServer=0 && set DOTNET_GCConcurrent=1 && set DOTNET_GCRetainVM=0 && "
    echo "  GC: Workstation (DOTNET_GCServer=0)"
fi

echo "=== Start gateway on :$PORT (SSH-babysat, profile=$PROFILE mode=$MODE) ==="
$SSH $SSH_USER@$HOST \
    "cmd /c \"cd C:\\build\\stylobot-aot-run && set STYLOBOT_PROFILE=$PROFILE && set SignatureLogging__SignatureHashKey=ItoH7zOanEtZDPSAg7/y0VOhjwsQNJ07Lw22SDWDoPs= && ${GC_ENV}stylobot.exe $PORT http://127.0.0.1:$UPSTREAM_PORT --mode $MODE --verbose > C:\\build\\stylobot-aot-run\\stylobot.fg.log 2>&1\"" 2>&1 &
echo $! > "$GW_PIDFILE"
echo "  Mac SSH PID $(cat "$GW_PIDFILE")"

echo "=== Wait for healthy ==="
URL="http://$HOST:$PORT"
for i in $(seq 1 45); do
    code=$(curl -s -o /dev/null -w "%{http_code}" -m 4 "$URL/health" 2>/dev/null || echo "000")
    if [ "$code" = "200" ]; then
        echo "  attempt $i: 200 OK"
        echo ""
        echo "Gateway ready on $URL (profile=$PROFILE mode=$MODE)"
        echo "  Stop: kill \$(cat $GW_PIDFILE) \$(cat $UPSTREAM_PIDFILE)"
        exit 0
    fi
    sleep 2
done
echo "Gateway never returned 200" >&2
exit 1
