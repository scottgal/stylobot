/**
 * SBC benchmark driver — single-level load run for the sbc-bench harness.
 *
 * One level of a ceiling ladder (MODE=ceiling, fixed RPS) or one soak window
 * (MODE=soak, fixed RPS, longer). The ladder/flatness logic lives in
 * sbc-bench.sh — this script only serves one steady level and reports
 * latency/error/drop signals per level. That keeps every level's numbers
 * directly comparable (each is its own summary-export JSON).
 *
 * Traffic mix (realistic "average user" shape, NOT just /health):
 *   - ~85% site page loads: 10 human browser UAs + 5 bot UAs, 6 paths
 *   - ~15% dashboard traffic: /stylobot/traffic (SSR page) + SignalR
 *     negotiate + /api/summary + /api/timeseries
 *   - SITES=3 rotates the Host header across 3 domains (multi-site mode);
 *     SITES=1 sends no Host override (catch-all route).
 *
 * Every request carries X-SB-Api-Key (poison-safe — the bench key is
 * configured on the gateway with ActionPolicyName=logonly so the synthetic
 * flood does NOT train the learned model).
 *
 * Usage (driven by sbc-bench.sh; also runnable standalone):
 *   k6 run k6-sbc.js --env TARGET=http://192.168.0.39:8080 \
 *     --env MODE=ceiling --env RPS=30 --env DURATION=90s \
 *     --env SITES=3 --env API_KEY=SB-BENCH \
 *     --summary-export results/level-30.json
 */
import http from 'k6/http';
import { check } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const TARGET     = __ENV.TARGET || 'http://localhost:8080';
const MODE       = __ENV.MODE || 'ceiling';        // ceiling | soak
const RPS        = parseInt(__ENV.RPS || '30');
const DURATION   = __ENV.DURATION || (MODE === 'soak' ? '5m' : '90s');
const SITES      = parseInt(__ENV.SITES || '1');
const DASH_RATIO = parseFloat(__ENV.DASH_RATIO || '0.15');
const API_KEY    = __ENV.API_KEY || '';
const BASE_PATH  = __ENV.BASE_PATH || '/stylobot';

const SITE_HOSTS = ['site1.test', 'site2.test', 'site3.test'];

const detectionLatency = new Trend('detection_latency_ms', true);
const errors           = new Rate('errors');
const siteLatency      = new Trend('site_latency_ms', true);
const dashLatency      = new Trend('dashboard_latency_ms', true);

// 10 human UAs + 5 bot UAs — same spread the pi-class test uses.
const UAS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36',
  'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:122.0) Gecko/20100101 Firefox/122.0',
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1',
  'Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Mobile Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36 Edg/137.0.0.0',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36 OPR/110.0.0.0',
  'curl/8.6.0',
  'python-requests/2.31.0',
  'Go-http-client/1.1',
  'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
  'Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)',
];

const PATHS = ['/', '/products', '/about', '/blog', '/api/data', '/pricing'];

// Dashboard realism: the V2 Traffic page is SSR + content-cache bundle +
// SignalR negotiate, with the API fetches the page triggers.
const DASH_ENDPOINTS = [
  { method: 'GET',  path: `${BASE_PATH}/traffic`, headers: { 'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' } },
  { method: 'POST', path: `${BASE_PATH}/hub/negotiate?negotiateVersion=1`, headers: { 'Accept': '*/*', 'Content-Type': 'text/plain;charset=UTF-8', 'X-Requested-With': 'XMLHttpRequest' } },
  { method: 'GET',  path: `${BASE_PATH}/api/summary`, headers: { 'Accept': 'application/json' } },
  { method: 'GET',  path: `${BASE_PATH}/api/timeseries`, headers: { 'Accept': 'application/json' } },
];

