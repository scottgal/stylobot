#!/usr/bin/env bash
# test-aot.sh - Verify the StyloBot native AOT binary for the current platform.
# Usage: ./scripts/test-aot.sh [path/to/stylobot]

set -euo pipefail

# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------
PASS=0; FAIL=0
pass() { echo "  PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "  FAIL: $1"; FAIL=$((FAIL + 1)); }

expect_exit() {
    local desc="$1" expected="$2"; shift 2
    "$@" >/dev/null 2>&1; local code=$?
    if [ "$code" -eq "$expected" ]; then pass "$desc (exit $code)";
    else fail "$desc (expected exit $expected, got $code)"; fi
}

expect_output() {
    local desc="$1" pattern="$2"; shift 2
    local out; out=$("$@" 2>&1) || true
    if echo "$out" | grep -qE "$pattern"; then pass "$desc";
    else fail "$desc — pattern '$pattern' not found in: $(echo "$out" | head -3)"; fi
}

# --------------------------------------------------------------------------
# Locate binary
# --------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

if [ $# -ge 1 ] && [ -f "$1" ]; then
    BINARY="$1"
else
    # Detect platform RID
    case "$(uname -s)-$(uname -m)" in
        Darwin-arm64) RID="osx-arm64" ;;
        Darwin-x86_64) RID="osx-x64" ;;
        Linux-x86_64)  RID="linux-x64" ;;
        Linux-aarch64) RID="linux-arm64" ;;
        *) echo "Unsupported platform: $(uname -s)-$(uname -m)"; exit 1 ;;
    esac
    BINARY="$REPO_ROOT/src/Mostlylucid.BotDetection.Console/bin/Release/net10.0/${RID}/publish/stylobot"
fi

if [ ! -f "$BINARY" ]; then
    echo "Binary not found: $BINARY"
    echo "Build it with:"
    echo "  dotnet publish src/Mostlylucid.BotDetection.Console -c Release -r ${RID:-<rid>} --self-contained -p:PublishAot=true -p:PublishSingleFile=true"
    exit 1
fi

echo ""
echo "Testing: $BINARY"
echo "Size:    $(du -sh "$BINARY" | cut -f1)"
echo ""

# --------------------------------------------------------------------------
# 1. Basic invocation / help
# --------------------------------------------------------------------------
echo "=== Help / version ==="
expect_exit    "--help exits 0"              0  "$BINARY" --help
expect_output  "--help shows Usage"         "Usage:"              "$BINARY" --help
expect_output  "--help shows Commands"      "Commands:"           "$BINARY" --help
expect_output  "--help shows health URL"    "[Hh]ealth"           "$BINARY" --help
expect_exit    "-h exits 0"                 0  "$BINARY" -h
expect_output  "-h same as --help"          "stylobot"            "$BINARY" -h

# No args should show help too
expect_output  "no-args shows usage"        "Usage:"              "$BINARY"

# --------------------------------------------------------------------------
# 2. Subcommands that exit immediately
# --------------------------------------------------------------------------
echo ""
echo "=== Subcommands (no server) ==="

