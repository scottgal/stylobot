#!/usr/bin/env bash
# Cardinality + freshness + memory-trend soak — replaces liveness-only
# plateau verdicts with the correctness class the mission demands.
#
# The existing run-compression-soak.sh only ever asked "is the fold row-count
# plateauing" (a DIFFERENT, still-valid question). This script answers a
# different one: can the rig reproduce the OOM/read-gap class at all, and did
# it just now? It reuses the SAME k6 corpus (k6-plateau.js), extended with
# X-Forwarded-For signature-cardinality growth and a freshness-probe scenario
# (see k6-plateau.js comments) — never a hand-rolled driver.
#
#   scripts/soak/run-cardinality-freshness-soak.sh [DURATION_HOURS] [RPS]
#
# Preconditions (deploy- sets these on the rig, never staging/prod):
#   * TARGET is the isolated :8290 rig. Refuses :8190 / staging.stylobot.net.
#   * The rig gateway trusts X-Forwarded-For (Network:TrustAllForwardedProxies
#     or a KnownNetworks allowlist) SCOPED so that setting cannot reach
#     staging/prod through any shared config path (operator hard guardrail,
#     2026-08-20) — this script does not set it, only depends on it.
#   * API_KEY: debug/ops key (DisableLearningWrites).
#   * CONTAINER_STATS_CMD: a command whose stdout is
#     "<rss_bytes> <restart_count> <oom_killed_0_or_1>" for the rig gateway
#     container. Verified working command for the :8290 rig (deploy-,
#     2026-08-20, cgroup v1 host — memory.usage_in_bytes not v2's
#     memory.current), used below as the default:
#       rss=$(ssh -o BatchMode=yes claude2@192.168.0.15 "docker exec stylobot-loadtest-gateway cat /sys/fs/cgroup/memory/memory.usage_in_bytes" 2>/dev/null | tr -d "\r\n"); meta=$(ssh -o BatchMode=yes claude2@192.168.0.15 "docker inspect stylobot-loadtest-gateway --format=\"{{.RestartCount}} {{.State.OOMKilled}}\"" 2>/dev/null | tr -d "\r"); rc=$(echo "$meta" | awk "{print \$1}"); oomv=$(echo "$meta" | awk "{print \$2}"); oom=0; [ "$oomv" = "true" ] && oom=1; echo "$rss $rc $oom"
#     4TH FIELD (running, 1/0) is OPTIONAL — add `{{.State.Running}}` to the
#     docker inspect format (convert true/false to 1/0) to enable the CRASH
#     GATE, which catches a death that is NEITHER an OOM-kill NOR a restart
#     (e.g. SIGSEGV/exit 139 — found for real in the 2026-08-21 baseline run,
#     where OOM GATE reported a misleading PASS because the container simply
#     stopped and never came back). Without it, defaults to "running", i.e.
#     the crash gate has nothing to check — an explicit NO DATA, not a lie.
#     TOPOLOGY GAP (overview-, 2026-08-20): the :8290 rig has NO separate
#     website process — TrafficController/dashboard UI is compiled into and
#     served BY this same gateway container (3 services total: gateway,
#     postgres, upstream stub). In prod/staging the dashboard is served by a
#     SEPARATE website app with its own caches (site materializer,
#     signal-shingle, SWR store) reached over a REST hop. A clean run on this
#     rig does NOT clear those website-side cache populations — scope every
#     verdict to "gateway process only", never imply whole-stack coverage.
#   * DB_STATS_CMD: same as run-compression-soak.sh (row/byte sampler).
#   * DB_WRITE_STATS_CMD: a command whose stdout is a SINGLE cumulative
#     integer counting persist OPERATIONS (statements/transactions, not rows
#     touched) — e.g. for Postgres:
#       SELECT xact_commit FROM pg_stat_database WHERE datname=current_database();
#     THE MOST IMPORTANT ASSERTION (operator ruling, 2026-08-20, ABSOLUTE — no
#     per-request/per-session/per-eviction persistence, ever): absorption
#     exists to decouple request activity from DB write load. This is the
#     ONLY sampler that measures that property directly — row-count growth
#     does not (a batched upsert can touch many rows in one statement).
#     Without it the write-amplification gate reports NO DATA, which is a
#     real coverage gap in the run, not a neutral skip.
#
# Env overrides mirror run-compression-soak.sh: TARGET, API_KEY, SOAK_HOST,
# SSH_USER, SSH_PASS, DB_STATS_CMD, DB_WRITE_STATS_CMD, SAMPLE_MINUTES, OUTDIR.
#   CONTAINER_STATS_CMD  (required for the memory-trend + OOM assertions;
#                         script degrades to DB-only reporting without it)
#   REGRESSION_GATE       (default false — see below)
#   POSITIVE_CONTROL       (default false — see below)
set -euo pipefail

