#!/usr/bin/env bash
# SBC benchmark DRIVER — the one command. Runs a full tier from the driver
# machine (.15/Maxo per operator directive; NEVER the Mac):
#
#   ./sbc-bench.sh <tier> [--sites 1|3] [--no-soak] [--levels "5 10 20 30 50 75 100 150"]
#
# Tiers (lowest first — the ladder overview- set):
#   pi-sqlite      Raspberry Pi + SQLite (the floor — "can I run this on a Pi?")
#   opi-sqlite     Orange Pi + SQLite
#   opi-postgres   Orange Pi + PostgreSQL
#   x86-postgres   x86 (Maxo) + PostgreSQL (reference point)
#
# What it does per tier:
#   1. copies the harness to the device, starts sbc-agent.sh (compose up +
#      cold-start timing + 5 s metric sampler)
#   2. runs the ceiling ladder: one k6 level per RPS, p95 < 500 ms gate
#   3. computes the ceiling = first level where p95 >= 500 ms or errors >= 1%
#   4. soak: 30 min at 50% of the ceiling in 5 min windows (flatness trace)
#   5. stops the agent, pulls results -> results/<label>/, prints the table
#
# Device registry (override with env): pi5 = 192.168.0.39 (claude@), the
# Orange Pi = hostname 'ubuntu'. Credentials via SSH_PASS / OPi_IP env or
# the defaults below (operator-provided device accounts).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
K6="${K6:-k6}"
JQ="${JQ:-jq}"
SSHPASS="${SSHPASS:-sshpass}"
API_KEY="${API_KEY:-SB-BENCH}"
STAMP="$(date +%Y%m%d-%H%M%S)"

# ---- device registry ----
PI5_HOST="${PI5_HOST:-192.168.0.39}"
PI5_USER="${PI5_USER:-claude}"
OPI_HOST="${OPi_IP:-${OPI_HOST:-ubuntu}}"     # Orange Pi hostname 'ubuntu'
OPI_USER="${OPI_USER:-ubuntu}"
X86_HOST=localhost

resolve_tier() {
  case "$1" in
    pi-sqlite)    echo "$PI5_HOST $PI5_USER ${PI5_PASS:-Cl4ude2026!} sqlite" ;;
    opi-sqlite)   echo "$OPI_HOST $OPI_USER ${OPI_PASS:-melkweg26} sqlite" ;;
    opi-postgres) echo "$OPI_HOST $OPI_USER ${OPI_PASS:-melkweg26} postgres" ;;
    x86-postgres) echo "$X86_HOST root x86-local postgres" ;;
    *) echo "unknown tier '$1' — use pi-sqlite|opi-sqlite|opi-postgres|x86-postgres"; exit 2 ;;
  esac
}

TIER="${1:?tier: pi-sqlite|opi-sqlite|opi-postgres|x86-postgres}"
SITES=1; SOAK=1; LEVELS="5 10 20 30 50 75 100 150"; HOLD="${HOLD:-90s}"
while [ $# -gt 0 ]; do
  case "$1" in
    --sites) SITES="$2"; shift 2 ;;
    --no-soak) SOAK=0; shift ;;
    --levels) LEVELS="$2"; shift 2 ;;
    *) shift ;;
  esac
done

read -r DEVICE DEV_USER DEV_PASS BACKEND <<< "$(resolve_tier "$TIER")"
LABEL="${TIER}-${SITES}s-${STAMP}"
OUT="results/$LABEL"
mkdir -p "$OUT"

SSH() { # local target: direct; remote: sshpass
  if [ "$DEVICE" = "localhost" ]; then "$@"; else
    "$SSHPASS" -p "$DEV_PASS" ssh -o StrictHostKeyChecking=no \
      -o PreferredAuthentications=password -o PubkeyAuthentication=no \
      -o ConnectTimeout=8 "$DEV_USER@$DEVICE" "$@"
  fi
}
SCP() { # $1=src $2=dst — remote copy of the harness to the device
  if [ "$DEVICE" != "localhost" ]; then
    "$SSHPASS" -p "$DEV_PASS" scp -o StrictHostKeyChecking=no \
      -o PreferredAuthentications=password -o PubkeyAuthentication=no \
      -r "$1" "$DEV_USER@$DEVICE:$2"
  fi
}

echo "=== sbc-bench tier=$TIER device=$DEVICE backend=$BACKEND sites=$SITES ==="
echo "    k6=$K6  levels=[$LEVELS]  hold=$HOLD  soak=$SOAK"

# ---- 1. ship harness + start agent ----
if [ "$DEVICE" != "localhost" ]; then
  SSH "mkdir -p ~/sbc-bench/results"
  SCP "$HERE/k6-sbc.js" "~/sbc-bench/"
  SCP "$HERE/sbc-agent.sh" "~/sbc-bench/"
  SCP "$HERE/docker-compose.sbc.yml" "~/sbc-bench/"
  SCP "$HERE/docker-compose.sbc.postgres.yml" "~/sbc-bench/"
  SCP "$HERE/config" "~/sbc-bench/"
  SSH "cd ~/sbc-bench && chmod +x sbc-agent.sh && nohup ./sbc-agent.sh '$BACKEND' '$SITES' '$LABEL' > agent.log 2>&1 &"
else
  cp "$HERE/config" /tmp/sbc-bench-config -r 2>/dev/null || true
  nohup "$HERE/sbc-agent.sh" "$BACKEND" "$SITES" "$LABEL" > agent.log 2>&1 &
fi

