#!/usr/bin/env bash
# Long-period compression soak: verify the detection fold's STEADY STATE under
# sustained load. The detections table must plateau — the fold fuses
# low-importance aged rows and retention erases past the window, so row count
# and DB file size stop growing linearly once the fold has caught up with the
# load, while every dashboard read stays exact.
#
#   scripts/soak/run-compression-soak.sh [DURATION_HOURS] [RPS]
#
# Preconditions (the rig operator — deploy- — sets these):
#   * TARGET points at an ISOLATED gateway running THIS branch with
#     TemporalStore:CompressionEnabled=true (the fold on Tick5m) and the
#     default SQLite store. The script REFUSES :8190 / staging.stylobot.net.
#   * DB_STATS_CMD: SSH command that prints "<row_count> <db_bytes>" for the
#     gateway's dashboard.db (defaults to a sqlite3 invocation on the SOAK_HOST
#     path; override for your rig).
#   * API_KEY: a debug/ops key (DisableLearningWrites) — soak traffic must not
#     train the model (feedback_always_api_key_on_stylobot_traffic). Defaults to
#     the Infisical staging gateway-debug-api-key (/stylobot-gateway), fetched
#     in-process; the script refuses to soak keyless.
#
# Env overrides:
#   TARGET      (default http://192.168.0.15:8190 — override for your isolated rig)
#   API_KEY     (default the staging learning-suppressed key)
#   SOAK_HOST / SSH_USER / SSH_PASS   (defaults match scripts/restart-gateway.sh)
#   DB_STATS_CMD                      (row-count + size sampler over SSH)
#   SAMPLE_MINUTES (default 15 — one sample per window)
#   OUTDIR      (default soak-results)
set -euo pipefail

DURATION_HOURS="${1:-6}"
RPS="${2:-150}"
TARGET="${TARGET:-http://192.168.0.15:8190}"
SOAK_HOST="${SOAK_HOST:-192.168.0.15}"
SSH_USER="${SSH_USER:-claude}"
SSH_PASS="${SSH_PASS:-Cl4ude2026!}"
SAMPLE_MINUTES="${SAMPLE_MINUTES:-15}"
OUTDIR="${OUTDIR:-soak-results}"
STAMP="$(date +%Y%m%d-%H%M%S)"
LABEL="compression-${STAMP}"
mkdir -p "$OUTDIR"

# HARD RULE: every request must carry X-SB-Api-Key — keyless traffic poisons
# the detection corpus (feedback_always_api_key_on_stylobot_traffic). When
# API_KEY is not overridden, fetch the gateway debug key from Infisical staging
# (path /stylobot-gateway, key gateway-debug-api-key) IN-PROCESS; never log or
# echo it. Refuse to soak keyless if the fetch fails.
if [ -z "${API_KEY:-}" ]; then
  API_KEY="$(infisical secrets get gateway-debug-api-key --env=staging --path=/stylobot-gateway --plain 2>/dev/null || true)"
fi
if [ -z "${API_KEY:-}" ]; then
  echo "API_KEY not set and Infisical fetch (staging /stylobot-gateway gateway-debug-api-key) failed — refusing to soak keyless." >&2
  exit 1
fi

# HARD GUARD: :8190 is staging.stylobot.net's live gateway, not an isolated rig —
# soaking it puts load on real staging traffic. Never target it from this script.
case "$TARGET" in
  *:8190*|*staging.stylobot.net*)
    echo "REFUSING: TARGET=$TARGET is staging (:8190 / staging.stylobot.net), not an isolated soak rig." >&2
    echo "Stand up a dedicated gateway on its own port with CompressionEnabled=true and point TARGET there." >&2
    exit 1
    ;;
esac

if ! command -v k6 >/dev/null; then
  echo "k6 not found — install it or point PATH at it." >&2
  exit 1
fi

log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$OUTDIR/$LABEL.log"; }