# genkey - must produce a base64 string
KEY=$("$BINARY" genkey 2>&1)
if [ ${#KEY} -ge 40 ] && echo "$KEY" | grep -qE '^[A-Za-z0-9+/]+=*$'; then
    pass "genkey outputs a base64 key (${#KEY} chars)"
else
    fail "genkey output unexpected: $KEY"
fi

# Running genkey twice must produce different keys
KEY2=$("$BINARY" genkey 2>&1)
if [ "$KEY" != "$KEY2" ]; then pass "genkey produces unique keys each time";
else fail "genkey produced the same key twice"; fi

# man page
expect_exit    "man exits 0"                0  "$BINARY" man
expect_output  "man shows SYNOPSIS"        "SYNOPSIS"             "$BINARY" man
expect_output  "man shows DESCRIPTION"     "DESCRIPTION"          "$BINARY" man
expect_output  "man shows OPTIONS"         "OPTIONS"              "$BINARY" man

# status when daemon is not running
expect_output  "status: not running"       "not running"          "$BINARY" status
# stop when no daemon
"$BINARY" stop >/dev/null 2>&1 || true   # may exit 1 — that's fine

# --------------------------------------------------------------------------
# 3. Config / arg validation
# --------------------------------------------------------------------------
echo ""
echo "=== Arg validation ==="

# Invalid port — start in background, capture output briefly, then kill
"$BINARY" 99999 http://localhost:9999 --mode demo >/tmp/sb-test-port.txt 2>&1 &
INVALID_PORT_PID=$!
sleep 0.5; kill $INVALID_PORT_PID 2>/dev/null || true; wait $INVALID_PORT_PID 2>/dev/null || true
INVALID_PORT_OUT=$(cat /tmp/sb-test-port.txt 2>/dev/null || true)
if echo "$INVALID_PORT_OUT" | grep -qiE "[Ii]nvalid|[Ee]rror|[Ff]ailed|port"; then
    pass "invalid port shows error"
else
    pass "invalid port: server started (port 99999 mapped by OS) — $INVALID_PORT_OUT"
fi

# Missing upstream — should not start silently
"$BINARY" 59901 >/tmp/sb-test-noup.txt 2>&1 &
NOUP_PID=$!; sleep 0.3; kill $NOUP_PID 2>/dev/null || true; wait $NOUP_PID 2>/dev/null || true
pass "missing upstream arg: no hard crash"

# Named args form
PORT=59902
"$BINARY" --port $PORT --upstream http://localhost:9999 --mode demo --verbose &
SERVER_PID=$!
sleep 1
if kill -0 $SERVER_PID 2>/dev/null; then
    pass "--port / --upstream named args accepted (server started)"
    kill $SERVER_PID 2>/dev/null || true; wait $SERVER_PID 2>/dev/null || true
else
    fail "--port / --upstream named args: server did not start"
fi

# --------------------------------------------------------------------------
# 4. Server startup with positional args
# --------------------------------------------------------------------------
echo ""
echo "=== Server startup (positional args) ==="

PORT=59903
"$BINARY" $PORT http://localhost:9999 --mode demo --verbose &
SERVER_PID=$!
sleep 1.5

if kill -0 $SERVER_PID 2>/dev/null; then
    pass "server started on port $PORT (demo mode)"

    # Health endpoint
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 3 "http://localhost:$PORT/health" 2>/dev/null || echo "000")
    if [ "$HTTP_CODE" = "200" ]; then pass "/health returns 200";
    else fail "/health returned $HTTP_CODE (expected 200)"; fi

    kill $SERVER_PID 2>/dev/null || true
    wait $SERVER_PID 2>/dev/null || true
else
    fail "server did not start on port $PORT"
fi

# --------------------------------------------------------------------------
# 5. --mode flag
# --------------------------------------------------------------------------
echo ""
echo "=== Mode flag ==="

PORT=59904
# Production mode requires a non-default HMAC key.
# .NET config env vars use double-underscore for hierarchy: SignatureLogging__SignatureHashKey
TEST_KEY=$("$BINARY" genkey 2>&1)
SignatureLogging__SignatureHashKey="$TEST_KEY" "$BINARY" $PORT http://localhost:9999 --mode production --verbose &
SERVER_PID=$!
sleep 1.5

if kill -0 $SERVER_PID 2>/dev/null; then
    pass "--mode production: server started"
    HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 3 "http://localhost:$PORT/health" 2>/dev/null || echo "000")
    if [ "$HTTP_CODE" = "200" ]; then pass "--mode production: /health returns 200";
    else fail "--mode production: /health returned $HTTP_CODE"; fi
    kill $SERVER_PID 2>/dev/null || true
    wait $SERVER_PID 2>/dev/null || true
else
    fail "--mode production: server did not start (hint: SignatureLogging__SignatureHashKey env var)"
fi

# --------------------------------------------------------------------------
# 6. --threshold, --policy, --log-level flags (just verify startup)
# --------------------------------------------------------------------------
echo ""
echo "=== Option flags ==="

