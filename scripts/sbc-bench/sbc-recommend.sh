#!/usr/bin/env bash
# Sizing recommendation program — turns measured benchmark results into
# "what can I run on this setup?" answers.
#
#   ./sbc-recommend.sh <results-dir> [<results-dir> ...]
#
# Each results dir is one tier run (results/<tier>-<sites>s-<timestamp>/ from
# sbc-bench.sh). Reads ceiling.txt/ceiling.csv, summary.txt, metrics.csv,
# soak-flatness.csv and emits a plain-language recommendation block, e.g.:
#
#   Raspberry Pi 5 + SQLite (1 site): ceiling 60 RPS -> ~30 RPS usable
#     -> ~6 typical small sites (5 RPS each) at 1.5x RSS headroom
#
# Model (all multipliers explicit, defaults from the observed x86 sizing
# doc's rules): usable = ceiling x HEADROOM (0.5); sites = usable / SITE_RPS
# (5 RPS = typical small site); RAM = 1.5 x max observed gateway RSS;
# storage = measured growth rate x RETENTION_HOURS.
#
# Env: SITE_RPS (5), HEADROOM (0.5), RAM_HEADROOM (1.5), RETENTION_HOURS (168).
# Output is markdown-ready — paste into performance-benchmarks.md §Recommendations.
set -euo pipefail

SITE_RPS="${SITE_RPS:-5}"
HEADROOM="${HEADROOM:-0.5}"
RAM_HEADROOM="${RAM_HEADROOM:-1.5}"
RETENTION_HOURS="${RETENTION_HOURS:-168}"

recommend() {
  local dir="$1"
  [ -d "$dir" ] || { echo "skip: no such dir $dir"; return; }

  local label ceiling
  label="$(basename "$dir")"
  ceiling="$(cat "$dir/ceiling.txt" 2>/dev/null || echo 0)"

  # usable RPS + sites
  local usable sites
  usable=$(awk "BEGIN{printf \"%.0f\", $ceiling * $HEADROOM}")
  sites=$(awk "BEGIN{printf \"%.0f\", $usable / $SITE_RPS}")

  # memory: max gateway RSS observed (metrics.csv col 4 = mem MB)
  local mem_max mem_rec
  mem_max=$(awk -F, 'NR>1 && $4+0>max {max=$4+0} END{print max+0}' "$dir/metrics.csv" 2>/dev/null || echo 0)
  mem_rec=$(awk "BEGIN{printf \"%.0f\", ($mem_max * $RAM_HEADROOM) / 1024}")

  # storage: growth over the whole run (first vs last db bytes) / run hours.
  # Run duration = sample count x 5s (sampler cadence) — NOT a fixed multiplier.
  local db_first db_last growth_h n
  db_first=$(awk -F, 'NR==2{print $7+0}' "$dir/metrics.csv" 2>/dev/null || echo 0)
  db_last=$(tail -1 "$dir/metrics.csv" 2>/dev/null | awk -F, '{print $7+0}')
  n=$(($(wc -l < "$dir/metrics.csv" 2>/dev/null || echo 0) - 1))
  growth_h=$(awk "BEGIN{ h=($n*5)/3600; if (h>0) printf \"%.1f\", (($db_last-$db_first)/1024/1024)/h; else printf \"0\" }")

  # soak verdict: flat if last window p95 <= 2x first window p95 and errors < 1%
  local soak_verdict="n/a"
  if [ -f "$dir/soak-flatness.csv" ] && [ "$(wc -l < "$dir/soak-flatness.csv")" -ge 3 ]; then
    local w1p95 wlp95 w1err wlerr
    w1p95=$(awk -F, 'NR==2{print $2+0}' "$dir/soak-flatness.csv")
    wlp95=$(tail -1 "$dir/soak-flatness.csv" | awk -F, '{print $2+0}')
    w1err=$(awk -F, 'NR==2{print $3+0}' "$dir/soak-flatness.csv")
    wlerr=$(tail -1 "$dir/soak-flatness.csv" | awk -F, '{print $3+0}')
    if awk "BEGIN{exit !(($wlp95 <= 2*$w1p95) && ($wlerr < 1))}"; then
      soak_verdict="FLAT (start p95 ${w1p95}ms -> end ${wlp95}ms)"
    else
      soak_verdict="DRIFT (start p95 ${w1p95}ms -> end ${wlp95}ms, err ${wlerr}%)"
    fi
  fi

  echo ""
  echo "### $label"
  echo ""
  echo "- **RPS ceiling (measured):** ${ceiling} RPS (p95 < 500 ms) -> **${usable} RPS usable** at ${HEADROOM} headroom"
  echo "- **Sites:** ~**${sites} typical small sites** (${SITE_RPS} RPS each)${mem_rec:+; RAM needed ≈ ${mem_rec} GB} (max observed RSS ${mem_max} MB x ${RAM_HEADROOM})"
  echo "- **Storage:** ~${growth_h} MB/hour (measured growth) -> ${RETENTION_HOURS}h ≈ $(( ${growth_h%.*} * RETENTION_HOURS )) MB"
  echo "- **Soak (30 min @ 50%):** ${soak_verdict}"
}

echo "# SBC sizing recommendations (measured -> plain language)"
echo ""
for d in "$@"; do recommend "$d"; done
echo ""
echo "---"
echo "model: usable = ceiling x ${HEADROOM}; sites = usable / ${SITE_RPS} RPS; RAM = maxRSS x ${RAM_HEADROOM}; storage = measured growth x ${RETENTION_HOURS}h"
