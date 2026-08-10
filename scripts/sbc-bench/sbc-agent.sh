#!/usr/bin/env bash
# SBC benchmark AGENT — runs ON the benchmark device (Pi / Orange Pi).
#
#   sbc-agent.sh <backend> <sites> <label> [gateway-port]
#     backend  sqlite | postgres
#     sites    1 | 3
#     label    results tag, e.g. pi5-sqlite-1s
#
# What it does:
#   1. compose up the benchmark stack (default config, poison-safe logonly key)
#   2. times cold start: gateway process start -> /admin/alive -> /stylobot/traffic
#   3. samples every 5 s: gateway CPU/RSS (docker stats), host free/loadavg,
#      DB file growth (SQLite files / PG data+WAL)  -> results/<label>/metrics.csv
#   4. waits for the driver's .done flag, then writes summary.txt + cold-start.txt
#
# The DRIVER (sbc-bench.sh on .15) runs the k6 levels against this box while the
# sampler runs. Both are needed — this script alone only brings the rig up.
set -euo pipefail

BACKEND="${1:?backend: sqlite|postgres}"
SITES="${2:?sites: 1|3}"
LABEL="${3:?label}"
PORT="${4:-8080}"
HERE="$(cd "$(dirname "$0")" && pwd)"
RES="$HERE/results/$LABEL"
mkdir -p "$RES"

# ---- cold-start timing ----
T0=$(date +%s.%N)
BOOT=$(uptime -s 2>/dev/null || echo unknown)
echo "=== sbc-agent: backend=$BACKEND sites=$SITES label=$LABEL ==="
echo "device_boot=$BOOT compose_start=$(date -Iseconds)" | tee "$RES/cold-start.txt"

COMPOSE_OPTS=(-f "$HERE/docker-compose.sbc.yml")
[ "$BACKEND" = "postgres" ] && COMPOSE_OPTS+=(-f "$HERE/docker-compose.sbc.postgres.yml")
[ "$SITES" = "3" ] && COMPOSE_OPTS+=("--profile" "three-site")
SITES="$SITES" docker compose "${COMPOSE_OPTS[@]}" up -d --pull always

# Wait for gateway health (liveness only — no DB hit, fast).
HEALTH_T=""
for _ in $(seq 1 60); do
  if curl -sf -m3 "http://localhost:$PORT/admin/alive" >/dev/null 2>&1; then
    HEALTH_T=$(date +%s.%N); break
  fi
  sleep 2
done
if [ -z "$HEALTH_T" ]; then
  echo "gateway not alive after 120s — dumping logs" | tee -a "$RES/cold-start.txt"
  docker compose "${COMPOSE_OPTS[@]}" logs --tail 50 | tee "$RES/gateway-fail.log" || true
  exit 1
fi

# Wait for first dashboard render (the Traffic page).
DASH_T=""
for _ in $(seq 1 60); do
  code=$(curl -s -o /dev/null -w '%{http_code}' -m5 "http://localhost:$PORT/stylobot/traffic" || echo 000)
  if [ "$code" = "200" ]; then DASH_T=$(date +%s.%N); break; fi
  sleep 2
done
T_END=$(date +%s.%N)

{
  echo "health_ready_delta_s=$(awk "BEGIN{printf \"%.1f\", $HEALTH_T-$T0}")"
  echo "dashboard_ready_delta_s=$(if [ -n "$DASH_T" ]; then awk "BEGIN{printf \"%.1f\", $DASH_T-$T0}"; else echo N/A; fi)"
  echo "compose_to_health_s=$(if [ -n "$HEALTH_T" ]; then awk "BEGIN{printf \"%.1f\", $HEALTH_T-$T0}"; else echo N/A; fi)"
  echo "health_to_dashboard_s=$(if [ -n "$HEALTH_T" ] && [ -n "$DASH_T" ]; then awk "BEGIN{printf \"%.1f\", $DASH_T-$HEALTH_T}"; else echo N/A; fi)"
} >> "$RES/cold-start.txt"

# ---- metric sampler (every 5 s until .done) ----
VOL_BASE=$(docker volume inspect sbc_sbc-data -f '{{.Mountpoint}}' 2>/dev/null || docker volume inspect sbc-data -f '{{.Mountpoint}}' 2>/dev/null || echo /nonexistent)
PG_VOL=$(docker volume inspect sbc_sbc-pg-data -f '{{.Mountpoint}}' 2>/dev/null || docker volume inspect sbc-pg-data -f '{{.Mountpoint}}' 2>/dev/null || echo /nonexistent)

sample() {
  local epoch cpu mem host_free load db pg_wal
  epoch=$(date +%s)
  # gateway container stats: "name|CPU%|MEM_USED/MEM_LIMIT|MEM%"
  local stats
  stats=$(docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.MemPerc}}' stylobot-sbc-gw 2>/dev/null || true)
  cpu=$(echo "$stats" | awk -F'|' '{gsub(/%/,"",$2); print $2+0}')
  mem=$(echo "$stats" | awk -F'|' '{split($3,a,"/"); gsub(/[^0-9.]/,"",a[1]); print a[1]+0}')
  host_free=$(awk '/MemFree|MemAvailable/ {print int($2/1024)}' /proc/meminfo | head -1)
  load=$(cut -d' ' -f1 /proc/loadavg 2>/dev/null || echo 0)
  db=$(du -sb "$VOL_BASE" 2>/dev/null | awk '{print $1}' || echo 0)
  pg_wal=$(du -sb "$PG_VOL/pg_wal" 2>/dev/null | awk '{print $1}' || echo 0)
  echo "$epoch,$(date -Iseconds),$cpu,$mem,$host_free,$load,$db,$pg_wal" >> "$RES/metrics.csv"
}

echo "epoch,ts,gateway_cpu_pct,gateway_mem_mb,host_free_mb,host_loadavg,db_bytes,pg_wal_bytes" > "$RES/metrics.csv"
while [ ! -f "$RES/.done" ]; do
  sample
  sleep 5
done
sample  # one final sample

# ---- summary ----
awk -F, 'NR==1{next}
  {if($3!=""&&$3!=""){c+=$3; cn++; if($3>cmax)cmax=$3}
   if($4!=""&&$4!=""){m+=$4; mn++; if($4>mmax)mmax=$4; if(mmin==""||$4<mmin)mmin=$4}}
  END{
    printf "gateway_cpu_pct avg=%.1f max=%.1f (n=%d)\n", c/cn, cmax, cn;
    printf "gateway_mem_mb min=%s avg=%.1f max=%.1f (n=%d)\n", mmin, m/mn, mmax, mn
  }' "$RES/metrics.csv" > "$RES/summary.txt"
{
  echo "samples=$(($(wc -l < "$RES/metrics.csv") - 1))"
  echo "first_db_bytes=$(awk -F, 'NR==2{print $7}' "$RES/metrics.csv") last_db_bytes=$(tail -1 "$RES/metrics.csv" | awk -F, '{print $7}')"
  echo "first_pg_wal=$(awk -F, 'NR==2{print $8}' "$RES/metrics.csv") last_pg_wal=$(tail -1 "$RES/metrics.csv" | awk -F, '{print $8}')"
} >> "$RES/summary.txt"
echo "=== agent done. results in $RES ==="
cat "$RES/summary.txt"