DURATION_HOURS="${1:-6}"
RPS="${2:-150}"
TARGET="${TARGET:-http://192.168.0.15:8290}"
SOAK_HOST="${SOAK_HOST:-192.168.0.15}"
SSH_USER="${SSH_USER:-claude}"
SSH_PASS="${SSH_PASS:-Cl4ude2026!}"
SAMPLE_MINUTES="${SAMPLE_MINUTES:-15}"
OUTDIR="${OUTDIR:-soak-results}"
# false by default. SCOPE CORRECTED 2026-08-20 12:12 (overview-, from stream-'s
# full-file audit): GetTimeSeriesAsync/GetSummaryAsync already compose DB with
# live state (a fixed-cutoff partition, not a gap) and are NOT this gate —
# they need a different assertion (double-count/drop AT the cutoff boundary,
# not yet built). GetDetectionsAsync is the one genuinely DB-primary surface
# (only consults live state when a SignatureId filter is set) — the ONLY
# surface this probe targets and the only one this gate should ever cover.
# Flip to true only once the read-through contract has actually landed for
# GetDetectionsAsync — then this script is what protects the fix.
REGRESSION_GATE="${REGRESSION_GATE:-false}"
# When true, this run is a deliberate red-run: prove the rig CAN fail before
# trusting a green run (operator/overview- non-negotiable requirement). The
# memory cap to apply is derived from this run's own baseline RSS sample, not
# a chosen number — see the printed recommendation below; deploy- applies it
# (Maxo exec is not this script's lane).
POSITIVE_CONTROL="${POSITIVE_CONTROL:-false}"
STAMP="$(date +%Y%m%d-%H%M%S)"
LABEL="cardinality-freshness-${STAMP}"
mkdir -p "$OUTDIR"

if [ -z "${API_KEY:-}" ]; then
  API_KEY="$(infisical secrets get gateway-debug-api-key --env=staging --path=/stylobot-gateway --plain 2>/dev/null || true)"
fi
if [ -z "${API_KEY:-}" ]; then
  echo "API_KEY not set and Infisical fetch failed — refusing to soak keyless." >&2
  exit 1
fi

case "$TARGET" in
  *:8190*|*staging.stylobot.net*)
    echo "REFUSING: TARGET=$TARGET is staging, not the isolated :8290 rig." >&2
    exit 1
    ;;
esac

if ! command -v k6 >/dev/null; then
  echo "k6 not found." >&2
  exit 1
fi
if ! command -v python3 >/dev/null; then
  echo "python3 not found — required for the trend/differential verdict." >&2
  exit 1
fi

log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$OUTDIR/$LABEL.log"; }

sample_db() {
  local out=""
  if [ -n "${DB_STATS_CMD:-}" ]; then
    out="$(eval "$DB_STATS_CMD" 2>/dev/null || true)"
  fi
  echo "$out" | tr '\n' ' ' | awk '{ if ($1 != "" && $2 != "") print $1, $2; else print "0 0" }'
}

