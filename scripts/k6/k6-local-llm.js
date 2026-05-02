import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const botDetected = new Rate('bot_detected');
const detectionLatency = new Trend('detection_latency_ms');
const llmEscalated = new Rate('llm_escalated');

const BASE_URL = 'http://localhost:5080';
// No bypass key - LLM escalation runs for real; policy = "demo" (full sync)
// Set path policy to "demo" in appsettings for this test

export const options = {
  stages: [
    { duration: '30s', target: 5 },
    { duration: '2m',  target: 20 },
    { duration: '1m',  target: 20 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_duration:    ['p(95)<3000'],  // LLM adds latency - 3s p95 is acceptable
    http_req_failed:      ['rate<0.01'],
    detection_latency_ms: ['p(95)<2500'],
  },
};

// Borderline profiles most likely to trigger LLM escalation
// (obvious bots + obvious humans are handled by fast path; edge cases hit LLM)
const profiles = [
  {
    name: 'chrome_human', weight: 30,
    headers: {
      'User-Agent': 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8',
      'Accept-Language': 'en-US,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Sec-Fetch-Dest': 'document',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Site': 'none',
    },
    paths: ['/', '/about', '/docs'],
    thinkTime: () => Math.random() * 1 + 0.5,
  },
  {
    name: 'suspicious_ua', weight: 40,
    headers: {
      'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/100.0.0.0 Safari/537.36',
      'Accept': '*/*',
      'Accept-Language': 'en-US',
    },
    paths: ['/', '/api/data', '/products', '/search'],
    thinkTime: () => Math.random() * 0.3,
  },
  {
    name: 'headless_chrome', weight: 30,
    headers: {
      'User-Agent': 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/125.0.0.0 Safari/537.36',
      'Accept': 'text/html',
      'Accept-Encoding': 'gzip, deflate',
    },
    paths: ['/', '/login', '/api/data'],
    thinkTime: () => Math.random() * 0.2,
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
    headers: profile.headers,
    tags: { profile: profile.name },
    timeout: '10s',
  });

  const isBot = res.headers['X-Bot-IsBot'] === 'true';
  const processingMs = parseFloat(res.headers['X-Bot-Processing-Ms'] || '0');
  const detectors = res.headers['X-Bot-Detectors'] || '';
  const usedLlm = detectors.includes('Llm') || detectors.includes('LlamaSharp');

  botDetected.add(isBot ? 1 : 0);
  detectionLatency.add(processingMs);
  llmEscalated.add(usedLlm ? 1 : 0);

  check(res, {
    'not 5xx': (r) => r.status < 500,
    'has detection header': (r) => r.headers['X-Bot-IsBot'] !== undefined,
  });

  sleep(profile.thinkTime());
}

export function handleSummary(data) {
  const p95 = data.metrics.http_req_duration?.values?.['p(95)']?.toFixed(0) ?? 'N/A';
  const rps = data.metrics.http_reqs?.values?.rate?.toFixed(1) ?? 'N/A';
  const llmRate = (data.metrics.llm_escalated?.values?.rate * 100)?.toFixed(1) ?? 'N/A';
  const detP95 = data.metrics.detection_latency_ms?.values?.['p(95)']?.toFixed(0) ?? 'N/A';
  const botRate = (data.metrics.bot_detected?.values?.rate * 100)?.toFixed(1) ?? 'N/A';

  console.log('\n=== StyloBot LLM-In-Path Load Test ===');
  console.log(`Throughput:       ${rps} req/s`);
  console.log(`Response p95:     ${p95}ms`);
  console.log(`Detection p95:    ${detP95}ms`);
  console.log(`LLM escalation:   ${llmRate}%`);
  console.log(`Bot detection:    ${botRate}%`);
  console.log('=======================================\n');

  return { stdout: JSON.stringify(data, null, 2) };
}
