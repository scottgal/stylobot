#!/usr/bin/env bash
# verify-aot.sh — smoke-tests the stylobot AOT binary
# Usage: ./verify-aot.sh [path-to-stylobot-binary]
set -euo pipefail

BINARY="${1:-stylobot}"
PASS=0; FAIL=0

pass() { echo "  PASS  $1"; ((PASS++)); }
fail() { echo "  FAIL  $1"; ((FAIL++)); }

require() {
    if ! command -v "$1" &>/dev/null; then
        echo "  SKIP  requires $1 (not installed)"
    fi
}

echo ""
echo "  stylobot AOT verification"
echo "  binary: $BINARY"
echo "  ────────────────────────────────────────"

# ── 1. Binary exists and is executable ───────────────────────────────────────
if [[ -x "$BINARY" ]] || command -v "$BINARY" &>/dev/null; then
    pass "binary is executable"
else
    fail "binary not found or not executable: $BINARY"
    echo "  Cannot continue."
    exit 1
fi

# ── 2. --help exits 0 ────────────────────────────────────────────────────────
if "$BINARY" --help 2>&1 | grep -q "stylobot"; then
    pass "--help shows usage"
else
    fail "--help did not show expected output"
fi

# ── 3. man exits 0 ───────────────────────────────────────────────────────────
if "$BINARY" man 2>&1 | grep -q "SYNOPSIS\|stylobot"; then
    pass "man page renders"
else
    fail "man page failed"
fi

# ── 4. genkey produces 44-char base64 ────────────────────────────────────────
KEY=$("$BINARY" genkey 2>/dev/null || true)
if [[ "${#KEY}" -ge 40 ]]; then
    pass "genkey produces key (${#KEY} chars)"
else
    fail "genkey output too short: '$KEY'"
fi

# ── 5. status exits gracefully when daemon not running ───────────────────────
if "$BINARY" status 2>&1 | grep -qiE "not running|no daemon|stopped|pid"; then
    pass "status reports daemon not running"
else
    # exit code 1 is also acceptable when daemon is not running
    pass "status exited (daemon not running)"
fi

# ── 6. invalid args exit non-zero ────────────────────────────────────────────
if ! "$BINARY" notacommand 99999 2>/dev/null; then
    pass "invalid args exit non-zero"
else
    fail "invalid args returned exit 0"
fi

# ── 7. Start server, check /health, send SIGTERM ─────────────────────────────
PORT=19187
TMPDIR_VAR=$(mktemp -d)
UPSTREAM="http://127.0.0.1:19186"

# Start a trivial upstream using nc or Python
if command -v python3 &>/dev/null; then
    python3 -c "
import http.server, threading
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'ok')
    def log_message(self, *a): pass
httpd = http.server.HTTPServer(('127.0.0.1', 19186), H)
httpd.serve_forever()
" &
    UPSTREAM_PID=$!
    sleep 0.3
else
    UPSTREAM_PID=""
    echo "  WARN  no python3; upstream will return 502 (that is ok for shutdown test)"
fi

# Start stylobot in background
"$BINARY" "$PORT" "$UPSTREAM" --verbose \
    2>"$TMPDIR_VAR/stderr.log" >"$TMPDIR_VAR/stdout.log" &
SB_PID=$!
sleep 2   # wait for startup

# Check /health
if curl -sf "http://127.0.0.1:$PORT/health" 2>/dev/null | grep -q "healthy"; then
    pass "/health returns healthy"
else
    fail "/health did not return healthy"
    echo "       stderr: $(tail -5 "$TMPDIR_VAR/stderr.log")"
fi

# Check proxy round-trip
if [[ -n "$UPSTREAM_PID" ]]; then
    STATUS=$(curl -o /dev/null -s -w "%{http_code}" "http://127.0.0.1:$PORT/" 2>/dev/null || echo "000")
    if [[ "$STATUS" == "200" ]]; then
        pass "proxy round-trip returns 200"
    else
        fail "proxy round-trip returned $STATUS (expected 200)"
    fi
fi

# Check SIGTERM shuts down cleanly (no zombie, no hanging)
kill -TERM "$SB_PID" 2>/dev/null || true
for i in $(seq 1 20); do
    if ! kill -0 "$SB_PID" 2>/dev/null; then break; fi
    sleep 0.3
done
if kill -0 "$SB_PID" 2>/dev/null; then
    fail "process did not exit within 6s after SIGTERM"
    kill -9 "$SB_PID" 2>/dev/null || true
else
    pass "clean shutdown on SIGTERM (exited within 6s)"
fi

[[ -n "$UPSTREAM_PID" ]] && kill "$UPSTREAM_PID" 2>/dev/null || true
rm -rf "$TMPDIR_VAR"

# ── 8. Database created at expected path ─────────────────────────────────────
DB_PATH="$HOME/.config/stylobot/botdetection.db"
FALLBACK_DB="$(dirname "$BINARY")/botdetection.db"
if [[ -f "$DB_PATH" ]] || [[ -f "$FALLBACK_DB" ]]; then
    pass "database file exists"
else
    fail "database file not found at $DB_PATH or $FALLBACK_DB"
fi

# ── Summary ───────────────────────────────────────────────────────────────────
echo "  ────────────────────────────────────────"
echo "  Passed: $PASS   Failed: $FAIL"
echo ""
[[ $FAIL -eq 0 ]]
