#!/usr/bin/env bash
# Run one k6 plateau soak against the .15 gateway for a single persistence backend,
# collect the k6 summary + gateway log, and print the key signals.
#
#   scripts/soak/run-backend-soak.sh <postgres|sqlite|current> [MAX_RPS]
#
# Companion to docs/soak-sqlite-vs-postgres-plan.md. The k6 drive + summary +
# log-collection are fully wired. The backend SWAP and store RESET are hooks
# (SWAP_CMD / RESET_CMD / LOG_CMD) that plug into deploy-'s .15 mechanism — until
# set, the run targets whatever backend is already deployed on :8190 (labelled
# "current"), so you can always at least drive load and read the signals.
#
# Env overrides:
#   TARGET     (default http://192.168.0.15:8190)
#   API_KEY    (default the staging learning-suppressed key)
#   SOAK_HOST / SSH_USER / SSH_PASS   (.15 ssh, defaults match scripts/restart-gateway.sh)
#   SWAP_CMD   remote command that reconfigures :8190 to $BACKEND and restarts it
#   RESET_CMD  remote command that wipes the store for a fresh baseline
#   LOG_CMD    remote command that prints the gateway log (collected post-run)
set -euo pipefail

BACKEND="${1:-current}"
MAX_RPS="${2:-300}"
TARGET="${TARGET:-http://192.168.0.15:8190}"
API_KEY="${API_KEY:-staging-test-website-key-do-not-use-elsewhere}"
SOAK_HOST="${SOAK_HOST:-192.168.0.15}"
SSH_USER="${SSH_USER:-claude}"
SSH_PASS="${SSH_PASS:-Cl4ude2026!}"
OUTDIR="${OUTDIR:-soak-results}"
STAMP="$(date +%Y%m%d-%H%M%S)"
LABEL="${BACKEND}-${STAMP}"
mkdir -p "$OUTDIR"

# HARD GUARD: :8190 is staging.stylobot.net's live gateway, not an isolated rig —
# soaking it puts load on real staging traffic. Never target it from this script.
# Point TARGET at a dedicated isolated commercial-gateway instance instead.
case "$TARGET" in
  *:8190*|*staging.stylobot.net*)
    echo "REFUSING: TARGET=$TARGET is staging (:8190 / staging.stylobot.net), not an isolated soak rig." >&2
    echo "Stand up a dedicated commercial gateway + fresh Postgres on its own port and point TARGET there." >&2
    exit 3
    ;;
esac

SSH_OPTS=(-o StrictHostKeyChecking=no -o PreferredAuthentications=password
          -o PubkeyAuthentication=no -o ConnectTimeout=8)
ssh15() { sshpass -p "$SSH_PASS" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SOAK_HOST" "$@"; }

swap_backend() {
  case "$BACKEND" in
    current) echo "[swap] BACKEND=current — no swap, testing whatever is deployed on :8190" ;;
    postgres|sqlite)
      if [ -n "${SWAP_CMD:-}" ]; then
        echo "[swap] switching :8190 to $BACKEND"
        ssh15 "BACKEND=$BACKEND ${SWAP_CMD}"
      else
        echo "[swap] WARNING: SWAP_CMD unset — cannot switch. Assuming :8190 is already $BACKEND."
        echo "       Set SWAP_CMD to deploy-'s recreate command to make this self-serve."
      fi ;;
    *) echo "[swap] unknown backend '$BACKEND' (use postgres|sqlite|current)"; exit 2 ;;
  esac
}

reset_store() {
  if [ -n "${RESET_CMD:-}" ]; then
    echo "[reset] fresh store for $BACKEND"
    ssh15 "BACKEND=$BACKEND ${RESET_CMD}"
  else
    echo "[reset] RESET_CMD unset — running against the warm store (baseline not reset)."
  fi
}

wait_healthy() {
  echo "[health] waiting for $TARGET/health ..."
  for _ in $(seq 1 30); do
    curl -sf -m3 "$TARGET/health" >/dev/null 2>&1 && { echo "[health] up"; return 0; }
    sleep 2
  done
  echo "[health] gateway not healthy after 60s — aborting"; exit 1
}

echo "=== soak: backend=$BACKEND  max_rps=$MAX_RPS  target=$TARGET ==="
swap_backend
reset_store
wait_healthy

k6 run scripts/soak/k6-plateau.js \
  --env TARGET="$TARGET" --env MAX_RPS="$MAX_RPS" --env API_KEY="$API_KEY" \
  --summary-export "$OUTDIR/soak-$LABEL-summary.json" | tee "$OUTDIR/soak-$LABEL-k6.log"

if [ -n "${LOG_CMD:-}" ]; then
  echo "[collect] gateway log -> $OUTDIR/soak-$LABEL-gateway.log"
  ssh15 "$LOG_CMD" > "$OUTDIR/soak-$LABEL-gateway.log" 2>&1 || true
fi

echo "=== key signals ($LABEL) ==="
jq -r '
  .metrics.http_req_waiting as $w
  | "http_req_waiting  p50=\($w.med|floor)ms  p95=\($w["p(95)"]|floor)ms  max=\($w.max|floor)ms"
  , "http_req_failed   \(((.metrics.http_req_failed.rate // 0)*100)|floor)%"
  , "iterations/s      \((.metrics.iterations.rate // 0)|floor)   dropped=\(.metrics.dropped_iterations.count // 0)"
' "$OUTDIR/soak-$LABEL-summary.json" 2>/dev/null \
  || echo "(couldn't parse summary — see $OUTDIR/soak-$LABEL-summary.json)"

if [ -f "$OUTDIR/soak-$LABEL-gateway.log" ]; then
  echo "=== backend failure signals (gateway log) ==="
  grep -icE 'pool has been exhausted|circuit' "$OUTDIR/soak-$LABEL-gateway.log" \
    | sed 's/^/  postgres pool-exhaust\/circuit lines: /' || true
  grep -icE 'database is locked|SQLITE_BUSY' "$OUTDIR/soak-$LABEL-gateway.log" \
    | sed 's/^/  sqlite busy\/locked lines: /' || true
fi
echo "artifacts: $OUTDIR/soak-$LABEL-*"
