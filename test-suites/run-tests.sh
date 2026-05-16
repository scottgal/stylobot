#!/usr/bin/env bash
# StyloBot BDF Test Suite Runner
# Goal: maximize sensitivity (bot catch rate) while minimizing false positives (human block rate).
#
# Usage:
#   ./run-tests.sh [--url URL] [--key API_KEY] [--verbose]
#   ./run-tests.sh --url http://localhost:5080 --key SB-BDF-TEST
#
# Exit codes:
#   0 = all tests passed
#   1 = test failures (FP or FN)
#   2 = server unreachable

set -euo pipefail

BASE_URL="${STYLOBOT_URL:-http://localhost:5080}"
API_KEY="${STYLOBOT_KEY:-SB-BDF-TEST}"
VERBOSE="${VERBOSE:-false}"
REPLAY_ENDPOINT="$BASE_URL/bot-detection/bdf-replay/replay"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Parse args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --url) BASE_URL="$2"; REPLAY_ENDPOINT="$BASE_URL/bot-detection/bdf-replay/replay"; shift 2 ;;
    --key) API_KEY="$2"; shift 2 ;;
    --verbose) VERBOSE=true; shift ;;
    *) echo "Unknown arg: $1"; exit 1 ;;
  esac
done

# Check server is up
if ! curl -sf "$BASE_URL/health" -o /dev/null 2>/dev/null && \
   ! curl -sf "$BASE_URL/" -o /dev/null 2>/dev/null; then
  echo "ERROR: Server not reachable at $BASE_URL"
  exit 2
fi

# Counters
TOTAL_BOTS=0; BOT_DETECTED=0
TOTAL_HUMANS=0; HUMAN_BLOCKED=0
SUITE_PASS=0; SUITE_FAIL=0
declare -a FAILURES

replay_suite() {
  local file="$1"
  local expect_bot="$2"  # "true" or "false"
  local suite_name
  suite_name=$(basename "$file" .bdf.json)

  local result
  result=$(curl -sf -X POST "$REPLAY_ENDPOINT" \
    -H "Content-Type: application/json" \
    -H "X-SB-Api-Key: $API_KEY" \
    -d @"$file" 2>&1)

  if [[ $? -ne 0 ]]; then
    echo "  [ERROR] $suite_name: replay request failed"
    FAILURES+=("$suite_name (request failed)")
    ((SUITE_FAIL++)) || true
    return
  fi

  local match_rate false_pos false_neg total truncated
  match_rate=$(echo "$result" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['summary']['matchRate'])" 2>/dev/null || echo "0")
  false_pos=$(echo "$result"  | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['summary']['falsePositives'])" 2>/dev/null || echo "0")
  false_neg=$(echo "$result"  | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['summary']['falseNegatives'])" 2>/dev/null || echo "0")
  total=$(echo "$result"      | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['summary']['totalRequests'])" 2>/dev/null || echo "0")
  truncated=$(echo "$result"  | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['summary'].get('truncated', False))" 2>/dev/null || echo "False")

  # Count per-detector category stats
  if [[ "$expect_bot" == "true" ]]; then
    TOTAL_BOTS=$((TOTAL_BOTS + total))
    BOT_DETECTED=$((BOT_DETECTED + total - false_neg))
    local detected_count=$((total - false_neg))
    if [[ "$false_neg" -gt 0 ]]; then
      local status="FAIL"
      FAILURES+=("$suite_name: $false_neg/$total requests not detected as bot (false negatives)")
      ((SUITE_FAIL++)) || true
    else
      local status="PASS"
      ((SUITE_PASS++)) || true
    fi
    printf "  [%s] %-40s bot=%d/%d  match=%.0f%%\n" \
      "$status" "$suite_name" "$detected_count" "$total" "$(echo "$match_rate * 100" | bc -l | xargs printf "%.0f")"
  else
    TOTAL_HUMANS=$((TOTAL_HUMANS + total))
    HUMAN_BLOCKED=$((HUMAN_BLOCKED + false_pos))
    if [[ "$false_pos" -gt 0 ]]; then
      local status="FAIL"
      FAILURES+=("$suite_name: $false_pos/$total legitimate requests BLOCKED (false positives)")
      ((SUITE_FAIL++)) || true
    else
      local status="PASS"
      ((SUITE_PASS++)) || true
    fi
    printf "  [%s] %-40s blocked=%d/%d  match=%.0f%%\n" \
      "$status" "$suite_name" "$false_pos" "$total" "$(echo "$match_rate * 100" | bc -l | xargs printf "%.0f")"
  fi

  if [[ "$VERBOSE" == "true" ]]; then
    echo "$result" | python3 -c "
import sys, json
d = json.load(sys.stdin)
for r in d['results']:
    actual = r.get('actual', {})
    prob = actual.get('botProbability', 0)
    is_bot = actual.get('isBot', False)
    match = r.get('match', False)
    reasons = actual.get('topReasons', [])[:2]
    print(f'    {\"ok\" if match else \"MISMATCH\"} path={r[\"path\"]} prob={prob:.2f} isBot={is_bot}')
    for reason in reasons:
        print(f'      - {reason}')
"
  fi

  if [[ "$truncated" == "True" ]]; then
    echo "    WARNING: results truncated (increase MaxRequestsPerReplay)"
  fi
}