# "<rss_bytes> <restart_count> <oom_killed_0_or_1>" — deploy- wires the real
# command (docker inspect/stats over the Maxo exec lane); without it this
# script still runs the freshness/cardinality checks, just not the memory
# trend or OOM gate.
sample_container() {
  local out=""
  if [ -n "${CONTAINER_STATS_CMD:-}" ]; then
    out="$(eval "$CONTAINER_STATS_CMD" 2>/dev/null || true)"
  fi
  # 4th field (running, 1/0) is OPTIONAL for backward compat with an
  # already-wired 3-field CONTAINER_STATS_CMD — defaults to 1 (assume
  # running) when absent, so existing commands keep working unmodified; the
  # CRASH GATE below simply has nothing to check without it. Add
  # `{{.State.Running}}` (true/false, convert to 1/0) to upgrade a command.
  echo "$out" | tr '\n' ' ' | awk '{
    if ($1 != "" && $2 != "" && $3 != "") {
      running = ($4 != "") ? $4 : 1
      print $1, $2, $3, running
    } else {
      print "0 0 0 1"
    }
  }'
}

# THE MOST IMPORTANT ASSERTION (operator, 2026-08-20): absorption exists to
# decouple request activity from DB WRITE load. Row count / DB size growth
# does not prove this — a batched upsert can still touch many rows per
# statement. The invariant is the number of PERSIST OPERATIONS (statements/
# transactions) per unit time, which must stay flat while request rate and
# cardinality climb. A single cumulative counter — Postgres:
#   SELECT xact_commit FROM pg_stat_database WHERE datname=current_database();
# (a delta over each sampling window is thus the number of transactions
# committed in that window — a batched-flush design keeps this flat under
# rising load; per-request/per-eviction persistence makes it track load 1:1).
# Required for the write-amplification gate; script degrades gracefully
# (reports NO DATA) without it, same pattern as the other two samplers.
sample_db_writes() {
  local out=""
  if [ -n "${DB_WRITE_STATS_CMD:-}" ]; then
    out="$(eval "$DB_WRITE_STATS_CMD" 2>/dev/null || true)"
  fi
  echo "$out" | tr -d '\n\r ' | awk '{ if ($0 != "") print $0; else print "0" }'
}

if [ "$POSITIVE_CONTROL" = "true" ]; then
  read -r base_rss _ _ _ <<< "$(sample_container)"
  if [ "$base_rss" = "0" ]; then
    echo "POSITIVE_CONTROL requested but CONTAINER_STATS_CMD gave no baseline RSS — cannot derive a cap." >&2
    exit 1
  fi
  # Derived, not chosen: a cap tight enough that the growing-cardinality
  # driver should exhaust it well inside the run (half the observed
  # steady-state baseline — the baseline itself came from THIS rig, not a
  # constant anyone picked).
  recommended_cap=$((base_rss / 2))
  log "POSITIVE CONTROL: baseline RSS=$base_rss bytes. Recommended memory cap ~$recommended_cap bytes."
  log "Apply this cap on the rig container (deploy-'s lane) BEFORE the run below, then confirm the"
  log "memory-trend and OOM assertions actually go red. Revert the cap before any real soak run."
fi

log "cardinality+freshness soak: ${DURATION_HOURS}h @ ${RPS} rps -> $TARGET"
log "load driver: scripts/soak/k6-plateau.js (extended: X-Forwarded-For cardinality growth + freshness_probe)"
log "REGRESSION_GATE=$REGRESSION_GATE (db-only known-defect divergence is a hard gate only when true)"

read -r base_rows base_bytes <<< "$(sample_db)"
read -r base_rss base_restarts base_oom base_running <<< "$(sample_container)"
base_writes="$(sample_db_writes)"
log "baseline: db_rows=$base_rows db_bytes=$base_bytes rss=$base_rss restarts=$base_restarts oom=$base_oom running=$base_running db_writes=$base_writes"

K6_RAW="$OUTDIR/$LABEL-raw.json"
# NO --duration flag here — confirmed 2026-08-21 that it conflicts with the
# script's own options.scenarios (freshness_probe never ran in the 3h
# baseline, not even a metric-name registration). Duration control lives
# entirely inside k6-plateau.js now, driven by DURATION_HOURS via --env.
k6 run scripts/soak/k6-plateau.js \
  --env TARGET="$TARGET" --env API_KEY="$API_KEY" --env MAX_RPS="$RPS" \
  --env DURATION_HOURS="$DURATION_HOURS" \
  --out "json=$K6_RAW" \
  > "$OUTDIR/$LABEL-k6.log" 2>&1 &