TARGET="http://$DEVICE:8080"
[ "$DEVICE" = "localhost" ] && TARGET="http://localhost:8080"
TARGET="${K6_TARGET:-$TARGET}"

# ---- 2. wait for the rig to come up ----
echo "[health] waiting for $TARGET/admin/alive ..."
UP=0
for _ in $(seq 1 60); do
  curl -sf -m3 "$TARGET/admin/alive" >/dev/null 2>&1 && { echo "[health] up"; UP=1; break; }
  sleep 2
done
[ "$UP" = "1" ] || { echo "[health] device never came up — check agent.log on the device"; exit 1; }

# ---- 3. ceiling ladder ----
echo "=== ceiling ladder ($LEVELS) ==="
: > "$OUT/ceiling.csv"
for rps in $LEVELS; do
  echo "[level] $rps RPS x $HOLD ..."
  "$K6" run "$HERE/k6-sbc.js" \
    --env TARGET="$TARGET" --env MODE=ceiling --env RPS="$rps" --env DURATION="$HOLD" \
    --env SITES="$SITES" --env API_KEY="$API_KEY" \
    --summary-export "$OUT/level-$rps.json" >/dev/null 2>&1 || true
  jq -r --arg r "$rps" '"\($r),\(.metrics.http_req_duration.values["p(95)"] // 0),\(.metrics.http_req_duration.values.med // 0),\((.metrics.http_req_failed.values.rate // 0)*100),\(.metrics.http_reqs.values.rate // 0),\(.metrics.dropped_iterations.values.count // 0)"' \
    "$OUT/level-$rps.json" >> "$OUT/ceiling.csv" 2>/dev/null \
    || echo "$rps,ERR,ERR,ERR,ERR,ERR" >> "$OUT/ceiling.csv"
done

# ---- 4. ceiling + soak ----
CEILING=$(awk -F, 'NR==1{p95=$2; r=$1; next} ($2>=500 || $3>=1){print r; exit} {p95=$2; r=$1} END{if(p95<500)print r}' \
  "$OUT/ceiling.csv")
SOAK_RPS=$(( ${CEILING:-0} / 2 ))
echo "=== ceiling = ${CEILING:-0} RPS (p95<500ms, <1% errors) -> soak at $SOAK_RPS RPS ==="
echo "$CEILING" > "$OUT/ceiling.txt"

if [ "$SOAK" = "1" ] && [ "$SOAK_RPS" -ge 1 ]; then
  echo "=== soak: 6 x 5 min at $SOAK_RPS RPS ==="
  : > "$OUT/soak-flatness.csv"
  for w in 1 2 3 4 5 6; do
    echo "[soak window $w/6] $SOAK_RPS RPS x 5m ..."
    "$K6" run "$HERE/k6-sbc.js" \
      --env TARGET="$TARGET" --env MODE=soak --env RPS="$SOAK_RPS" --env DURATION=5m \
      --env SITES="$SITES" --env API_KEY="$API_KEY" \
      --summary-export "$OUT/soak-window-$w.json" >/dev/null 2>&1 || true
    jq -r --arg w "$w" '"\($w),\(.metrics.http_req_duration.values["p(95)"] // 0),\((.metrics.http_req_failed.values.rate // 0)*100),\(.metrics.http_reqs.values.rate // 0),\(.metrics.dropped_iterations.values.count // 0)"' \
      "$OUT/soak-window-$w.json" >> "$OUT/soak-flatness.csv" 2>/dev/null \
      || echo "$w,ERR,ERR,ERR,ERR" >> "$OUT/soak-flatness.csv"
  done
fi

# ---- 5. stop agent + pull results ----
if [ "$DEVICE" != "localhost" ]; then
  SSH "touch ~/sbc-bench/results/$LABEL/.done"
  sleep 6
  mkdir -p "$OUT"
  "$SSHPASS" -p "$DEV_PASS" scp -o StrictHostKeyChecking=no \
    -o PreferredAuthentications=password -o PubkeyAuthentication=no \
    -r "$DEV_USER@$DEVICE:~/sbc-bench/results/$LABEL/." "$OUT/" 2>/dev/null || true
else
  touch "$OUT/.done"
  sleep 6
fi

# ---- 6. the table ----
echo ""
echo "══════════════════════ $TIER (sites=$SITES) ══════════════════════"
echo "  RPS   p95(ms)  med(ms)  err%   achieved  dropped"
cat "$OUT/ceiling.csv" | awk -F, 'NR==1{printf "  %-5s %-8s %-7s %-5s %-9s %s\n",$1,$2,$3,$4,$5,$6}'
echo "  Ceiling: ${CEILING:-0} RPS before p95 >= 500 ms"
echo ""
if [ -f "$OUT/summary.txt" ]; then echo "  System (from agent):"; sed 's/^/    /' "$OUT/summary.txt"; fi
if [ -f "$OUT/cold-start.txt" ]; then echo "  Cold start:"; grep -E 'delta|boot' "$OUT/cold-start.txt" | sed 's/^/    /'; fi
if [ -f "$OUT/soak-flatness.csv" ]; then
  echo "  Soak @ ${SOAK_RPS} RPS (per 5-min window):"
  awk -F, 'NR==1{printf "    %-6s %-8s %-6s %-9s %s\n","win","p95","err%","achieved","dropped"; next}{printf "    %-6s %-8s %-6s %-9s %s\n",$1,$2,$3,$4,$5}' "$OUT/soak-flatness.csv"
fi
echo "═══════════════════════════════════════════════════════════════"
echo "artifacts: $OUT/"