echo ""
echo "StyloBot Detection Test Suite"
echo "=============================="
echo "Server: $BASE_URL"
echo "Key:    $API_KEY"
echo ""

# === BOT SCENARIOS (should be detected) ===
echo "BOT DETECTION (sensitivity - should catch all):"
echo "------------------------------------------------"
for f in "$SCRIPT_DIR"/bots/*.bdf.json; do
  [[ -f "$f" ]] && replay_suite "$f" "true"
done

echo ""

# === FALSE POSITIVE PREVENTION (humans - must not be blocked) ===
echo "FALSE POSITIVE PREVENTION (humans - must not block):"
echo "-----------------------------------------------------"
for f in "$SCRIPT_DIR"/humans/*.bdf.json; do
  [[ -f "$f" ]] && replay_suite "$f" "false"
done

echo ""

# === ADVERSARIAL - bots trying to evade (adv-* = bot, adv-fp-* = human) ===
echo "ADVERSARIAL - bots evading (adv-*) and legit-but-suspicious (adv-fp-*):"
echo "--------------------------------------------------------------------------"
for f in "$SCRIPT_DIR"/adversarial/*.bdf.json; do
  [[ -f "$f" ]] || continue
  name=$(basename "$f")
  if [[ "$name" == adv-fp-* ]]; then
    replay_suite "$f" "false"
  else
    replay_suite "$f" "true"
  fi
done

echo ""
echo "=============================="
echo "RESULTS SUMMARY"
echo "=============================="

# Sensitivity (true positive rate)
if [[ "$TOTAL_BOTS" -gt 0 ]]; then
  SENSITIVITY=$(echo "scale=1; $BOT_DETECTED * 100 / $TOTAL_BOTS" | bc)
  echo "Sensitivity (bots caught):    $BOT_DETECTED/$TOTAL_BOTS  ($SENSITIVITY%)"
else
  echo "Sensitivity: no bot scenarios run"
fi

# False positive rate
if [[ "$TOTAL_HUMANS" -gt 0 ]]; then
  FP_RATE=$(echo "scale=1; $HUMAN_BLOCKED * 100 / $TOTAL_HUMANS" | bc)
  echo "False positives (humans blocked): $HUMAN_BLOCKED/$TOTAL_HUMANS  ($FP_RATE%)"
else
  echo "False positives: no human scenarios run"
fi

echo ""
echo "Suites: $SUITE_PASS passed, $SUITE_FAIL failed"

if [[ ${#FAILURES[@]} -gt 0 ]]; then
  echo ""
  echo "FAILURES:"
  for f in "${FAILURES[@]}"; do
    echo "  - $f"
  done
fi

echo ""

if [[ "$SUITE_FAIL" -gt 0 ]]; then
  echo "RESULT: FAILED ($SUITE_FAIL test suites failed)"
  exit 1
else
  echo "RESULT: ALL PASSED"
  exit 0
fi