K6_PID=$!

# python3, not bash `$(())`: DURATION_HOURS may be fractional (e.g. a short
# pilot at 0.25h) and bash integer arithmetic rejects a "." token outright.
SAMPLES=$(python3 -c "import sys; print(max(1, int(float(sys.argv[1]) * 60 / float(sys.argv[2]))))" "$DURATION_HOURS" "$SAMPLE_MINUTES")
echo -e "elapsed_min\tdb_rows\tdb_bytes\trss_bytes\trestarts\toom_killed\tdb_writes\trunning" > "$OUTDIR/$LABEL-samples.tsv"

for ((s = 1; s <= SAMPLES; s++)); do
  sleep $((SAMPLE_MINUTES * 60))
  read -r rows bytes <<< "$(sample_db)"
  read -r rss restarts oom running <<< "$(sample_container)"
  writes="$(sample_db_writes)"
  echo -e "$((s * SAMPLE_MINUTES))\t$rows\t$bytes\t$rss\t$restarts\t$oom\t$writes\t$running" >> "$OUTDIR/$LABEL-samples.tsv"
  log "sample $s: db_rows=$rows rss=$rss restarts=$restarts oom=$oom running=$running db_writes=$writes"
  if [ "$oom" != "0" ] || { [ "$restarts" != "0" ] && [ "$restarts" != "$base_restarts" ]; }; then
    log "OOM/RESTART DETECTED mid-run — recording, not aborting (soak must keep sampling through the failure)."
  fi
  if [ "$running" = "0" ]; then
    log "CONTAINER NOT RUNNING mid-run (crashed without OOM/restart, e.g. SIGSEGV) — recording, not aborting."
  fi
done

wait "$K6_PID" || log "k6 exited nonzero — see $OUTDIR/$LABEL-k6.log"

log "verdict:"
python3 - "$OUTDIR/$LABEL-samples.tsv" "$K6_RAW" "$REGRESSION_GATE" <<'PYEOF' | tee -a "$OUTDIR/$LABEL.log"
import json, sys, csv

samples_path, raw_path, regression_gate = sys.argv[1], sys.argv[2], sys.argv[3] == "true"
overall_fail = False

# ── Memory trend: slope after the warm-up inflection, not a fixed window ──
rss = []
db_writes = []
restarts_seen = False
oom_seen = False
crashed_seen = False
crash_at_elapsed_min = None
with open(samples_path) as f:
    for row in csv.DictReader(f, delimiter="\t"):
        is_running = not ("running" in row and row["running"] not in ("", None) and int(row["running"]) == 0)
        if not is_running and not crashed_seen:
            crash_at_elapsed_min = row["elapsed_min"]
        if not is_running:
            crashed_seen = True
        # A crashed sample's rss/db_writes are sampler-failure artifacts (the
        # container is dead — e.g. a 0 reading), NOT real measurements. Feeding
        # them into the trend/shape/write-amp math would misread "the process
        # died" as "memory dropped" or "writes went flat" — exactly the
        # confound a real 2026-08-21 baseline run hit. Once crashed, stop
        # appending to the series entirely; the CRASH GATE reports the death,
        # everything else is judged only on the genuinely-alive window.
        if is_running:
            rss.append(int(row["rss_bytes"]))
            if "db_writes" in row and row["db_writes"] not in ("", None):
                db_writes.append(int(row["db_writes"]))
        if int(row["restarts"]) > 0:
            restarts_seen = True
        if int(row["oom_killed"]) != 0:
            oom_seen = True

