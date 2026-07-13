/**
 * StyloBot Plateau / Breaking Point Test
 *
 * Ramps traffic in steps, holds each plateau for 2 minutes,
 * measures latency/error rate at each level. Finds the breaking point.
 *
 * Usage:
 *   k6 run scripts/soak/k6-plateau.js --env TARGET=http://192.168.0.89:5080
 *   k6 run scripts/soak/k6-plateau.js --env TARGET=http://192.168.0.89:5080 --env MAX_RPS=500
 */

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

const TARGET = __ENV.TARGET || 'http://192.168.0.89:5080';
const MAX_RPS = parseInt(__ENV.MAX_RPS || '300');
// Debug/ops key sent as X-SB-Api-Key on EVERY request. Set it (to a key with
// DisableLearningWrites) so a soak's synthetic traffic does NOT train the model — un-keyed soak
// traffic is learned and poisons centroids/reputation. Leave empty ONLY when deliberately
// exercising the learning pipeline. See feedback_always_api_key_on_stylobot_traffic.
const API_KEY = __ENV.API_KEY || '';

// Custom metrics
const detectionLatency = new Trend('detection_latency_ms');
const botDetectionRate = new Rate('bot_detected');
const errorRate = new Rate('request_errors');
const plateauLevel = new Counter('plateau_level');

// 40 realistic browser UAs
const HUMAN_UAS = [
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0',
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15',
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1',
  'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0',
];

const BOT_UAS = [
  'curl/8.4.0',
  'python-requests/2.31.0',
  'Go-http-client/1.1',
  'Scrapy/2.11.0',
  'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)',
  'Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)',
  'Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)',
  'wget/1.21.4',
  'axios/1.6.0',
  'node-fetch/1.0',
];

const PATHS = ['/', '/products', '/products/123', '/api/data', '/about', '/contact', '/blog', '/blog/post-1'];
const ATTACK_PATHS = ['/wp-login.php', '/.env', '/phpmyadmin/', '/.git/config', "/products?id=1' OR '1'='1"];
const HONEYPOT_PATHS = ['/wp-admin/', '/xmlrpc.php', '/config.php', '/backup.sql'];

// Build ramp stages: 10 → 20 → 50 → 100 → 150 → 200 → MAX_RPS, 2 min each
function buildStages() {
  const levels = [10, 20, 50, 100, 150, 200];
  if (MAX_RPS > 200) levels.push(MAX_RPS);

  const stages = [];
  for (const level of levels) {
    if (level > MAX_RPS) break;
    stages.push({ duration: '30s', target: level });  // ramp up
    stages.push({ duration: '90s', target: level });  // hold plateau
  }
  return stages;
}

export const options = {
  scenarios: {
    plateau: {
      executor: 'ramping-arrival-rate',
      startRate: 5,
      timeUnit: '1s',
      preAllocatedVUs: 200,
      maxVUs: 500,
      stages: buildStages(),
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<2000'],  // Will likely be exceeded at breaking point
    request_errors: ['rate<0.20'],       // 20% error = definitely broken
  },
};

export default function () {
  // Mix: 60% human, 25% bot, 10% attack, 5% honeypot
  const roll = Math.random();

  let url, headers, tag;

  if (roll < 0.60) {
    // Human browsing
    const ua = HUMAN_UAS[Math.floor(Math.random() * HUMAN_UAS.length)];
    const path = PATHS[Math.floor(Math.random() * PATHS.length)];
    url = `${TARGET}${path}`;
    headers = {
      'User-Agent': ua,
      'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
      'Accept-Language': 'en-US,en;q=0.9',
      'Accept-Encoding': 'gzip, deflate, br',
      'Sec-Fetch-Mode': 'navigate',
      'Sec-Fetch-Site': 'same-origin',
    };
    tag = 'human';
  } else if (roll < 0.85) {
    // Bot traffic
    const ua = BOT_UAS[Math.floor(Math.random() * BOT_UAS.length)];
    const path = PATHS[Math.floor(Math.random() * PATHS.length)];
    url = `${TARGET}${path}`;
    headers = { 'User-Agent': ua, 'Accept': '*/*' };
    tag = 'bot';
  } else if (roll < 0.95) {
    // Attack traffic
    const path = ATTACK_PATHS[Math.floor(Math.random() * ATTACK_PATHS.length)];
    url = `${TARGET}${path}`;
    headers = { 'User-Agent': 'python-requests/2.31.0', 'Accept': '*/*' };
    tag = 'attack';
  } else {
    // Honeypot probing
    const path = HONEYPOT_PATHS[Math.floor(Math.random() * HONEYPOT_PATHS.length)];
    url = `${TARGET}${path}`;
    headers = { 'User-Agent': 'curl/8.4.0', 'Accept': '*/*' };
    tag = 'honeypot';
  }

  // Poison-guard: carry the debug key so the model does not learn from this synthetic flood.
  if (API_KEY) headers['X-SB-Api-Key'] = API_KEY;

  const res = http.get(url, { headers, tags: { traffic_type: tag } });

  // Track metrics
  const isError = res.status === 0 || res.status >= 500;
  errorRate.add(isError);

  const processingMs = res.headers['X-Bot-Detection-ProcessingMs'];
  if (processingMs) {
    detectionLatency.add(parseFloat(processingMs));
  }

  const isBot = res.headers['X-StyloBot-IsBot'] === 'true' ||
                res.status === 403;
  botDetectionRate.add(isBot);

  // Tiny sleep to prevent pure CPU spin
  sleep(0.01);
}

export function handleSummary(data) {
  // Output summary to both stdout and JSON file
  const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
  return {
    'stdout': textSummary(data),
    [`soak-results/plateau-${timestamp}.json`]: JSON.stringify(data, null, 2),
  };
}

function textSummary(data) {
  const metrics = data.metrics;
  const lines = [
    '\n═══════════════════════════════════════════════════════════',
    '  PLATEAU TEST RESULTS',
    '═══════════════════════════════════════════════════════════',
    `  Target: ${TARGET}`,
    `  Max RPS: ${MAX_RPS}`,
    `  Duration: ${(data.state?.testRunDurationMs / 1000 / 60).toFixed(1)} min`,
    '',
    `  Requests:    ${metrics.http_reqs?.values?.count || 0}`,
    `  Errors:      ${((metrics.request_errors?.values?.rate || 0) * 100).toFixed(1)}%`,
    `  p50 latency: ${(metrics.http_req_duration?.values?.['p(50)'] || 0).toFixed(0)}ms`,
    `  p95 latency: ${(metrics.http_req_duration?.values?.['p(95)'] || 0).toFixed(0)}ms`,
    `  p99 latency: ${(metrics.http_req_duration?.values?.['p(99)'] || 0).toFixed(0)}ms`,
    `  Max latency: ${(metrics.http_req_duration?.values?.max || 0).toFixed(0)}ms`,
    `  Bot detect:  ${((metrics.bot_detected?.values?.rate || 0) * 100).toFixed(1)}%`,
    '',
    `  Detection p50: ${(metrics.detection_latency_ms?.values?.['p(50)'] || 0).toFixed(0)}ms`,
    `  Detection p95: ${(metrics.detection_latency_ms?.values?.['p(95)'] || 0).toFixed(0)}ms`,
    '═══════════════════════════════════════════════════════════\n',
  ];
  return lines.join('\n');
}
