/**
 * k6 detection throughput benchmark
 *
 * Measures detection pipeline latency at realistic traffic mix.
 * Exercises the Slim* similarity search hot path by sending requests
 * with varied User-Agents and IPs that will (or won't) hit the cache.
 *
 * Run:
 *   k6 run scripts/k6/k6-detection-throughput.js
 *   k6 run --vus 50 --duration 60s scripts/k6/k6-detection-throughput.js
 *   k6 run -e BASE_URL=http://localhost:5080 scripts/k6/k6-detection-throughput.js
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend, Rate } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';

// Custom metrics
const detectionLatency  = new Trend('detection_latency_ms', true);
const botDetected       = new Rate('bot_detected');
const humanPassthrough  = new Rate('human_passthrough');
const similarityHit     = new Rate('similarity_search_hit');
const dropCount         = new Counter('hp_mode_drops');

// -----------------------------------------------------------------------
// Test scenarios
// -----------------------------------------------------------------------

export const options = {
  summaryTrendStats: ['med', 'p(95)', 'p(99)'],
  scenarios: {
    // Ramp-up: find throughput ceiling
    ramp: {
      executor: 'ramping-vus',
      startVUs: 1,
      stages: [
        { duration: '15s', target: 10 },
        { duration: '30s', target: 30 },
        { duration: '30s', target: 60 },
        { duration: '15s', target: 0  },
      ],
      gracefulRampDown: '5s',
    },
  },
  thresholds: {
    // Full pipeline p95 < 300ms, p99 < 1s (dev machine, all 49 detectors + SQLite writes)
    // Similarity search hot path alone is <1ms — see BenchmarkDotNet results
    'http_req_duration{scenario:ramp}': ['p(95)<300', 'p(99)<1000'],
    // Error rate under 5% (some timeouts expected at 60 VUs on dev machine)
    http_req_failed:      ['rate<0.05'],
    detection_latency_ms: ['p(95)<300', 'p(99)<1000'],
    // Bot detection should catch most bot traffic (k6 clients look like bots — no browser fingerprints)
    bot_detected:         ['rate>0.5'],
  },
};

// -----------------------------------------------------------------------
// Traffic profiles
// -----------------------------------------------------------------------

const humanProfiles = [
  { ua: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
    accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8' },
  { ua: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15',
    accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' },
  { ua: 'Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0',
    accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8' },
  { ua: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
    accept: 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8' },
];

const botProfiles = [
  { ua: 'python-requests/2.32.3',          accept: '*/*' },
  { ua: 'curl/8.6.0',                      accept: '*/*' },
  { ua: 'Go-http-client/1.1',              accept: '*/*' },
  { ua: 'scrapy/2.11.0 (+https://scrapy.org)', accept: '*/*' },
  { ua: 'Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)', accept: '*/*' },
  { ua: 'GPTBot/1.2 (+https://openai.com/gptbot)', accept: '*/*' },
  { ua: '',                                 accept: '*/*' },  // empty UA
];

// Datacenter IPs that feed the similarity cache
const datacenterIPs = [
  '45.33.32.156', '104.24.0.1', '198.41.128.1', '162.159.0.1',
  '13.32.0.1', '54.230.0.1', '151.101.0.1', '185.220.101.1',
];

const residentialIPs = [
  '82.0.0.1', '94.0.0.1', '62.0.0.1', '78.0.0.1',
  '5.0.0.1',  '31.0.0.1', '89.0.0.1', '46.0.0.1',
];

function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }

// -----------------------------------------------------------------------
// Request builders
// -----------------------------------------------------------------------

function humanRequest() {
  const profile = pick(humanProfiles);
  const ip = pick(residentialIPs);
  return {
    path: pick(['/api/demo/protected', '/api/demo/analyze', '/api/demo/open']),
    headers: {
      'User-Agent': profile.ua,
      'Accept': profile.accept,
      'Accept-Language': 'en-GB,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Cache-Control': 'no-cache',
      'Sec-Fetch-Site': 'none',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Dest': 'document',
      'X-Forwarded-For': ip,
    },
    isBot: false,
  };
}

function botRequest() {
  const profile = pick(botProfiles);
  const ip = pick(datacenterIPs);
  return {
    path: '/api/demo/protected',
    headers: {
      'User-Agent': profile.ua,
      'Accept': profile.accept,
      'X-Forwarded-For': ip,
    },
    isBot: true,
  };
}

// -----------------------------------------------------------------------
// Main
// -----------------------------------------------------------------------

export default function () {
  // 40% bot, 60% human — mirrors typical web traffic under attack
  const req = Math.random() < 0.4 ? botRequest() : humanRequest();

  const start = Date.now();
  const res = http.get(`${BASE_URL}${req.path}`, { headers: req.headers, timeout: '5s' });
  const elapsed = Date.now() - start;

  detectionLatency.add(elapsed);

  const isBot  = res.status === 403 ||
                 res.headers['X-Stylobot-Isbot'] === 'true' ||
                 (res.headers['X-Stylobot-Probability'] && parseFloat(res.headers['X-Stylobot-Probability']) > 0.7);

  const simScore = res.headers['X-Stylobot-Similarity-Score'];
  if (simScore && parseFloat(simScore) > 0) {
    similarityHit.add(1);
  } else {
    similarityHit.add(0);
  }

  if (req.isBot) {
    botDetected.add(isBot ? 1 : 0);
  } else {
    humanPassthrough.add(!isBot ? 1 : 0);
  }

  check(res, {
    'status not 5xx':    (r) => r.status < 500,
    'responded quickly': (r) => r.timings.duration < 500,
  });

  // Minimal pacing — we're testing throughput
  sleep(Math.random() * 0.05);
}

export function handleSummary(data) {
  const d = data.metrics;
  // k6 Trend uses 'med' for the median; p(99) is included by default
  const p50  = (d.detection_latency_ms?.values?.med)?.toFixed(1)       ?? 'n/a';
  const p95  = d.detection_latency_ms?.values?.['p(95)']?.toFixed(1)   ?? 'n/a';
  const p99  = d.detection_latency_ms?.values?.['p(99)']?.toFixed(1)   ?? 'n/a';
  const rps  = d.http_reqs?.values?.rate?.toFixed(1)                    ?? 'n/a';
  const bdr  = (d.bot_detected?.values?.rate * 100)?.toFixed(1)         ?? 'n/a';
  const hpr  = (d.human_passthrough?.values?.rate * 100)?.toFixed(1)    ?? 'n/a';
  const simr = (d.similarity_search_hit?.values?.rate * 100)?.toFixed(1) ?? 'n/a';

  const report = `
╔══════════════════════════════════════════════════════════╗
║          Detection Throughput Benchmark Results          ║
╠══════════════════════════════════════════════════════════╣
║  Throughput       ${rps.padStart(8)} req/s                       ║
║  Detection p50    ${p50.padStart(8)} ms                          ║
║  Detection p95    ${p95.padStart(8)} ms                          ║
║  Detection p99    ${p99.padStart(8)} ms                          ║
║  Bot detection    ${bdr.padStart(7)}%                           ║
║  Human passthru   ${hpr.padStart(7)}%                           ║
║  Similarity hits  ${simr.padStart(7)}%                           ║
╚══════════════════════════════════════════════════════════╝
`;
  return { stdout: report };
}