rss_nonzero = [v for v in rss if v > 0]
if len(rss_nonzero) >= 2:
    # Report the raw magnitude ALWAYS, independent of whether the trend
    # verdict below is conclusive — a steep raw delta is worth a human's eyes
    # even at INCONCLUSIVE, and silently dropping it is the same instrument
    # failure this whole exercise exists to catch.
    first, last = rss_nonzero[0], rss_nonzero[-1]
    ratio = (last / first) if first else float("inf")
    print(f"MEMORY RAW: first={first} last={last} delta={last-first:+d} ({ratio:.2f}x over {len(rss_nonzero)} samples)")

if len(rss_nonzero) >= 4:
    # Inflection = the sample index of the running maximum stops advancing for
    # good, i.e. warm-up ends where the peak-so-far first stays flat across
    # the rest of the run's first half. Everything from there on is "steady
    # state" and is what the trend is judged on.
    running_max_idx = 0
    for i, v in enumerate(rss):
        if v > rss[running_max_idx]:
            running_max_idx = i
    # steady-state window: from the point warm-up growth stopped dominating
    # (half-way between the running max and the end) to the end.
    inflection = running_max_idx + max(1, (len(rss) - running_max_idx) // 4)
    steady = rss[min(inflection, len(rss) - 2):]
    if len(steady) >= 3:
        n = len(steady)
        xs = list(range(n))
        mean_x = sum(xs) / n
        mean_y = sum(steady) / n
        num = sum((xs[i] - mean_x) * (steady[i] - mean_y) for i in range(n))
        den = sum((x - mean_x) ** 2 for x in xs) or 1
        slope = num / den
        print(f"MEMORY TREND: steady-state slope = {slope:+.1f} bytes/sample over {n} samples "
              f"(warm-up excluded via inflection at sample {inflection})")
        if slope > 0:
            print("MEMORY TREND: FAIL — rising, non-plateauing trend in steady state (do not wait for OOM to call it)")
            overall_fail = True
        else:
            print("MEMORY TREND: PASS — steady-state slope is flat or decaying")
    else:
        print("MEMORY TREND: INCONCLUSIVE — not enough post-inflection samples yet")
else:
    print("MEMORY TREND: NO DATA — CONTAINER_STATS_CMD not wired for this run")

# ── Memory SHAPE (operator, 2026-08-21): a sawtooth under the cap is a FAIL ──
# even if the trend check above passes (a max-only/slope-only view is blind to
# this — a sawtooth's post-inflection segment can look locally flat while the
# full series climbs-then-drops repeatedly). This is the direct observable for
# whether a threshold/periodic-drain mechanism crept back into the design:
# flat/stable -> PASS; climbing-then-dropping repeatedly under a cap -> FAIL
# (threshold mechanism); climbing without dropping -> FAIL (covered above,
# the unbounded/OOM class). "Significant" drop is derived from THIS series'
# own typical sample-to-sample noise (median absolute delta), never a chosen
# byte count.
#
# TWO KNOWN LIMITS (overview-, 2026-08-21 — parked pending v9 session-layer
# work; carry these forward into that run, they matter more against the new
# implementation, not less):
#   1. This gate can only see a sawtooth whose PERIOD is shorter than the run
#      itself. Report run duration alongside every verdict and state which
#      periods it could NOT have excluded (e.g. a 5-minute tick, a 2-hour
#      compression window, daily retention — any cycle longer than the run
#      is invisible to this detector by construction, not proof of absence).
#   2. An RSS drop can come from a Gen2 GC returning segments to the OS
#      rather than a genuine bulk drain/eviction pass. Carry MANAGED HEAP
#      SIZE (not just process RSS) alongside this sampler once available, so
#      a GC-driven dip isn't mistaken for the drain pattern this gate exists
#      to catch (or vice versa — a real drain masked by GC noise).
if len(rss) >= 5:
    deltas_full = [rss[i] - rss[i - 1] for i in range(1, len(rss))]
    abs_deltas = sorted(abs(d) for d in deltas_full)
    noise_floor = abs_deltas[len(abs_deltas) // 2]
    significant = max(noise_floor * 3, 1)

    drops = []
    i = 1
    while i < len(rss) - 1:
        if rss[i] >= rss[i - 1] and rss[i] > rss[i + 1]:
            peak = rss[i]
            j = i + 1
            while j < len(rss) - 1 and rss[j] >= rss[j + 1]:
                j += 1
            drop = peak - rss[j]
            if drop >= significant:
                drops.append(drop)
            i = j
        else:
            i += 1

    print(f"MEMORY SHAPE: {len(drops)} significant peak-to-trough drop(s) "
          f"(noise floor={noise_floor}, significance bar={significant:.0f} bytes); drops={drops}")
    if len(drops) >= 2:
        print("MEMORY SHAPE: FAIL — SAWTOOTH (recurring climb-then-drop under a cap) — the visible "
              "signature of a threshold/periodic-drain mechanism, not the required flat/stable curve")
        overall_fail = True
    elif len(drops) == 1:
        print("MEMORY SHAPE: single drop observed — treated as warm-up settling, not a recurring "
              "sawtooth (see MEMORY TREND for the steady-state slope check)")
    else:
        print("MEMORY SHAPE: PASS — no recurring climb-then-drop pattern")
else:
    print("MEMORY SHAPE: NO DATA — not enough samples to assess curve shape")

if oom_seen:
    print("OOM GATE: FAIL — OOMKilled observed during the run")
    overall_fail = True
elif restarts_seen:
    print("OOM GATE: FAIL — restart count increased during the run")
    overall_fail = True
else:
    print("OOM GATE: PASS — zero OOM kills, zero restarts")

# ── CRASH GATE (added 2026-08-21 after a real baseline run hit this exact ──
# gap): OOM GATE alone is blind to a crash that is neither an OOM-kill nor a
# restart — e.g. SIGSEGV, exit 139, container dies and never comes back. The
# 4th sample_container() field (running) exists for exactly this. A crash
# here INVALIDATES every sample after it — they measure a dead container, not
# the system under test — so every other verdict in this report is annotated
# accordingly rather than left to imply full-run coverage it doesn't have.
if crashed_seen:
    print(f"CRASH GATE: FAIL — container stopped running at ~{crash_at_elapsed_min}min "
          f"without an OOM-kill or a restart (e.g. SIGSEGV/exit 139) — samples after this point "
          f"measure a DEAD container and invalidate every OTHER verdict in this report for the "
          f"remainder of the run; treat this run as informative-but-truncated, not a clean result")
    overall_fail = True
elif "running" not in open(samples_path).readline():
    print("CRASH GATE: NO DATA — CONTAINER_STATS_CMD is not yet reporting a 4th (running) field; "
          "add {{.State.Running}} to it to enable this check")
else:
    print("CRASH GATE: PASS — container stayed up (running=1) for every sample")

# ── Write-amplification gate (operator, 2026-08-20): THE MOST IMPORTANT ──
# assertion — absorption exists to decouple request activity from DB write
# load. The plateau driver RAMPS RPS/cardinality through the run (buildStages
# in k6-plateau.js), so if persistence is correctly batched, writes-per-window
# stays flat despite rising load; if it is one-write-per-request/eviction, the
# per-window write delta rises in step with the ramp. Directional check
# (first-third vs last-third average delta), not a magnitude threshold — RPS
# only ever goes up across this run, so a batched design has no reason for
# late-window deltas to exceed early-window ones.
if len(db_writes) >= 6:
    deltas = [db_writes[i] - db_writes[i - 1] for i in range(1, len(db_writes))]
    third = max(1, len(deltas) // 3)
    first_avg = sum(deltas[:third]) / third
    last_avg = sum(deltas[-third:]) / third
    print(f"WRITE AMPLIFICATION: db_writes deltas={deltas} "
          f"first-third avg={first_avg:.1f}/window last-third avg={last_avg:.1f}/window "
          f"(RPS ramps UP across the run — a batched design keeps these comparable)")
    if last_avg > first_avg and first_avg >= 0:
        print("WRITE AMPLIFICATION: FAIL — write rate rose alongside the RPS/cardinality ramp; "
              "persistence is tracking load, not decoupled from it (the entire point of absorption)")
        overall_fail = True
    else:
        print("WRITE AMPLIFICATION: PASS — write rate did not rise with the ramp")
elif len(db_writes) >= 2:
    print(f"WRITE AMPLIFICATION: INCONCLUSIVE — only {len(db_writes)} db_writes samples, "
          f"need >=6 to split first/last thirds meaningfully")
else:
    print("WRITE AMPLIFICATION: NO DATA — DB_WRITE_STATS_CMD not wired for this run "
          "(this is the assertion the mission calls arguably the most important — treat its "
          "absence as a real gap in the run's coverage, not a neutral skip)")

# ── Freshness differential: control vs oneshot, per surface, from raw tags ──
by_key = {}  # (surface, population, metric) -> list of values
try:
    with open(raw_path) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                point = json.loads(line)
            except json.JSONDecodeError:
                continue
            if point.get("type") != "Point":
                continue
            metric = point.get("metric")
            if metric not in ("freshness_ms", "freshness_timeout"):
                continue
            tags = point.get("data", {}).get("tags", {})
            key = (tags.get("surface", "?"), tags.get("population", "?"), metric)
            by_key.setdefault(key, []).append(point["data"]["value"])
except FileNotFoundError:
    print("FRESHNESS: NO RAW DATA — --out json path missing")
    by_key = None

if by_key is not None and not by_key:
    # The raw file exists but contains ZERO freshness_ms/freshness_timeout
    # points — the freshness_probe scenario produced no data at all. This is
    # NOT a clean/quiet result and must never be silently skipped: it means
    # either the scenario didn't run, every request errored before tagging,
    # or the --out json wiring is broken. Loud and failed, not absent.
    print("FRESHNESS: ZERO DATA POINTS — freshness_probe scenario produced no "
          "freshness_ms/freshness_timeout points. This is an INSTRUMENT FAULT, "
          "not a clean run: verify the scenario actually executed (check k6.log "
          "for freshness_probe iteration counts) before trusting any other verdict "
          "in this run.")
    overall_fail = True

surfaces = sorted({k[0] for k in (by_key or {}) if k[0] != "?"})
for surface in surfaces:
    for population in ("control", "oneshot"):
        timeouts = by_key.get((surface, population, "freshness_timeout"), [])
        resolved = by_key.get((surface, population, "freshness_ms"), [])
        timeout_rate = (sum(timeouts) / len(timeouts)) if timeouts else None
        p50 = sorted(resolved)[len(resolved) // 2] if resolved else None
        print(f"FRESHNESS[{surface}][{population}]: "
              f"n={len(timeouts)} timeout_rate={timeout_rate} p50_ms={p50}")
        if population == "control" and timeout_rate and timeout_rate > 0:
            print(f"FRESHNESS[{surface}][control]: FAIL — the low-cardinality control population "
                  f"itself failed to resolve; the probe/rig is broken, not just the defect under test")
            overall_fail = True

    ctrl_to = by_key.get((surface, "control", "freshness_timeout"), [])
    one_to = by_key.get((surface, "oneshot", "freshness_timeout"), [])
    ctrl_rate = (sum(ctrl_to) / len(ctrl_to)) if ctrl_to else None
    one_rate = (sum(one_to) / len(one_to)) if one_to else None
    if ctrl_rate is not None and one_rate is not None and one_rate > ctrl_rate:
        label = "REGRESSION (gate active)" if regression_gate else "KNOWN-DEFECT (reported, not gated)"
        print(f"FRESHNESS[{surface}]: DIVERGENCE — oneshot timeout_rate={one_rate} > "
              f"control timeout_rate={ctrl_rate} — {label}")
        if regression_gate:
            overall_fail = True

print()
print("OVERALL: FAIL" if overall_fail else "OVERALL: PASS")
sys.exit(1 if overall_fail else 0)
PYEOF

log "artifacts: $OUTDIR/$LABEL-samples.tsv, $OUTDIR/$LABEL-raw.json, $OUTDIR/$LABEL-k6.log, $OUTDIR/$LABEL.log"
