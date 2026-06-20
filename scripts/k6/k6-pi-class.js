/**
 * Pi-class sustained load test: 30 RPS for 15 minutes.
 *
 * Models the realistic load a Pi-class device can sustain without queueing.
 * The plateau test (k6-plateau.js) targets 300 RPS / 500 VUs which is wrong
 * for measuring Pi behavior; it just queues the device and tells us nothing.
 *
 * Run:
 *   k6 run scripts/k6/k6-pi-class.js --env BASE_URL=http://192.168.0.15:5080
 *   k6 run scripts/k6/k6-pi-class.js --env BASE_URL=... --env RPS=50 --env DURATION=30m
 */
import http from 'k6/http';
import { check } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const RPS      = parseInt(__ENV.RPS || '30');
const DURATION = __ENV.DURATION || '15m';

const detectionLatency = new Trend('detection_latency_ms', true);
const botDetected      = new Rate('bot_detected');
const errors           = new Rate('errors');

// 10 distinct human UAs + 5 bot UAs = 15 unique signatures.
// Diverse enough that the per-signature lock contention spreads across buckets.
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

export const options = {
  scenarios: {
    pi: {
      executor: 'constant-arrival-rate',
      rate: RPS, timeUnit: '1s', duration: DURATION,
      preAllocatedVUs: RPS * 2, maxVUs: RPS * 4,
    },
  },
  thresholds: {
    // Pi-class realistic SLA: not a stress test.
    'http_req_duration': ['p(95)<500', 'p(99)<2000'],
    'errors':            ['rate<0.01'],
  },
};

export default function () {
  const ua   = UAS[Math.floor(Math.random() * UAS.length)];
  const path = PATHS[Math.floor(Math.random() * PATHS.length)];

  const r = http.get(`${BASE_URL}${path}`, {
    headers: {
      'User-Agent': ua,
      'Accept':     'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    },
    timeout: '10s',
  });

  const detMs = parseFloat(r.headers['X-Bot-Detection-Processingms'] || '0');
  if (detMs > 0) detectionLatency.add(detMs);

  const isBot = r.headers['X-Bot-Detection-Result'] === 'true';
  botDetected.add(isBot ? 1 : 0);
  errors.add(r.status >= 500 ? 1 : 0);

  check(r, { 'not 5xx': (x) => x.status < 500 });
}

export function handleSummary(data) {
  const m = data.metrics;
  const ms = (k, p) => (m[k]?.values?.[p] ?? 0).toFixed(0);
  const pct = (k) => ((m[k]?.values?.rate ?? 0) * 100).toFixed(1);
  console.log('\n========== Pi-class run ==========');
  console.log(`  Target: ${RPS} RPS for ${DURATION}`);
  console.log(`  Total req: ${m.http_reqs?.values?.count ?? 0}`);
  console.log(`  Achieved RPS: ${(m.http_reqs?.values?.rate ?? 0).toFixed(1)}`);
  console.log(`  Latency med/p95/p99: ${ms('http_req_duration','med')}/${ms('http_req_duration','p(95)')}/${ms('http_req_duration','p(99)')} ms`);
  console.log(`  Detection processing p95: ${ms('detection_latency_ms','p(95)')} ms`);
  console.log(`  Errors (5xx): ${pct('errors')}%`);
  console.log(`  Bot detection rate: ${pct('bot_detected')}%`);
  console.log('==================================\n');
  return { stdout: JSON.stringify(data, null, 2) };
}
