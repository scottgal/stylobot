import http from 'k6/http';
import { check } from 'k6';
import { Rate, Counter, Trend } from 'k6/metrics';

const blocked = new Rate('blocked_rate');
const detected = new Rate('detected_rate');
const detectionLatency = new Trend('detection_latency_ms');
const errors = new Counter('error_count');

const BASE_URL = 'http://localhost:5080';
// No API key - bots should get blocked or holodeck'd under realfast policy

export const options = {
  stages: [
    { duration: '30s', target: 50 },
    { duration: '30s', target: 200 },
    { duration: '30s', target: 500 },
    { duration: '1m',  target: 500 },
    { duration: '30s', target: 1000 },
    { duration: '1m',  target: 1000 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_failed:      ['rate<0.05'],   // <5% errors at peak (503s acceptable, 5xxs not)
    detection_latency_ms: ['p(99)<100'],   // Even at 1000 VUs, detection stays <100ms p99
  },
};

// Pure bot flood - no think time, no sleep
const attackProfiles = [
  {
    headers: { 'User-Agent': 'curl/8.7.1', 'Accept': '*/*' },
    paths: ['/', '/.env', '/wp-login.php', '/admin'],
  },
  {
    headers: { 'User-Agent': 'sqlmap/1.7 (http://sqlmap.org)', 'Accept': '*/*' },
    paths: ['/api/search?q=1+OR+1=1', '/login?id=1--'],
  },
  {
    headers: { 'User-Agent': 'python-requests/2.31.0', 'Accept': '*/*' },
    paths: ['/', '/api/data', '/sitemap.xml'],
  },
  {
    headers: { 'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 HeadlessChrome/125.0.0.0 Safari/537.36' },
    paths: ['/', '/login', '/checkout'],
  },
  {
    headers: { 'User-Agent': 'Mozilla/5.00 (Nikto/2.1.6) (Evasions:None)' },
    paths: ['/.git/config', '/backup.sql', '/phpmyadmin'],
  },
];

export default function () {
  const profile = attackProfiles[Math.floor(Math.random() * attackProfiles.length)];
  const path = profile.paths[Math.floor(Math.random() * profile.paths.length)];

  const res = http.get(`${BASE_URL}${path}`, {
    headers: profile.headers,
    timeout: '5s',
  });

  const isBot = res.headers['X-Bot-IsBot'] === 'true';
  const processingMs = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');

  detected.add(isBot ? 1 : 0);
  blocked.add(res.status === 403 ? 1 : 0);
  if (processingMs > 0) detectionLatency.add(processingMs);
  if (res.status >= 500) errors.add(1);

  check(res, {
    'not 5xx': (r) => r.status < 500,
    'blocked or ok': (r) => r.status === 200 || r.status === 403 || r.status === 429,
  });

  // No sleep - pure flood
}

export function handleSummary(data) {
  const p95 = data.metrics.http_req_duration?.values?.['p(95)']?.toFixed(0) ?? 'N/A';
  const p99 = data.metrics.http_req_duration?.values?.['p(99)']?.toFixed(0) ?? 'N/A';
  const rps = data.metrics.http_reqs?.values?.rate?.toFixed(0) ?? 'N/A';
  const blockedRate = (data.metrics.blocked_rate?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detectedRate = (data.metrics.detected_rate?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detP99 = data.metrics.detection_latency_ms?.values?.['p(99)']?.toFixed(1) ?? 'N/A';
  const errCount = data.metrics.error_count?.values?.count ?? 0;

  console.log('\n=== StyloBot DDoS Flood Test ===');
  console.log(`Peak RPS:         ${rps} req/s`);
  console.log(`Response p95/p99: ${p95}ms / ${p99}ms`);
  console.log(`Detection p99:    ${detP99}ms`);
  console.log(`Bot detected:     ${detectedRate}%`);
  console.log(`Requests blocked: ${blockedRate}%`);
  console.log(`5xx errors:       ${errCount}`);
  console.log('================================\n');

  return { stdout: JSON.stringify(data, null, 2) };
}
