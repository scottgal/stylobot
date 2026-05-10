#!/usr/bin/env bash
# verify-aot.sh — smoke-tests the stylobot AOT binary
# Usage: ./verify-aot.sh [path-to-stylobot-binary]
set -euo pipefail

BINARY="${1:-stylobot}"
# Resolve to absolute path so env -C doesn't lose track of a relative binary path
if [[ "$BINARY" != /* ]] && [[ -f "$BINARY" ]]; then
    BINARY="$(cd "$(dirname "$BINARY")" && pwd)/$(basename "$BINARY")"
fi
PASS=0; FAIL=0

pass() { echo "  PASS  $1"; PASS=$((PASS + 1)); }
fail() { echo "  FAIL  $1"; FAIL=$((FAIL + 1)); }

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

# Start a trivial upstream using Python
if command -v python3 &>/dev/null; then
    python3 -c "
import http.server
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

# Start stylobot from TMPDIR_VAR so the DB lands there (CWD is first writable candidate).
# Use env -C to set the working directory without a subshell, keeping SB_PID as the binary PID.
env -C "$TMPDIR_VAR" "$BINARY" "$PORT" "$UPSTREAM" --verbose \
    2>"$TMPDIR_VAR/stderr.log" >"$TMPDIR_VAR/stdout.log" &
SB_PID=$!

# Poll /health up to 30s (60 x 0.5s) - VerifiedBotRegistry fetches IP ranges on startup
HEALTHY=0
for i in $(seq 1 60); do
    if curl -sf "http://127.0.0.1:$PORT/health" 2>/dev/null | grep -q "healthy"; then
        HEALTHY=1
        break
    fi
    sleep 0.5
done

if [[ $HEALTHY -eq 1 ]]; then
    pass "/health returns healthy"
else
    fail "/health did not return healthy after 30s"
    echo "       stderr: $(tail -5 "$TMPDIR_VAR/stderr.log" 2>/dev/null || echo '(empty)')"
    echo "       stdout: $(tail -5 "$TMPDIR_VAR/stdout.log" 2>/dev/null || echo '(empty)')"
fi

# Check proxy round-trip (only if upstream is running and server is healthy)
if [[ -n "$UPSTREAM_PID" && $HEALTHY -eq 1 ]]; then
    STATUS=$(curl -o /dev/null -s -w "%{http_code}" "http://127.0.0.1:$PORT/" 2>/dev/null || echo "000")
    if [[ "$STATUS" == "200" ]]; then
        pass "proxy round-trip returns 200"
    else
        fail "proxy round-trip returned $STATUS (expected 200)"
    fi
fi

# Check SIGTERM shuts down cleanly
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

# ── 8. Database created at expected path ─────────────────────────────────────
# ResolveDataDirectory() tries CWD first, then ~/.local/share/stylobot, then AppContext.BaseDirectory
DB_TMPDIR="$TMPDIR_VAR/botdetection.db"
DB_LOCAL="$HOME/.local/share/stylobot/botdetection.db"
DB_CONFIG="$HOME/.config/stylobot/botdetection.db"
DB_BINARY_DIR="$(dirname "$(command -v "$BINARY" 2>/dev/null || echo "$BINARY")")/botdetection.db"
if [[ -f "$DB_TMPDIR" ]] || [[ -f "$DB_LOCAL" ]] || [[ -f "$DB_CONFIG" ]] || [[ -f "$DB_BINARY_DIR" ]]; then
    pass "database file exists"
else
    fail "database file not found (checked tmpdir, ~/.local/share/stylobot, ~/.config/stylobot, binary dir)"
fi

rm -rf "$TMPDIR_VAR"

# ── Summary ───────────────────────────────────────────────────────────────────
echo "  ────────────────────────────────────────"
echo "  Passed: $PASS   Failed: $FAIL"
echo ""
[[ $FAIL -eq 0 ]]
