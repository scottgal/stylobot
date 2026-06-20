/**
 * StyloBot enforcement test: humans pass, bots get limited.
 *
 * Runs a mixed traffic mix against a gateway in production mode (where action
 * policies actually fire) and checks:
 *   - human UAs → HTTP 200 majority, p95 latency reasonable
 *   - bot UAs → HTTP 429 (throttle-tools / throttle-status) or 403 (block)
 *   - 5xx errors → none (gateway never crashes under enforcement load)
 *
 * Usage:
 *   k6 run scripts/k6/k6-allow-humans-limit-bots.js --env BASE_URL=http://192.168.0.15:5080
 *   k6 run scripts/k6/k6-allow-humans-limit-bots.js --env BASE_URL=... --env DURATION=10m
 */
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const BASE_URL  = __ENV.BASE_URL || 'http://localhost:5080';
const DURATION  = __ENV.DURATION || '5m';
const HUMAN_RPS = parseInt(__ENV.HUMAN_RPS || '40');
const BOT_RPS   = parseInt(__ENV.BOT_RPS   || '40');

const humanAllowed   = new Rate('human_allowed');     // human UA → 200
const humanBlocked   = new Rate('human_blocked');     // human UA → 429/403 (BAD)
const humanLatencyMs = new Trend('human_latency_ms');

const botAllowed   = new Rate('bot_allowed');         // bot UA → 200 (LEAKAGE)
const botLimited   = new Rate('bot_limited');         // bot UA → 429 (good)
const botBlocked   = new Rate('bot_blocked');         // bot UA → 403 (good)
const botLatencyMs = new Trend('bot_latency_ms');

const HUMAN_UAS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:122.0) Gecko/20100101 Firefox/122.0',
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1',
];

const BOT_UAS = [
  'curl/8.6.0',
  'python-requests/2.31.0',
  'Go-http-client/1.1',
  'Scrapy/2.11.0',
  'wget/1.21.4',
  'axios/1.6.0',
];

const HUMAN_PATHS = ['/', '/products', '/about', '/blog', '/pricing'];
const BOT_PATHS   = ['/', '/api/data', '/sitemap.xml', '/robots.txt'];

const HUMAN_HEADERS = {
  'Accept':          'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
  'Accept-Language': 'en-US,en;q=0.9',
  'Accept-Encoding': 'gzip, deflate, br',
  'Sec-Fetch-Dest':  'document',
  'Sec-Fetch-Mode':  'navigate',
  'Sec-Fetch-Site':  'none',
};

const BOT_HEADERS = {
  'Accept': '*/*',
};

export const options = {
  scenarios: {
    humans: {
      executor: 'constant-arrival-rate',
      rate: HUMAN_RPS, timeUnit: '1s', duration: DURATION,
      preAllocatedVUs: HUMAN_RPS, maxVUs: HUMAN_RPS * 4,
      exec: 'driveHuman',
    },
    bots: {
      executor: 'constant-arrival-rate',
      rate: BOT_RPS, timeUnit: '1s', duration: DURATION,
      preAllocatedVUs: BOT_RPS, maxVUs: BOT_RPS * 4,
      exec: 'driveBot',
    },
  },
  thresholds: {
    // Enforcement target: humans largely allowed, bots largely NOT allowed.
    'human_allowed':   ['rate>0.90'],
    'bot_allowed':     ['rate<0.20'],
    'http_req_failed': ['rate<0.02'],
  },
};

export function driveHuman() {
  const ua   = HUMAN_UAS[Math.floor(Math.random() * HUMAN_UAS.length)];
  const path = HUMAN_PATHS[Math.floor(Math.random() * HUMAN_PATHS.length)];
  const r = http.get(`${BASE_URL}${path}`, { headers: { ...HUMAN_HEADERS, 'User-Agent': ua }, timeout: '15s' });
  humanLatencyMs.add(r.timings.duration);
  humanAllowed.add(r.status === 200 ? 1 : 0);
  humanBlocked.add(r.status === 429 || r.status === 403 ? 1 : 0);
  check(r, { 'human not 5xx': (x) => x.status < 500 });
}

export function driveBot() {
  const ua   = BOT_UAS[Math.floor(Math.random() * BOT_UAS.length)];
  const path = BOT_PATHS[Math.floor(Math.random() * BOT_PATHS.length)];
  const r = http.get(`${BASE_URL}${path}`, { headers: { ...BOT_HEADERS, 'User-Agent': ua }, timeout: '15s' });
  botLatencyMs.add(r.timings.duration);
  botAllowed.add(r.status === 200 ? 1 : 0);
  botLimited.add(r.status === 429 ? 1 : 0);
  botBlocked.add(r.status === 403 ? 1 : 0);
  check(r, { 'bot not 5xx': (x) => x.status < 500 });
}

export function handleSummary(data) {
  const m = data.metrics;
  const pct = (k) => ((m[k]?.values?.rate ?? 0) * 100).toFixed(1);
  const ms  = (k, p) => (m[k]?.values?.[p] ?? 0).toFixed(0);
  console.log('\n========== HUMANS vs BOTS ENFORCEMENT ==========');
  console.log(`Duration: ${DURATION}   Human RPS: ${HUMAN_RPS}   Bot RPS: ${BOT_RPS}`);
  console.log('');
  console.log(`  HUMAN  allowed: ${pct('human_allowed')}%   blocked: ${pct('human_blocked')}%`);
  console.log(`         p50/p95 latency: ${ms('human_latency_ms','med')}ms / ${ms('human_latency_ms','p(95)')}ms`);
  console.log('');
  console.log(`  BOT    allowed: ${pct('bot_allowed')}%   limited(429): ${pct('bot_limited')}%   blocked(403): ${pct('bot_blocked')}%`);
  console.log(`         p50/p95 latency: ${ms('bot_latency_ms','med')}ms / ${ms('bot_latency_ms','p(95)')}ms`);
  console.log('');
  console.log(`  5xx errors: ${((m.http_req_failed?.values?.rate ?? 0) * 100).toFixed(2)}%`);
  console.log('================================================\n');
  return { stdout: JSON.stringify(data, null, 2) };
}
