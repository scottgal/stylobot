import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

const botDetected = new Rate('bot_detected');
const humanPassRate = new Rate('human_pass_rate');
const detectionLatency = new Trend('detection_latency_ms');
const blockedRequests = new Counter('blocked_requests');

const BASE_URL = 'http://localhost:5080';
// Uses logonly policy - nothing gets blocked, all traffic flows through
const LOGONLY_KEY = 'SB-K6-FULL-DETECTION';

export const options = {
  stages: [
    { duration: '30s', target: 10 },
    { duration: '1m',  target: 50 },
    { duration: '1m',  target: 100 },
    { duration: '2m',  target: 100 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_duration:   ['p(95)<500', 'p(99)<1000'],
    http_req_failed:     ['rate<0.01'],
    detection_latency_ms: ['p(95)<20'],
  },
};

// Weighted traffic profiles
const profiles = [
  {
    name: 'chrome_human', weight: 40,
    headers: {
      'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
      'Accept-Language': 'en-US,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Sec-Fetch-Dest': 'document',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Site': 'none',
      'Sec-Fetch-User': '?1',
      'Upgrade-Insecure-Requests': '1',
    },
    paths: ['/', '/about', '/features', '/docs', '/pricing'],
    thinkTime: () => Math.random() * 2 + 0.5,
    isBot: false,
  },
  {
    name: 'curl_bot', weight: 15,
    headers: {
      'User-Agent': 'curl/8.7.1',
      'Accept': '*/*',
    },
    paths: ['/', '/api/health', '/.env', '/wp-admin', '/config.php'],
    thinkTime: () => Math.random() * 0.2,
    isBot: true,
  },
  {
    name: 'python_scraper', weight: 15,
    headers: {
      'User-Agent': 'python-requests/2.31.0',
      'Accept-Encoding': 'gzip, deflate',
      'Accept': '*/*',
      'Connection': 'keep-alive',
    },
    paths: ['/', '/api/data', '/sitemap.xml', '/robots.txt', '/api/v1/items'],
    thinkTime: () => Math.random() * 0.3,
    isBot: true,
  },
  {
    name: 'googlebot', weight: 10,
    headers: {
      'User-Agent': 'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
      'Accept': 'text/html',
      'Accept-Encoding': 'gzip, deflate, br',
    },
    paths: ['/', '/about', '/docs', '/sitemap.xml'],
    thinkTime: () => Math.random() * 1 + 0.5,
    isBot: true,
  },
  {
    name: 'headless_chrome', weight: 10,
    headers: {
      'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html,application/xhtml+xml',
      'Accept-Encoding': 'gzip, deflate, br',
    },
    paths: ['/', '/login', '/api/data', '/checkout'],
    thinkTime: () => Math.random() * 0.1,
    isBot: true,
  },
  {
    name: 'sqlmap', weight: 5,
    headers: {
      'User-Agent': 'sqlmap/1.7 (http://sqlmap.org)',
      'Accept': '*/*',
    },
    paths: ['/api/search?q=1%27+OR+%271%27%3D%271', '/login?id=1+AND+1=1'],
    thinkTime: () => 0,
    isBot: true,
  },
  {
    name: 'nikto', weight: 5,
    headers: {
      'User-Agent': 'Mozilla/5.00 (Nikto/2.1.6) (Evasions:None) (Test:Port Check)',
    },
    paths: ['/.git/config', '/backup.sql', '/phpmyadmin', '/wp-login.php'],
    thinkTime: () => 0,
    isBot: true,
  },
];

function pickProfile() {
  const total = profiles.reduce((sum, p) => sum + p.weight, 0);
  let r = Math.random() * total;
  for (const p of profiles) {
    r -= p.weight;
    if (r <= 0) return p;
  }
  return profiles[0];
}

export default function () {
  const profile = pickProfile();
  const path = profile.paths[Math.floor(Math.random() * profile.paths.length)];

  const res = http.get(`${BASE_URL}${path}`, {
    headers: {
      ...profile.headers,
      'X-SB-Api-Key': LOGONLY_KEY,
    },
    tags: { profile: profile.name },
  });

  const riskScore = parseFloat(res.headers['X-Bot-Risk-Score'] || '0');
  const isBot = riskScore > 0.7;
  const processingMs = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');

  botDetected.add(isBot ? 1 : 0);
  humanPassRate.add(!profile.isBot && !isBot ? 1 : 0);
  if (processingMs > 0) detectionLatency.add(processingMs);
  if (res.status === 403) blockedRequests.add(1);

  check(res, {
    'not 5xx': (r) => r.status < 500,
    'has detection header': (r) => r.headers['X-Bot-Risk-Score'] !== undefined,
    'detection under 50ms': () => processingMs < 50,
  });

  sleep(profile.thinkTime());
}

export function handleSummary(data) {
  const p95 = data.metrics.http_req_duration?.values?.['p(95)']?.toFixed(1) ?? 'N/A';
  const p99 = data.metrics.http_req_duration?.values?.['p(99)']?.toFixed(1) ?? 'N/A';
  const rps = data.metrics.http_reqs?.values?.rate?.toFixed(1) ?? 'N/A';
  const botRate = (data.metrics.bot_detected?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detP95 = data.metrics.detection_latency_ms?.values?.['p(95)']?.toFixed(2) ?? 'N/A';

  console.log('\n=== StyloBot Local Log-Mode Load Test ===');
  console.log(`Throughput:        ${rps} req/s`);
  console.log(`Response p95/p99:  ${p95}ms / ${p99}ms`);
  console.log(`Detection p95:     ${detP95}ms`);
  console.log(`Bot detection rate: ${botRate}%`);
  console.log('=========================================\n');

  return { stdout: JSON.stringify(data, null, 2) };
}