export const options = {
  scenarios: {
    sbc: {
      executor: 'constant-arrival-rate',
      rate: RPS, timeUnit: '1s', duration: DURATION,
      preAllocatedVUs: Math.max(RPS, 10),
      maxVUs: Math.max(RPS * 4, 40),
    },
  },
  thresholds: {
    // SLA for the ceiling ladder (and soak): p95 < 500 ms, < 1% errors.
    'http_req_duration': ['p(95)<500'],
    'errors':            ['rate<0.01'],
  },
};

function baseHeaders() {
  const h = {
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    'Accept-Language': 'en-US,en;q=0.9',
  };
  if (SITES === 3) h['Host'] = SITE_HOSTS[Math.floor(Math.random() * SITE_HOSTS.length)];
  if (API_KEY) h['X-SB-Api-Key'] = API_KEY;
  return h;
}

export default function () {
  const roll = Math.random();
  const headers = baseHeaders();

  if (roll < 1 - DASH_RATIO) {
    // Site traffic: random human/bot UA + path through the detection pipeline.
    headers['User-Agent'] = UAS[Math.floor(Math.random() * UAS.length)];
    const r = http.get(`${TARGET}${PATHS[Math.floor(Math.random() * PATHS.length)]}`, {
      headers, timeout: '10s', tags: { class: 'site' },
    });
    siteLatency.add(r.timings.duration);
    errors.add(r.status >= 500 ? 1 : 0);
    const detMs = parseFloat(r.headers['X-Bot-Detection-Processingms'] || '0');
    if (detMs > 0) detectionLatency.add(detMs);
    check(r, { 'site not 5xx': (x) => x.status < 500 });
  } else {
    // Dashboard traffic: one page/negotiate/api fetch (realistic dash user).
    const ep = DASH_ENDPOINTS[Math.floor(Math.random() * DASH_ENDPOINTS.length)];
    headers['User-Agent'] = UAS[Math.floor(Math.random() * 9)]; // human UA only
    headers['Sec-Fetch-Site'] = 'same-origin';
    headers['Sec-Fetch-Mode'] = 'cors';
    headers['Sec-Fetch-Dest'] = 'empty';
    headers['Cache-Control'] = 'no-cache';
    const r = ep.method === 'GET'
      ? http.get(`${TARGET}${ep.path}`, { headers, timeout: '10s', tags: { class: 'dashboard' } })
      : http.post(`${TARGET}${ep.path}`, null, { headers, timeout: '10s', tags: { class: 'dashboard' } });
    dashLatency.add(r.timings.duration);
    errors.add(r.status >= 500 ? 1 : 0);
    check(r, { 'dashboard not 5xx': (x) => x.status < 500 });
  }
}

export function handleSummary(data) {
  const m = data.metrics;
  const ms = (k, p) => (m[k]?.values?.[p] ?? 0).toFixed(0);
  const pct = (k) => ((m[k]?.values?.rate ?? 0) * 100).toFixed(1);
  console.log('\n========== SBC run ==========');
  console.log(`  Mode: ${MODE}  RPS: ${RPS}  Dur: ${DURATION}  Sites: ${SITES}  Dash: ${(DASH_RATIO * 100).toFixed(0)}%`);
  console.log(`  Total req: ${m.http_reqs?.values?.count ?? 0}`);
  console.log(`  Achieved RPS: ${(m.http_reqs?.values?.rate ?? 0).toFixed(1)}`);
  console.log(`  Latency med/p95/p99: ${ms('http_req_duration','med')}/${ms('http_req_duration','p(95)')}/${ms('http_req_duration','p(99)')} ms`);
  console.log(`  Site p95: ${ms('site_latency_ms','p(95)')} ms   Dashboard p95: ${ms('dashboard_latency_ms','p(95)')} ms`);
  console.log(`  Detection processing p95: ${ms('detection_latency_ms','p(95)')} ms`);
  console.log(`  Errors: ${pct('errors')}%   Dropped iters: ${m.dropped_iterations?.values?.count ?? 0}`);
  console.log('==================================\n');
  return { stdout: JSON.stringify(data, null, 2) };
}
