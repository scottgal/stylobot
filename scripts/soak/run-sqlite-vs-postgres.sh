#!/usr/bin/env bash
# Head-to-head: run the plateau soak for Postgres, then SQLite, SEQUENTIALLY
# (never parallel — .15 is CPU-capped; parallel runs contend and confound),
# then print a side-by-side of the key signals.
#
#   scripts/soak/run-sqlite-vs-postgres.sh [MAX_RPS]
#
# Requires deploy-'s .15 backend-swap wired via SWAP_CMD / RESET_CMD (see
# run-backend-soak.sh). Without them, both runs test the currently-deployed
# backend and the comparison is meaningless — the script warns loudly.
# See docs/soak-sqlite-vs-postgres-plan.md for the hypothesis + measurements.
set -euo pipefail

MAX_RPS="${1:-300}"
HERE="$(cd "$(dirname "$0")" && pwd)"

if [ -z "${SWAP_CMD:-}" ]; then
  echo "WARNING: SWAP_CMD unset — both runs will hit the SAME deployed backend."
  echo "         The head-to-head is only meaningful once deploy-'s backend swap is wired."
  echo "         Continuing in 5s (Ctrl-C to abort) ..."
  sleep 5
fi

echo "########## RUN P: Postgres ##########"
"$HERE/run-backend-soak.sh" postgres "$MAX_RPS"

echo "########## cooldown 60s between runs ##########"
sleep 60

echo "########## RUN S: SQLite ##########"
"$HERE/run-backend-soak.sh" sqlite "$MAX_RPS"

echo ""
echo "########## HEAD-TO-HEAD ##########"
for b in postgres sqlite; do
  f="$(ls -t soak-results/soak-"$b"-*-summary.json 2>/dev/null | head -1)"
  if [ -n "$f" ]; then
    jq -r --arg b "$b" '.metrics.http_req_waiting as $w
      | "\($b):  waiting p95=\($w["p(95)"]|floor)ms  max=\($w.max|floor)ms  failed=\(((.metrics.http_req_failed.rate // 0)*100)|floor)%  it/s=\((.metrics.iterations.rate // 0)|floor)"' "$f"
  else
    echo "$b: no summary found"
  fi
done
echo "(full artifacts under soak-results/)"