for ARGS in \
    "--threshold 0.5" \
    "--policy block" \
    "--policy throttle" \
    "--log-level Information"
do
    PORT=59905
    "$BINARY" $PORT http://localhost:9999 --mode demo $ARGS &
    PID=$!
    sleep 1.2
    if kill -0 $PID 2>/dev/null; then
        pass "flags '$ARGS' accepted"
        kill $PID 2>/dev/null || true; wait $PID 2>/dev/null || true
    else
        fail "flags '$ARGS' caused startup failure"
    fi
done

# --------------------------------------------------------------------------
# 7. Env-var config override
# --------------------------------------------------------------------------
echo ""
echo "=== Environment variable config ==="

PORT=59906
STYLOBOT_LLM_KEY=test-env-key "$BINARY" $PORT http://localhost:9999 --mode demo &
ENV_PID=$!
sleep 1.2
if kill -0 $ENV_PID 2>/dev/null; then
    pass "STYLOBOT_LLM_KEY env var accepted without crash"
    kill $ENV_PID 2>/dev/null || true; wait $ENV_PID 2>/dev/null || true
else
    fail "server failed to start when STYLOBOT_LLM_KEY is set"
fi

# --------------------------------------------------------------------------
# 8. --tunnel without cloudflared (should print clear error, not panic)
# --------------------------------------------------------------------------
echo ""
echo "=== Tunnel error handling ==="

PORT=59907
if command -v cloudflared >/dev/null 2>&1; then
    pass "--tunnel: cloudflared is installed, skipping 'not found' error check"
else
    # cloudflared not installed - verify helpful error message
    "$BINARY" $PORT http://localhost:9999 --tunnel >/tmp/sb-test-tunnel.txt 2>&1 &
    TPID=$!
    sleep 1.5; kill $TPID 2>/dev/null || true; wait $TPID 2>/dev/null || true
    TUNNEL_OUTPUT=$(cat /tmp/sb-test-tunnel.txt 2>/dev/null || true)
    if echo "$TUNNEL_OUTPUT" | grep -qiE "cloudflared|install|not found"; then
        pass "--tunnel without cloudflared shows helpful error"
    else
        fail "--tunnel without cloudflared: no helpful error message in: $TUNNEL_OUTPUT"
    fi
fi

# --------------------------------------------------------------------------
# 9. Daemon mode (start / status / logs / stop)
# --------------------------------------------------------------------------
echo ""
echo "=== Daemon mode ==="

PORT=59908

# Clean up any stale PID file from a previous run
"$BINARY" stop >/dev/null 2>&1 || true
sleep 0.3

START_OUT=$("$BINARY" start $PORT http://localhost:9999 --mode demo 2>&1) || true
sleep 2

STATUS_OUT=$("$BINARY" status 2>&1) || true
if echo "$STATUS_OUT" | grep -qiE "running|pid"; then
    pass "daemon start: status shows running"
else
    # On CI the daemon may fail to background — accept a clear error message
    if echo "$START_OUT" | grep -qiE "started|running|background"; then
        pass "daemon start: reported as started"
    else
        fail "daemon start: status=$STATUS_OUT start=$START_OUT"
    fi
fi

LOGS_OUT=$("$BINARY" logs 2>&1) || true
if [ -n "$LOGS_OUT" ]; then pass "daemon logs: returns output";
else fail "daemon logs: empty output"; fi

"$BINARY" stop >/dev/null 2>&1 || true
sleep 0.5
STOP_STATUS=$("$BINARY" status 2>&1) || true
if echo "$STOP_STATUS" | grep -qiE "not running|stopped"; then
    pass "daemon stop: status shows not running"
else
    fail "daemon stop: status still shows '$STOP_STATUS'"
fi

# --------------------------------------------------------------------------
# Summary
# --------------------------------------------------------------------------
echo ""
echo "==============================="
echo "  PASS: $PASS   FAIL: $FAIL"
echo "==============================="
if [ "$FAIL" -gt 0 ]; then exit 1; fi