# Sampler: prints "<rows> <bytes>" for the rig's dashboard.db. The default
# SSHes to the rig; override DB_STATS_CMD (a command whose stdout is
# "<rows> <bytes>") for your rig's actual DB path.
sample_db() {
  local out
  if [ -n "${DB_STATS_CMD:-}" ]; then
    out="$(eval "$DB_STATS_CMD" 2>/dev/null || true)"
  else
    out="$(SSHPASS="$SSH_PASS" sshpass -e ssh -o StrictHostKeyChecking=accept-new "$SSH_USER@$SOAK_HOST" \
      "sqlite3 ~/stylobot/data/dashboard.db 'SELECT COUNT(*) FROM detections;' 2>/dev/null; stat -c%s ~/stylobot/data/dashboard.db 2>/dev/null" 2>/dev/null || true)"
  fi
  echo "$out" | tr '\n' ' ' | awk '{ if ($1 != "" && $2 != "") print $1, $2; else print "0 0" }'
}

log "compression soak: ${DURATION_HOURS}h @ ${RPS} rps -> $TARGET"
log "load driver: scripts/soak/k6-plateau.js (existing corpus — humans/bots/attacks/honeypots)"

# Baseline before load.
read -r base_rows base_bytes <<< "$(sample_db)"
log "baseline: rows=$base_rows bytes=$base_bytes"

# Drive sustained load in the background (the plateau driver ramps up and
# holds levels through MAX_RPS).
k6 run scripts/soak/k6-plateau.js \
  --env TARGET="$TARGET" --env API_KEY="$API_KEY" --env MAX_RPS="$RPS" \
  > "$OUTDIR/$LABEL-k6.log" 2>&1 &
K6_PID=$!

# Sampling loop: one row-count + size sample per window; the fold must hold
# the count at a plateau (growth collapses once it catches up with the load).
SAMPLES=$((DURATION_HOURS * 60 / SAMPLE_MINUTES))
: > "$OUTDIR/$LABEL-samples.tsv"
echo -e "elapsed_min\trows\tbytes" > "$OUTDIR/$LABEL-samples.tsv"

prev_rows="$base_rows"
for ((s = 1; s <= SAMPLES; s++)); do
  sleep $((SAMPLE_MINUTES * 60))
  read -r rows bytes <<< "$(sample_db)"
  echo -e "$((s * SAMPLE_MINUTES))\t$rows\t$bytes" >> "$OUTDIR/$LABEL-samples.tsv"
  growth=$((rows - prev_rows))
  log "sample $s: rows=$rows (+$growth) bytes=$bytes"
  prev_rows="$rows"
done

wait "$K6_PID" || log "k6 exited nonzero — see $OUTDIR/$LABEL-k6.log"

# Verdict: after the first two warm-up windows the fold must be holding a
# steady state — total growth over the remainder must be a small fraction of
# what the raw pipeline would accumulate at $RPS rps (throttled to ~1
# row/min/signature, that is still thousands of rows per window).
read -r warm_rows _ <<< "$(tail -n +3 "$OUTDIR/$LABEL-samples.tsv" | sed -n '2p')"
read -r final_rows _ <<< "$(tail -n 1 "$OUTDIR/$LABEL-samples.tsv")"
warm_rows="${warm_rows:-$base_rows}"
growth_after_warmup=$((final_rows - warm_rows))
windows_after=$((SAMPLES - 2))
max_plateau_growth=$((RPS * 60 * windows_after * SAMPLE_MINUTES / 3600))

if [ "$growth_after_warmup" -le "$max_plateau_growth" ]; then
  log "PASS: plateau held — +${growth_after_warmup} rows over $windows_after windows (raw pipeline would add ~$max_plateau_growth)"
else
  log "FAIL: no plateau — +${growth_after_warmup} rows over $windows_after windows (raw pipeline ~$max_plateau_growth); fold not absorbing"
fi

log "artifacts: $OUTDIR/$LABEL-samples.tsv, $OUTDIR/$LABEL-k6.log, $OUTDIR/$LABEL.log"
