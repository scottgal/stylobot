// Memory-pressure via HIGH SIGNATURE CARDINALITY.
//
// Existing k6 scripts reuse ~15 UAs -> ~15 distinct signatures, which never
// fills the bounded stores. This script gives EVERY request a unique UA (and a
// unique X-Forwarded-For), so it mints a fresh PrimarySignature per request.
// Over a few minutes that is tens of thousands of distinct identities -- far
// past SignatureCacheSize (5k), SessionCacheSize (2k), MarkovCohortSize (10k),
// and the WriteBehindLfu caps. If the post-refactor bounding holds, process RSS
// climbs to the cap footprint then PLATEAUS. If any identity-keyed store is
// still unbounded, RSS grows ~linearly with total requests -> a leak.
//
// Run the Demo on :5080, sample its RSS alongside this, and compare the RSS
// curve against total distinct signatures seen.
//
//   k6 run scripts/k6/k6-memory-cardinality.js
//
// Tunables via env:  RPS (default 150), DURATION (default 6m), SESSION_LEN (3)

import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const RPS         = parseInt(__ENV.RPS || '150');
const DURATION    = __ENV.DURATION || '6m';
const SESSION_LEN = parseInt(__ENV.SESSION_LEN || '3'); // requests sharing one signature (fills session store)

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const API_KEY  = __ENV.API_KEY || '';
if (!API_KEY) {
  throw new Error('API_KEY is required -- keyless traffic poisons the corpus. Pass --env API_KEY=<gateway-debug-api-key>.');
}

const uniqueSignatures = new Counter('unique_signatures_minted');
const detectionLatency = new Trend('detection_latency_ms');

export const options = {
  scenarios: {
    cardinality_flood: {
      executor: 'constant-arrival-rate',
      rate: RPS,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: parseInt(__ENV.PREVU || Math.max(50, RPS)),
      maxVUs: parseInt(__ENV.MAXVU || (RPS * 4)),  // cap => simulates a server in-flight limit
    },
  },
  thresholds: {
    // We are not asserting latency here; the point is the RSS curve. Keep loose
    // so the run does not abort, but flag hard failures.
    http_req_failed: ['rate<0.10'],
    http_req_duration: ['p(99)<3000'],
  },
};

const PATHS = ['/', '/about', '/docs', '/api/data', '/products', '/search',
               '/.env', '/wp-login.php', '/admin', '/api/v1/items', '/sitemap.xml'];

// Base UA shells; the unique token is appended so each is a distinct signature.
const UA_SHELLS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{T} Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/{T} Safari/605.1.15',
  'python-requests/2.{T}',
  'curl/8.{T}',
  'Scraper/{T} (+http://bot-{T}.example.com)',
];

function octet() { return Math.floor(Math.random() * 254) + 1; }

export default function () {
  // One unique identity, reused for SESSION_LEN requests to also fill the session store.
  const token = `${__VU}-${__ITER}-${Math.random().toString(36).slice(2, 10)}`;
  const shell = UA_SHELLS[Math.floor(Math.random() * UA_SHELLS.length)];
  const ua = shell.replace(/\{T\}/g, token);
  // Unique source IP too (used if the Demo trusts XFF; harmless otherwise).
  const xff = `${octet()}.${octet()}.${octet()}.${octet()}`;
  uniqueSignatures.add(1);

  for (let i = 0; i < SESSION_LEN; i++) {
    const path = PATHS[Math.floor(Math.random() * PATHS.length)];
    const res = http.get(`${BASE_URL}${path}`, {
      headers: {
        'User-Agent': ua,
        'X-Forwarded-For': xff,
        'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
        'Accept-Language': 'en-US,en;q=0.9',
        'X-SB-Api-Key': API_KEY,
      },
      tags: { name: 'cardinality' },
    });
    const ms = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
    if (ms > 0) detectionLatency.add(ms);
    check(res, { 'not 5xx': (r) => r.status < 500 });
  }
}

export function handleSummary(data) {
  const reqs = data.metrics.http_reqs?.values?.count ?? 0;
  const sigs = data.metrics.unique_signatures_minted?.values?.count ?? 0;
  const p95 = data.metrics.http_req_duration?.values?.['p(95)']?.toFixed(0) ?? 'N/A';
  const detP95 = data.metrics.detection_latency_ms?.values?.['p(95)']?.toFixed(2) ?? 'N/A';
  const failed = (data.metrics.http_req_failed?.values?.rate * 100)?.toFixed(2) ?? 'N/A';
  console.log('\n=== StyloBot Memory-Cardinality Soak ===');
  console.log(`Total requests:       ${reqs}`);
  console.log(`Distinct signatures:  ~${sigs}`);
  console.log(`Response p95:         ${p95}ms`);
  console.log(`Detection p95:        ${detP95}ms`);
  console.log(`Failed rate:          ${failed}%`);
  console.log('Compare against the RSS curve: bounded => plateau, leak => linear.');
  console.log('========================================\n');
  return { stdout: JSON.stringify(data, null, 2) };
}
