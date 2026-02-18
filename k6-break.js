// k6 Break Test for StyloBot Demo Stack
// Goal: Find the breaking point by ramping up traffic aggressively
// Run: k6 run k6-break.js

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

const errorRate = new Rate('errors');
const detectionTime = new Trend('detection_time_ms', true);
const reqsPerSec = new Counter('successful_reqs');

const BASE = __ENV.BASE_URL || 'http://localhost';

export const options = {
    scenarios: {
        // Ramp up aggressively to find breaking point
        break_test: {
            executor: 'ramping-arrival-rate',
            startRate: 10,
            timeUnit: '1s',
            preAllocatedVUs: 50,
            maxVUs: 500,
            stages: [
                { duration: '30s', target: 50 },   // Warm up
                { duration: '30s', target: 100 },   // Medium load
                { duration: '30s', target: 200 },   // Heavy load
                { duration: '30s', target: 400 },   // Very heavy
                { duration: '30s', target: 600 },   // Breaking point?
                { duration: '30s', target: 800 },   // Push harder
                { duration: '30s', target: 1000 },  // Extreme
                { duration: '30s', target: 50 },    // Cool down
            ],
        },
    },
    thresholds: {
        http_req_duration: ['p(95)<5000'],  // Generous: 5s p95
        errors: ['rate<0.20'],              // Allow 20% errors at peak
    },
};

// Mix of traffic patterns
const SCENARIOS = [
    { weight: 40, fn: humanBrowsing },
    { weight: 30, fn: botScraping },
    { weight: 15, fn: attackTraffic },
    { weight: 10, fn: credentialStuffing },
    { weight: 5, fn: dashboardPolling },
];

export default function () {
    const r = Math.random() * 100;
    let cumulative = 0;
    for (const s of SCENARIOS) {
        cumulative += s.weight;
        if (r < cumulative) {
            s.fn();
            return;
        }
    }
    humanBrowsing();
}

// ===== Human Browser =====
const HUMAN_UAS = [
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15',
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0',
];
const PATHS = ['/', '/about', '/features', '/pricing', '/docs', '/blog', '/contact', '/dashboard'];

function humanBrowsing() {
    const ua = HUMAN_UAS[Math.floor(Math.random() * HUMAN_UAS.length)];
    const path = PATHS[Math.floor(Math.random() * PATHS.length)];
    const res = http.get(`${BASE}${path}`, {
        headers: {
            'User-Agent': ua,
            'Accept': 'text/html,application/xhtml+xml',
            'Accept-Language': 'en-US,en;q=0.9',
            'Accept-Encoding': 'gzip, deflate, br',
            'Referer': 'https://www.google.com/',
        },
        tags: { scenario: 'human' },
    });
    recordMetrics(res);
    check(res, { 'status < 500': (r) => r.status < 500 });
}

// ===== Bot Scraping =====
const BOT_UAS = [
    'curl/8.4.0', 'python-requests/2.31.0', 'Go-http-client/2.0',
    'Scrapy/2.11.0', 'wget/1.21.4', 'Java/17.0.9', 'node-fetch/3.3.2',
    'Mozilla/5.0 (compatible; GPTBot/1.0; +https://openai.com/gptbot)',
    'Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)',
];

function botScraping() {
    const ua = BOT_UAS[Math.floor(Math.random() * BOT_UAS.length)];
    const path = PATHS[Math.floor(Math.random() * PATHS.length)];
    const res = http.get(`${BASE}${path}`, {
        headers: { 'User-Agent': ua, 'Accept': '*/*' },
        tags: { scenario: 'bot' },
    });
    recordMetrics(res);
    check(res, { 'status < 500': (r) => r.status < 500 });
}

// ===== Attack Traffic =====
const ATTACKS = [
    { path: '/search', qs: "?q=1' UNION SELECT username,password FROM users--" },
    { path: '/search', qs: '?q=<script>alert(1)</script>' },
    { path: '/download', qs: '?file=../../../etc/passwd' },
    { path: '/proxy', qs: '?url=http://169.254.169.254/latest/meta-data/' },
    { path: '/wp-admin/', qs: '' },
    { path: '/.env', qs: '' },
    { path: '/.git/config', qs: '' },
    { path: '/phpmyadmin/', qs: '' },
    { path: '/actuator/env', qs: '' },
    { path: '/render', qs: '?tpl={{7*7}}' },
];

function attackTraffic() {
    const attack = ATTACKS[Math.floor(Math.random() * ATTACKS.length)];
    const res = http.get(`${BASE}${attack.path}${attack.qs}`, {
        headers: { 'User-Agent': 'sqlmap/1.7.12', 'Accept': '*/*' },
        tags: { scenario: 'attack' },
    });
    recordMetrics(res);
    check(res, { 'status < 500': (r) => r.status < 500 });
}

// ===== Credential Stuffing =====
function credentialStuffing() {
    const loginPaths = ['/login', '/signin', '/auth', '/api/auth/login'];
    const path = loginPaths[Math.floor(Math.random() * loginPaths.length)];
    const res = http.post(`${BASE}${path}`, JSON.stringify({
        username: `user${Math.floor(Math.random() * 10000)}@example.com`,
        password: 'password123',
    }), {
        headers: {
            'User-Agent': 'python-requests/2.31.0',
            'Content-Type': 'application/json',
        },
        tags: { scenario: 'credential_stuffing' },
    });
    recordMetrics(res);
    check(res, { 'status < 500': (r) => r.status < 500 });
}

// ===== Dashboard Polling =====
const DASHBOARD_ENDPOINTS = [
    '/_stylobot/api/summary',
    '/_stylobot/api/timeseries?bucket=60',
    '/_stylobot/api/detections?limit=20',
    '/_stylobot/api/topbots?count=10',
    '/_stylobot/api/countries?count=20',
];

function dashboardPolling() {
    const endpoint = DASHBOARD_ENDPOINTS[Math.floor(Math.random() * DASHBOARD_ENDPOINTS.length)];
    const res = http.get(`${BASE}${endpoint}`, {
        headers: {
            'Accept': 'application/json',
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36',
        },
        tags: { scenario: 'dashboard' },
    });
    recordMetrics(res);
    check(res, {
        'dashboard: status 200': (r) => r.status === 200,
        'dashboard: has body': (r) => r.body && r.body.length > 2,
    });
}

function recordMetrics(res) {
    errorRate.add(res.status >= 500 ? 1 : 0);
    if (res.status < 500) reqsPerSec.add(1);
    const procTime = parseFloat(res.headers['X-Bot-Detection-ProcessingMs'] || res.headers['X-Bot-Processing-Ms'] || '0');
    if (procTime > 0) detectionTime.add(procTime);
}
