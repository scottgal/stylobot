#!/usr/bin/env bash
# Poll the k6 REST API (default :6565) until k6 exits, printing one CSV row
# every INTERVAL seconds. Designed to be tee'd into a log for graphing later.
#
# Usage: poll-k6.sh [INTERVAL_SEC] [API_URL]

INTERVAL=${1:-30}
API=${2:-http://localhost:6565}

echo "time,vus,vus_max,rps,med_ms,p95_ms,max_ms,errors,iters"

until ! curl -s -m 3 "$API/v1/status" 2>/dev/null | grep -q '"running":true'; do
  ts=$(date +%H:%M:%S)

  status=$(curl -s -m 3 "$API/v1/status" 2>/dev/null)
  metrics=$(curl -s -m 3 "$API/v1/metrics" 2>/dev/null)

  row=$(python3 -c "
import json
try:
  s = json.loads('''$status''')['data']['attributes']
  m = json.loads('''$metrics''')['data']
  mm = {d['id']: d['attributes'].get('sample',{}) for d in m}
  d = mm.get('http_req_duration', {})
  print(f'$ts,{s[\"vus\"]},{s[\"vus-max\"]},{round(mm.get(\"http_reqs\",{}).get(\"rate\",0),1)},{round(d.get(\"med\",0))},{round(d.get(\"p(95)\",0))},{round(d.get(\"max\",0))},{mm.get(\"request_errors\",{}).get(\"rate\",0)},{round(mm.get(\"iterations\",{}).get(\"rate\",0),1)}')
except Exception as e:
  print(f'$ts,-,-,-,-,-,-,-,-')
" 2>/dev/null)
  echo "$row"

  sleep "$INTERVAL"
done
